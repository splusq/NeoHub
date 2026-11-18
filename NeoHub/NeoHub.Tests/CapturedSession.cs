using System.Text;
using DSC.TLink.ITv2.Encryption;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using DSC.TLink.ITv2.Protocol;
using DSC.TLink.Tests;

namespace NeoHub.Tests;

/// <summary>
/// Runs a captured connection through the real protocol core and returns what the core
/// published to the application, with arrival times, plus the human markers. Pure and
/// synchronous: the same file always gives the same stream.
/// </summary>
internal static class CapturedSession
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string AccessCode = "000102030405060708090A0B0C0D0E0F";

    public sealed record Item(long Ms, IMessageData? Message, string? Marker)
    {
        public DateTimeOffset At => T0.AddMilliseconds(Ms);
    }

    public static IReadOnlyList<Item> Run(string fixture = "connection-01.jsonl")
    {
        var records = CapturedTraffic.Load(Path.Combine(CapturedTraffic.Directory, fixture));
        var packets = records.Where(r => r.IsPacket).ToList();
        var ourRequestAccess = packets.First(r => r.Dir == "tx" && r.Type == "RequestAccess");
        var initializer = ((RequestAccess)CapturedTraffic.Decode(ourRequestAccess.FrameBytes).Message).Initializer;
        var random = initializer.Concat(new byte[ConnectionConfig.LocalRandomLength - initializer.Length]).ToArray();

        var state = Connection.Create(T0, new ConnectionConfig(ConnectionTimings.Default, random));
        var output = new List<Item>();
        var established = false;
        long nextId = 1;

        void Apply(ConnectionInput input, long ms)
        {
            var t = Connection.Step(state, input);
            state = t.State;
            foreach (var effect in t.Effects)
            {
                switch (effect)
                {
                    case Publish p: output.Add(new Item(ms, p.Message, null)); break;
                    case SessionEstablished: established = true; break;
                    case ResolveCredentials:
                        Apply(new CredentialsResolved(input.Now, new ConnectionCredentials(EncryptionType.Type2, null, AccessCode)), ms);
                        break;
                }
            }
        }

        foreach (var r in records)
        {
            var now = T0.AddMilliseconds(r.Ms);
            if (r.Kind == "marker")
            {
                output.Add(new Item(r.Ms, null, r.Label));
                continue;
            }
            if (!r.IsPacket)
                continue;
            if (r.Dir == "rx")
            {
                var frame = r.FrameBytes;
                Apply(new FrameReceived(now, Encoding.ASCII.GetBytes("200000000001"),
                    state.InboundKey is { } key ? ITv2Crypto.Encrypt(key, frame) : frame), r.Ms);
            }
            else if (!established || r.Type is "SimpleAck" or "ConnectionPoll")
                Apply(new Tick(now), r.Ms);
            else
                Apply(new SendRequested(now, new RequestId(nextId++), CapturedTraffic.Decode(r.FrameBytes).Message), r.Ms);
        }
        return output;
    }
}
