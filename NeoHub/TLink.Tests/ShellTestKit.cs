using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Encryption;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using DSC.TLink.ITv2.Protocol;
using DSC.TLink.ITv2.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DSC.TLink.Tests;

/// <summary>A transport made of two channels. Detects overlapping writes.</summary>
internal sealed class ChannelTransport : ITLinkTransport
{
    private readonly Channel<Result<TLinkMessage>> _inbound = Channel.CreateUnbounded<Result<TLinkMessage>>();
    private int _writing;

    public Channel<byte[]> Written { get; } = Channel.CreateUnbounded<byte[]>();
    public bool OverlappingWriteSeen { get; private set; }
    public bool Disposed { get; private set; }
    public byte[] Header { get; init; } = Encoding.ASCII.GetBytes("200000000001");
    public ReadOnlyMemory<byte> DefaultHeader => Header;

    public void Deliver(byte[] payload) => _inbound.Writer.TryWrite(new TLinkMessage(Header, payload));
    public void DeliverFailure(TLinkErrorCode code) => _inbound.Writer.TryWrite(Result<TLinkMessage>.Fail(code, "injected"));
    public void Hangup() => _inbound.Writer.TryComplete();

    public Task<Result<TLinkMessage>> ReadMessageAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public async IAsyncEnumerable<Result<TLinkMessage>> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _inbound.Reader.ReadAllAsync(ct))
            yield return item;
    }

    public Task<Result> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        // The connection writes from a single loop; two writers would show up here.
        if (++_writing > 1)
            OverlappingWriteSeen = true;
        Written.Writer.TryWrite(payload.ToArray());
        _writing--;
        return Task.FromResult(Result.Ok());
    }

    public Task<Result> SendAsync(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => SendAsync(payload, ct);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Plays the panel against a live <see cref="PanelConnection"/>: the real captured handshake,
/// then synthetic packets. Encrypts what it sends and decrypts what it receives with the same
/// keys a real panel would derive. Waits only on what the connection actually writes.
/// </summary>
internal sealed class PanelSimulator : IAsyncDisposable
{
    public static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);   // hang guard only; never a timing assertion
    private const string AccessCode = "000102030405060708090A0B0C0D0E0F";
    private static readonly byte[] LocalRandom = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();

    private byte[]? _toPanelKey;    // what the connection encrypts with (its outbound key)
    private byte[]? _fromPanelKey;  // what we encrypt with (the connection's inbound key)
    private byte _sequence = 50;

    public FakeTimeProvider Time { get; }
    public ChannelTransport Transport { get; }
    public PanelConnection Connection { get; }
    public ConnectionTimings Timings { get; }
    public string PanelId { get; }

    /// <param name="connectionClock">The clock the connection itself sees, when it should differ from
    /// <see cref="Time"/> (e.g. <see cref="EarlyTimers"/>); the test still drives <see cref="Time"/>.</param>
    public PanelSimulator(FakeTimeProvider? time = null, ConnectionTimings? timings = null, bool knownPanel = true, string panelId = "200000000001",
        TimeProvider? connectionClock = null)
    {
        Time = time ?? new FakeTimeProvider();
        Timings = timings ?? ConnectionTimings.Default;
        PanelId = panelId;
        Transport = new ChannelTransport { Header = Encoding.ASCII.GetBytes(panelId) };
        Connection = PanelConnection.Start(
            Transport,
            (_, type) => knownPanel ? new ConnectionCredentials(type, null, AccessCode) : null,
            connectionClock ?? Time,
            new ConnectionConfig(Timings, LocalRandom),
            NullLogger.Instance);
    }

    /// <summary>Runs the captured handshake. Each panel packet produces exactly one reply.</summary>
    public async Task HandshakeAsync()
    {
        var records = CapturedTraffic.Load(Path.Combine(CapturedTraffic.Directory, "connection-01.jsonl"));
        var accessCode = ITv2Crypto.Type2.ParseAccessCode(AccessCode);
        foreach (var r in records.Where(r => r.IsPacket && r.Dir == "rx").Take(6))
        {
            var (sender, _, message) = CapturedTraffic.Decode(r.FrameBytes);
            _sequence = sender;
            if (message is RequestAccess panelRequest)
                _toPanelKey = ITv2Crypto.Type2.OutboundKey(accessCode, panelRequest.Initializer);
            Transport.Deliver(Protect(r.FrameBytes));
            var reply = await ReceiveAsync();
            if (reply.Message is RequestAccess ours)
                _fromPanelKey = ITv2Crypto.Type2.InboundKey(accessCode, ours.Initializer);
        }
        Assert.Equal(PanelId, await Connection.Established.WaitAsync(Guard));
    }

    /// <summary>Handshake, then the quiet period on the fake clock: the session is ready.</summary>
    public async Task ReadyAsync()
    {
        await HandshakeAsync();
        await AdvanceAsync(Timings.ReadyQuietPeriod);
    }

    /// <summary>Moves the fake clock once the connection has scheduled everything it had to.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        await Connection.QuiesceAsync().WaitAsync(Guard);
        Time.Advance(by);
        await Connection.QuiesceAsync().WaitAsync(Guard);
    }

    /// <summary>The next packet the connection writes, decrypted.</summary>
    public async Task<ITv2Packet> ReceiveAsync()
    {
        var wire = await Transport.Written.Reader.ReadAsync().AsTask().WaitAsync(Guard);
        var decoded = PacketCodec.Decode(wire, _toPanelKey);
        Assert.True(decoded.IsSuccess, decoded.Error?.Message);
        return decoded.Value;
    }

    /// <summary>Reads packets until one of type T, acking nothing; returns it.</summary>
    public async Task<ITv2Packet> ReceiveAsync<T>()
    {
        while (true)
        {
            var packet = await ReceiveAsync();
            if (packet.Message is T)
                return packet;
        }
    }

    public void Send(IMessageData message, byte receiverSequence)
        => Transport.Deliver(Protect(PacketCodec.Encode(new ITv2Packet(unchecked(++_sequence), receiverSequence, message))));

    public void Ack(ITv2Packet ours) => Send(new SimpleAck(), ours.SenderSequence);

    public void Respond(ITv2Packet ours, CommandResponseCode code = CommandResponseCode.Success)
        => Send(new CommandResponse { CommandSequence = ((ICommandMessage)ours.Message).CommandSequence, ResponseCode = code }, ours.SenderSequence);

    private byte[] Protect(byte[] frame) => _fromPanelKey is { } key ? ITv2Crypto.Encrypt(key, frame) : frame;

    public async ValueTask DisposeAsync()
    {
        Transport.Hangup();
        await Connection.Completion.WaitAsync(Guard);
    }
}

/// <summary>
/// A clock whose timers fire 1 ms before they are due, as a real timer can relative to the
/// wall clock: System.Threading.Timer counts whole milliseconds on the monotonic clock, while
/// deadlines are compared with GetUtcNow. Time itself is <paramref name="inner"/>'s.
/// </summary>
internal sealed class EarlyTimers(FakeTimeProvider inner) : TimeProvider
{
    private static readonly TimeSpan Early = TimeSpan.FromMilliseconds(1);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
    public override long GetTimestamp() => inner.GetTimestamp();
    public override long TimestampFrequency => inner.TimestampFrequency;
    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => new Timer(inner.CreateTimer(callback, state, Shift(dueTime), period));

    private static TimeSpan Shift(TimeSpan due) => due > Early && due != Timeout.InfiniteTimeSpan ? due - Early : due;

    private sealed class Timer(ITimer timer) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(Shift(dueTime), period);
        public void Dispose() => timer.Dispose();
        public ValueTask DisposeAsync() => timer.DisposeAsync();
    }
}
