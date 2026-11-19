using System.Text;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Encryption;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using DSC.TLink.ITv2.Protocol;

namespace DSC.TLink.Tests;

/// <summary>
/// Drives the pure connection core synchronously. The handshake is the real captured one;
/// after that the test plays the panel with synthetic packets, encrypted with the keys the core
/// derived. Time only moves when the test says so.
/// </summary>
internal sealed class CoreHarness
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string AccessCode = "000102030405060708090A0B0C0D0E0F";
    private long _nextId = 1;
    private byte _panelSequence = 50;

    public ConnectionState State { get; private set; }
    public DateTimeOffset Now { get; private set; } = T0;
    public List<ITv2Packet> Sent { get; } = [];
    public List<IMessageData> Published { get; } = [];
    public Dictionary<RequestId, Result<IMessageData>> Results { get; } = [];
    public List<string> Closed { get; } = [];
    public List<ConnectionEffect> Effects { get; } = [];
    public ConnectionCredentials? Credentials { get; set; } = new(EncryptionType.Type2, null, AccessCode);

    public CoreHarness(ConnectionTimings? timings = null)
    {
        State = Connection.Create(T0, new ConnectionConfig(timings ?? ConnectionTimings.Default, new byte[32]));
    }

    public ConnectionTimings Timings => State.Config.Timings;

    /// <summary>Runs the captured handshake (panel side) up to the burst.</summary>
    public CoreHarness Handshake()
    {
        var records = CapturedTraffic.Load(Path.Combine(CapturedTraffic.Directory, "connection-01.jsonl"));
        foreach (var r in records.Where(r => r.IsPacket && r.Dir == "rx"))
        {
            if (State.Phase >= Phase.Bursting)
                break;
            var (sender, receiver, message) = CapturedTraffic.Decode(r.FrameBytes);
            _panelSequence = sender;
            Receive(new ITv2Packet(sender, receiver, message));
        }
        Assert.Equal(Phase.Bursting, State.Phase);
        Sent.Clear();
        return this;
    }

    /// <summary>Handshake, then the quiet period, so the session is ready.</summary>
    public CoreHarness Ready()
    {
        Handshake();
        Advance(Timings.ReadyQuietPeriod);
        Assert.Equal(Phase.Ready, State.Phase);
        return this;
    }

    public RequestId Request(IMessageData message)
    {
        var id = new RequestId(_nextId++);
        Step(new SendRequested(Now, id, message));
        return id;
    }

    public void Cancel(RequestId id) => Step(new CancelRequested(Now, id));

    /// <summary>The panel sends a packet (its own next sender sequence unless given).</summary>
    public void PanelSends(IMessageData message, byte? receiverSequence = null, byte? senderSequence = null)
    {
        var sender = senderSequence ?? unchecked(++_panelSequence);
        Receive(new ITv2Packet(sender, receiverSequence ?? State.LocalSequence, message));
    }

    /// <summary>The panel acks one of our packets.</summary>
    public void PanelAcks(ITv2Packet ours) => PanelSends(new SimpleAck(), ours.SenderSequence);

    public void PanelSendsGarbage()
        => Step(new FrameReceived(Now, Header, [0x01, 0x02, 0x03]));

    public void Advance(TimeSpan by)
    {
        // Deliver ticks at every deadline on the way, like the shell's timer would.
        var target = Now + by;
        while (Connection.NextDeadline(State) is { } due && due <= target)
        {
            Now = due > Now ? due : Now;
            Step(new Tick(Now));
            if (Connection.NextDeadline(State) == due)
                break;
        }
        Now = target;
        Step(new Tick(Now));
    }

    public void TransportCloses() => Step(new TransportClosed(Now));
    public void CloseRequested() => Step(new CloseRequested(Now));

    public ITv2Packet LastSent => Sent[^1];
    public IEnumerable<ITv2Packet> SentOfType<T>() => Sent.Where(p => p.Message is T);

    private static readonly byte[] Header = Encoding.ASCII.GetBytes("200000000001");

    private void Receive(ITv2Packet packet)
    {
        var plain = PacketCodec.Encode(packet);
        var payload = State.InboundKey is { } key ? ITv2Crypto.Encrypt(key, plain) : plain;
        Step(new FrameReceived(Now, Header, payload));
    }

    private void Step(ConnectionInput input)
    {
        var t = Connection.Step(State, input);
        State = t.State;
        foreach (var effect in t.Effects)
        {
            Effects.Add(effect);
            switch (effect)
            {
                case WriteFrame w: Sent.Add(w.Packet); break;
                case Publish p: Published.Add(p.Message); break;
                case CompleteRequest c: Results[c.Id] = c.Result; break;
                case CloseConnection c: Closed.Add(c.Reason); break;
                case ResolveCredentials: Step(new CredentialsResolved(Now, Credentials)); break;
            }
        }
    }
}
