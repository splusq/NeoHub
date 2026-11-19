using System.Text.Json;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Messages;

namespace DSC.TLink.Tests;

/// <summary>One line of a fixture file (see Fixtures/README.md).</summary>
public sealed record FixtureRecord(
    long Ms,
    string Kind,
    string? Dir,
    string? Type,
    string? Frame,
    bool? RoundTrips,
    bool? Redacted,
    bool? Dropped,
    string? Label,
    string? Request,
    string? Operation)
{
    public bool IsPacket => Kind == "packet" && Frame is not null;
    public byte[] FrameBytes => Convert.FromHexString(Frame!);
    public override string ToString() => $"{Ms}ms {Dir} {Type ?? Kind}";
}

/// <summary>Loads the sanitized captures of real panel traffic.</summary>
public static class CapturedTraffic
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static IEnumerable<string> Files()
        => System.IO.Directory.EnumerateFiles(Directory, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal);

    public static IReadOnlyList<FixtureRecord> Load(string file)
        => File.ReadLines(file).Select(line => JsonSerializer.Deserialize<FixtureRecord>(line, Options)!).ToList();

    public static IEnumerable<(string File, FixtureRecord Record)> AllPackets()
        => Files().SelectMany(f => Load(f).Where(r => r.IsPacket).Select(r => (Path.GetFileName(f), r)));

    /// <summary>The ITv2 packet layout used by ITv2Session.</summary>
    internal static (byte Sender, byte Receiver, IMessageData Message) Decode(byte[] frame)
    {
        ReadOnlySpan<byte> span = frame;
        ITv2Framing.RemoveFraming(ref span);
        return (span[0], span[1], MessageFactory.DeserializeMessage(span[2..]));
    }

    internal static byte[] Encode(byte sender, byte receiver, IMessageData message)
    {
        var bytes = new List<byte> { sender, receiver };
        bytes.AddRange(MessageFactory.SerializeMessage(message));
        ITv2Framing.AddFraming(bytes);
        return bytes.ToArray();
    }

    internal static string TypeName(IMessageData message)
        => message is DefaultMessage dm ? $"DefaultMessage({dm.Command})" : message.GetType().Name;
}
