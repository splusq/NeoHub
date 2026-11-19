namespace CaptureTool;

internal static class Commands
{
    public static IReadOnlyList<CaptureEvent> Load(IEnumerable<string> files)
        => files.SelectMany(File.ReadLines).Pipe(CaptureLog.Read).OrderBy(e => e.At).ToList();

    /// <summary>
    /// Lists every message type in the capture, and checks each packet re-encodes to exactly
    /// the captured bytes. Redaction re-encodes edited messages, so it is only safe for types
    /// that round-trip.
    /// </summary>
    public static int Analyze(string[] files)
    {
        var events = Load(files);
        var packets = events.OfType<PacketEvent>().ToList();
        Console.WriteLine($"{events.Count} events, {packets.Count} packets, " +
                          $"{events.OfType<ConnectEvent>().Count()} connects, {events.OfType<ClientRequestEvent>().Count()} client requests");

        var stats = new SortedDictionary<string, (int Count, int RoundTrip, int Failed)>();
        int decodeErrors = 0;
        var shown = new HashSet<string>();
        foreach (var p in packets)
        {
            DecodedPacket d;
            try { d = PacketCodec.Decode(p.Plaintext); }
            catch (Exception ex)
            {
                decodeErrors++;
                Console.WriteLine($"  decode error at {p.At:O} {p.Direction}: {ex.Message}");
                continue;
            }

            var key = $"{p.Direction} {PacketCodec.TypeName(d.Message)}";
            stats.TryGetValue(key, out var s);
            bool ok;
            try { ok = PacketCodec.Encode(d.SenderSequence, d.ReceiverSequence, d.Message).AsSpan().SequenceEqual(d.Frame); }
            catch { ok = false; }
            stats[key] = (s.Count + 1, s.RoundTrip + (ok ? 1 : 0), s.Failed + (ok ? 0 : 1));
            if (!ok && Environment.GetEnvironmentVariable("SHOW_DIFF") == "1" && shown.Add(key))
            {
                byte[] re;
                try { re = PacketCodec.Encode(d.SenderSequence, d.ReceiverSequence, d.Message); } catch (Exception ex) { Console.WriteLine($"  {key}: encode threw {ex.Message}"); continue; }
                Console.WriteLine($"  {key}\n    captured  {Convert.ToHexString(d.Frame)}\n    re-encode {Convert.ToHexString(re)}");
                if (d.Message is DSC.TLink.ITv2.Messages.MultipleMessagePacket mm)
                    Console.WriteLine($"    contains: {string.Join(", ", mm.Messages.Select(PacketCodec.TypeName))}");
            }
        }

        Console.WriteLine($"decode errors: {decodeErrors}");
        Console.WriteLine($"{"type",-58} {"count",6} {"round-trip",10}");
        foreach (var (key, s) in stats)
            Console.WriteLine($"{key,-58} {s.Count,6} {(s.Failed == 0 ? "ok" : $"{s.Failed} FAIL"),10}");
        return 0;
    }

    /// <summary>
    /// Writes one JSONL fixture per panel connection. Times are milliseconds from the start of
    /// that connection, so no wall-clock dates leave the private capture. Packets are kept as
    /// captured, except messages with secrets, which are re-encoded with the secrets replaced.
    /// Ends with an audit; a single leaked secret fails the run and deletes the output.
    /// </summary>
    public static int Fixtures(string outDir, string markersFile, string[] files)
    {
        var events = Load(files);
        var markers = markersFile == "-" ? [] : Markers.Read(markersFile);
        var redactor = new Redactor();
        Directory.CreateDirectory(outDir);

        var written = new List<string>();
        var connections = Split(events);
        foreach (var (connection, index) in connections.Select((c, i) => (c, i)))
        {
            var start = connection[0].At;
            var path = Path.Combine(outDir, $"connection-{index + 1:00}.jsonl");
            using var writer = new StreamWriter(path) { NewLine = "\n" };
            var stats = new FixtureStats();

            foreach (var record in Records(connection, markers, start, redactor, stats))
                writer.WriteLine(record);

            Console.WriteLine($"{path}: {stats}");
            written.Add(path);
        }

        var leaks = Audit.Run(written, redactor);
        if (leaks.Count > 0)
        {
            foreach (var leak in leaks.Take(20))
                Console.Error.WriteLine($"LEAK: {leak}");
            foreach (var path in written)
                File.Delete(path);
            Console.Error.WriteLine($"{leaks.Count} leak(s); fixtures deleted.");
            return 1;
        }

        Console.WriteLine($"audit: clean ({redactor.SecretStrings.Count} distinct secrets checked)");
        return 0;
    }

