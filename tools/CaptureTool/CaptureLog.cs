using System.Globalization;
using System.Text.RegularExpressions;

namespace CaptureTool;

/// <summary>One line of interest from a NeoHub Trace/Debug capture (docker logs --timestamps).</summary>
internal abstract record CaptureEvent(DateTimeOffset At);

/// <summary>A decrypted ITv2 packet as logged by ITv2Session ("RX bytes" / "TX bytes").</summary>
internal sealed record PacketEvent(DateTimeOffset At, Direction Direction, byte[] Plaintext) : CaptureEvent(At);

/// <summary>A panel opened a TLink connection.</summary>
internal sealed record ConnectEvent(DateTimeOffset At) : CaptureEvent(At);

/// <summary>The TLink connection ended.</summary>
internal sealed record DisconnectEvent(DateTimeOffset At) : CaptureEvent(At);

/// <summary>A WebSocket client request (Homebridge / Home Assistant), e.g. ArmHomeMessage.</summary>
internal sealed record ClientRequestEvent(DateTimeOffset At, string Request) : CaptureEvent(At);

/// <summary>A command started from the NeoHub web UI or a service (bypass, user read, ...).</summary>
internal sealed record OperationEvent(DateTimeOffset At, string Operation) : CaptureEvent(At);

internal enum Direction { Rx, Tx }

/// <summary>
/// Reads `docker logs --timestamps` output from NeoHub's console logger. Each log entry is a
/// header line ("TS level: Category[id]") followed by one or more message lines ("TS   text").
/// Only the message lines matter here; they are matched by content.
/// </summary>
internal static partial class CaptureLog
{
    [GeneratedRegex(@"^(?<ts>\d{4}-\d\d-\d\dT[\d:.]+Z)\s+(?<msg>.*)$")]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"^(?<dir>RX|TX) bytes: \[(?<hex>[0-9A-F-]*)\]$")]
    private static partial Regex PacketPattern();

    [GeneratedRegex(@"^Client [0-9a-f]+ request: (?<req>\w+)$")]
    private static partial Regex ClientRequestPattern();

    [GeneratedRegex(@"^(?<op>Bypass command: Partition=\d+, Zone=\d+, Bypass=(True|False))")]
    private static partial Regex BypassPattern();

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiPattern();

    public static IEnumerable<CaptureEvent> Read(IEnumerable<string> lines)
    {
        // The capture loop resumes with --since <last timestamp>, which can repeat the last line.
        string? previous = null;
        foreach (var raw in lines)
        {
            var line = AnsiPattern().Replace(raw, "");
            if (line == previous)
                continue;
            previous = line;

            var m = LinePattern().Match(line);
            if (!m.Success)
                continue;

            var at = DateTimeOffset.Parse(m.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var msg = m.Groups["msg"].Value.Trim();

            var packet = PacketPattern().Match(msg);
            if (packet.Success)
            {
                yield return new PacketEvent(at,
                    packet.Groups["dir"].Value == "RX" ? Direction.Rx : Direction.Tx,
                    ParseHex(packet.Groups["hex"].Value));
                continue;
            }

            if (msg.StartsWith("Connection request from ", StringComparison.Ordinal))
                yield return new ConnectEvent(at);
            else if (msg is "Transport completed" or "Received END_SESSION from panel, shutting down")
                yield return new DisconnectEvent(at);
            else if (ClientRequestPattern().Match(msg) is { Success: true } req)
                yield return new ClientRequestEvent(at, req.Groups["req"].Value);
            else if (BypassPattern().Match(msg) is { Success: true } op)
                yield return new OperationEvent(at, op.Groups["op"].Value);
            else if (msg.StartsWith("Reading users", StringComparison.Ordinal) || msg.StartsWith("Starting panel configuration pull", StringComparison.Ordinal))
                yield return new OperationEvent(at, msg);
        }
    }

    public static byte[] ParseHex(string dashed)
        => dashed.Length == 0 ? [] : Convert.FromHexString(dashed.Replace("-", ""));
}
