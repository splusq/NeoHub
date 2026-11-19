using System.Text;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Encryption;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using DSC.TLink.ITv2.Protocol;

namespace DSC.TLink.Tests;

/// <summary>
/// Replays real panel traffic through the pure connection core and checks it sends exactly
/// what NeoHub sent to the real panel: every byte of every packet, in order.
///
/// Inbound packets are encrypted by the test with the keys the core derives (as the panel
/// did), so decryption is exercised too. Application requests (configuration reads, arm,
/// disarm, bypass, user reads) are fed in as requests at the time they were sent; acks,
/// handshake packets and heartbeat polls must come from the core on its own.
/// Fully deterministic: time comes from the fixture, there are no threads.
/// </summary>
public class ConnectionReplayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string PanelId = "200000000001";
    // Any valid Type 2 code works: the test encrypts inbound traffic with whatever keys the core derives.
    private const string AccessCode = "000102030405060708090A0B0C0D0E0F";

    public static TheoryData<string> FixtureFiles() => CodecGoldenTests.FixtureFiles();

    private static readonly HashSet<string> SentByTheCore =
        ["SimpleAck", "ConnectionPoll"];

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void CoreSendsExactlyWhatWasCaptured(string fixture)
    {
        var replay = Replay.Run(CapturedTraffic.Load(Path.Combine(CapturedTraffic.Directory, fixture)));

        var expected = replay.ExpectedTx;
        var actual = replay.ActualTx;
        for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            Assert.True(expected[i].Frame == actual[i].Frame,
                $"TX #{i} differs\n  captured {expected[i]}\n  core     {actual[i]}\n" +
                $"  previous: {(i > 0 ? expected[i - 1].ToString() : "-")}");
        }
        Assert.Equal(expected.Count, actual.Count);
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void EveryRequestCompletes_AndNothingTimesOut(string fixture)
    {
        var replay = Replay.Run(CapturedTraffic.Load(Path.Combine(CapturedTraffic.Directory, fixture)));

        Assert.Empty(replay.Outstanding);
        Assert.DoesNotContain(replay.Results.Values, r => r.IsFailure && r.Error!.Value.Code == TLinkErrorCode.Timeout);
        Assert.DoesNotContain(replay.Effects, e => e is CloseConnection);
        Assert.DoesNotContain(replay.Effects, e => e is Diagnostic { Level: Microsoft.Extensions.Logging.LogLevel.Warning });
    }

    /// <summary>
    /// The capture's duplicate "arm stay" from HomeKit: the panel rejects the second with
    /// FunctionNotAvailable, and the core must hand that answer to the second request.
    /// </summary>
    [Fact]
    public void DuplicateArm_GetsThePanelsRejection()
    {
        var replay = Replay.Run(CapturedTraffic.Load(Path.Combine(CapturedTraffic.Directory, "connection-01.jsonl")));

        var arms = replay.Requests.Where(r => r.Message is PartitionArm).Select(r => replay.Results[r.Id]).ToList();
        Assert.Equal(2, arms.Count);
        Assert.True(arms[0].IsSuccess);
        var second = Assert.IsType<CommandResponse>(arms[1].Value);
        Assert.Equal(CommandResponseCode.FunctionNotAvailable, second.ResponseCode);
    }

    /// <summary>
    /// Every packet received is reported, decrypted, as the captured frame bytes, so the
    /// "RX bytes" trace log (which CaptureTool reads) shows exactly what the panel sent.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void EveryReceivedPacket_IsReportedAsTheCapturedFrame(string fixture)
    {
        var records = CapturedTraffic.Load(Path.Combine(CapturedTraffic.Directory, fixture));
        var replay = Replay.Run(records);

        var captured = records.Where(r => r.IsPacket && r.Dir == "rx").Select(r => r.Frame!).ToList();
        var reported = replay.Effects.OfType<FrameDecoded>().Select(d => Convert.ToHexString(d.Plaintext)).ToList();
        Assert.Equal(captured, reported);
    }

    [Fact]
    public void ZoneChanges_ArePublished_InOrder()
    {
        var replay = Replay.Run(CapturedTraffic.Load(Path.Combine(CapturedTraffic.Directory, "connection-01.jsonl")));

        var zones = replay.Effects.OfType<Publish>().Select(p => p.Message).OfType<NotificationLifestyleZoneStatus>()
            .Select(z => $"{z.ZoneNumber}:{z.Status}").ToList();

        Assert.Equal(["1:Open", "1:Restored", "9:Open", "9:Restored", "1:Open", "1:Restored"], zones.Take(6));
    }

    internal sealed record TxFrame(long Ms, string Type, string Frame)
    {
        public override string ToString() => $"{Ms}ms {Type} {Frame}";
    }

    internal sealed class Replay
    {
        public List<TxFrame> ExpectedTx { get; } = [];
        public List<TxFrame> ActualTx { get; } = [];
        public List<ConnectionEffect> Effects { get; } = [];
        public List<(RequestId Id, IMessageData Message)> Requests { get; } = [];
        public Dictionary<RequestId, Result<IMessageData>> Results { get; } = [];
        public IEnumerable<RequestId> Outstanding => Requests.Select(r => r.Id).Where(id => !Results.ContainsKey(id));

        public static Replay Run(IReadOnlyList<FixtureRecord> records)
        {
            var replay = new Replay();
            var packets = records.Where(r => r.IsPacket).ToList();

            // Our RequestAccess initializer is whatever NeoHub actually sent (first 16 bytes for Type 2).
            var ourRequestAccess = packets.First(r => r.Dir == "tx" && r.Type == "RequestAccess");
            var initializer = ((RequestAccess)CapturedTraffic.Decode(ourRequestAccess.FrameBytes).Message).Initializer;
            var random = initializer.Concat(new byte[ConnectionConfig.LocalRandomLength - initializer.Length]).ToArray();

            var state = Connection.Create(T0, new ConnectionConfig(ConnectionTimings.Default, random));
            var established = false;
            long nextId = 1;
            long lastMs = 0;

            void Apply(ConnectionInput input, long ms)
            {
                var transition = Connection.Step(state, input);
                state = transition.State;
                foreach (var effect in transition.Effects)
                {
                    replay.Effects.Add(effect);
                    switch (effect)
                    {
                        case WriteFrame w:
                            replay.ActualTx.Add(new TxFrame(ms, w.Packet.Message.GetType().Name, Convert.ToHexString(w.Plaintext)));
                            Assert.Equal(PacketCodec.Protect(w.Plaintext, state.OutboundKey), w.Wire);
                            break;
                        case ResolveCredentials:
                            Apply(new CredentialsResolved(input.Now,
                                new ConnectionCredentials(EncryptionType.Type2, null, AccessCode)), ms);
                            break;
                        case CompleteRequest c:
                            replay.Results[c.Id] = c.Result;
                            break;
                        case SessionEstablished:
                            established = true;
                            break;
                    }
                }
            }

            foreach (var record in records)
            {
                lastMs = record.Ms;
                var now = T0.AddMilliseconds(record.Ms);
                if (!record.IsPacket)
                    continue;

                if (record.Dir == "rx")
                {
                    var frame = record.FrameBytes;
                    var payload = state.InboundKey is { } key ? ITv2Crypto.Encrypt(key, frame) : frame;
                    Apply(new FrameReceived(now, Encoding.ASCII.GetBytes(PanelId), payload), record.Ms);
                    continue;
                }

                replay.ExpectedTx.Add(new TxFrame(record.Ms, record.Type!, record.Frame!));

                // Handshake packets, acks and heartbeat polls come from the core itself; just let time pass.
                if (!established || SentByTheCore.Contains(record.Type!))
                {
                    Apply(new Tick(now), record.Ms);
                    continue;
                }

                // Anything else was an application request: hand the core the same message.
                var message = CapturedTraffic.Decode(record.FrameBytes).Message;
                var id = new RequestId(nextId++);
                replay.Requests.Add((id, message));
                Apply(new SendRequested(now, id, message), record.Ms);
            }

            return replay;
        }
    }
}