    /// <summary>Splits the event stream at each panel connect; events before the first connect are dropped.</summary>
    private static List<List<CaptureEvent>> Split(IReadOnlyList<CaptureEvent> events)
    {
        var result = new List<List<CaptureEvent>>();
        foreach (var e in events)
        {
            if (e is ConnectEvent)
                result.Add([e]);
            else if (result.Count > 0)
                result[^1].Add(e);
        }
        return result;
    }

    private static IEnumerable<string> Records(
        List<CaptureEvent> connection, IReadOnlyList<Marker> markers, DateTimeOffset start,
        Redactor redactor, FixtureStats stats)
    {
        var end = connection[^1].At;
        var stream = connection
            .Concat(markers.Where(m => m.At >= start && m.At <= end))
            .OrderBy(e => e.At)
            .ToList();

        foreach (var e in stream)
        {
            var ms = (long)Math.Round((e.At - start).TotalMilliseconds);
            switch (e)
            {
                case PacketEvent p:
                    yield return Packet(p, ms, redactor, stats);
                    break;
                case ConnectEvent:
                    yield return Json.Line(new { ms, kind = "connect" });
                    break;
                case DisconnectEvent:
                    yield return Json.Line(new { ms, kind = "disconnect" });
                    break;
                case ClientRequestEvent c:
                    yield return Json.Line(new { ms, kind = "client", request = c.Request });
                    break;
                case OperationEvent o:
                    yield return Json.Line(new { ms, kind = "operation", operation = o.Operation });
                    break;
                case Marker m:
                    yield return Json.Line(new { ms, kind = "marker", label = m.Label });
                    break;
            }
        }
    }

    private static string Packet(PacketEvent p, long ms, Redactor redactor, FixtureStats stats)
    {
        var dir = p.Direction == Direction.Rx ? "rx" : "tx";
        var decoded = PacketCodec.Decode(p.Plaintext);
        var roundTrips = Safe(() => PacketCodec.Encode(decoded.SenderSequence, decoded.ReceiverSequence, decoded.Message)
            .AsSpan().SequenceEqual(decoded.Frame));
        var (verdict, message) = redactor.Redact(decoded.Message);
        var type = PacketCodec.TypeName(decoded.Message);

        switch (verdict)
        {
            case Redactor.Verdict.Keep:
                stats.Kept++;
                return Json.Line(new { ms, kind = "packet", dir, type, frame = Convert.ToHexString(decoded.Frame), roundTrips });

            case Redactor.Verdict.Rewritten when roundTrips:
                stats.Redacted++;
                var frame = PacketCodec.Encode(decoded.SenderSequence, decoded.ReceiverSequence, message);
                return Json.Line(new { ms, kind = "packet", dir, type, frame = Convert.ToHexString(frame), roundTrips, redacted = (bool?)true });

            default:
                // Unknown type, or a secret-bearing message the codec can't re-encode faithfully:
                // keep its place (sequence numbers stay visible) but not its content.
                stats.Dropped++;
                return Json.Line(new { ms, kind = "packet", dir, type, dropped = (bool?)true,
                    senderSeq = decoded.SenderSequence, receiverSeq = decoded.ReceiverSequence });
        }
    }

    private static bool Safe(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }

    private sealed class FixtureStats
    {
        public int Kept, Redacted, Dropped;
        public override string ToString() => $"{Kept} kept, {Redacted} redacted, {Dropped} dropped";
    }
}

internal static class PipeExtensions
{
    public static TOut Pipe<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value);
}
