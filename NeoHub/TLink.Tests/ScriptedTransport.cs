using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Messages;

namespace DSC.TLink.Tests;

/// <summary>Encodes and decodes unencrypted ITv2 packets the way ITv2Session does.</summary>
internal static class Wire
{
    public static byte[] Encode(byte senderSequence, byte receiverSequence, IMessageData message)
    {
        var bytes = new List<byte> { senderSequence, receiverSequence };
        bytes.AddRange(MessageFactory.SerializeMessage(message));
        ITv2Framing.AddFraming(bytes);
        return bytes.ToArray();
    }

    public static SentPacket Decode(ReadOnlyMemory<byte> payload)
    {
        ReadOnlySpan<byte> span = payload.Span;
        ITv2Framing.RemoveFraming(ref span);
        var sender = span[0];
        var receiver = span[1];
        var message = MessageFactory.DeserializeMessage(span[2..]);
        return new SentPacket(sender, receiver, message);
    }
}

internal sealed record SentPacket(byte SenderSequence, byte ReceiverSequence, IMessageData Message);

/// <summary>
/// An in-memory panel. Records every packet the session writes, detects overlapping writes,
/// and lets a test inject inbound packets or answer requests through <see cref="OnSend"/>.
/// </summary>
internal sealed class ScriptedTransport : ITLinkTransport
{
    private readonly Channel<Result<TLinkMessage>> _inbound = Channel.CreateUnbounded<Result<TLinkMessage>>();
    private int _sendsInFlight;
    private int _maxSendsInFlight;
    private byte _panelSequence = 100;

    public ConcurrentQueue<SentPacket> Sent { get; } = new();

    /// <summary>
    /// Called (on the thread pool, after the write completes) for every packet the session
    /// writes. Use it to answer requests. Exceptions fail the test via <see cref="CallbackErrors"/>.
    /// </summary>
    public Func<SentPacket, ScriptedTransport, Task>? OnSend { get; set; }

    public ConcurrentQueue<Exception> CallbackErrors { get; } = new();

    /// <summary>
    /// How long each write takes. A nonzero delay widens the window in which two
    /// unsynchronized writers would overlap.
    /// </summary>
    public TimeSpan SendDelay { get; set; } = TimeSpan.FromMilliseconds(1);

    public int MaxConcurrentSends => Volatile.Read(ref _maxSendsInFlight);
    public int DisposeCount;

    public ReadOnlyMemory<byte> DefaultHeader { get; } = "200000000001"u8.ToArray();

    public Task<Result<TLinkMessage>> ReadMessageAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The session reads through ReadAllAsync once started");

    public async IAsyncEnumerable<Result<TLinkMessage>> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _inbound.Reader.ReadAllAsync(cancellationToken))
            yield return item;
    }

    public Task<Result> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => SendAsync(DefaultHeader, payload, cancellationToken);

    public async Task<Result> SendAsync(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var inFlight = Interlocked.Increment(ref _sendsInFlight);
        int max;
        while (inFlight > (max = Volatile.Read(ref _maxSendsInFlight))
               && Interlocked.CompareExchange(ref _maxSendsInFlight, inFlight, max) != max) { }

        SentPacket packet;
        try
        {
            if (SendDelay > TimeSpan.Zero)
                await Task.Delay(SendDelay, CancellationToken.None);
            packet = Wire.Decode(payload);
            Sent.Enqueue(packet);
        }
        finally
        {
            Interlocked.Decrement(ref _sendsInFlight);
        }

        if (OnSend is { } onSend)
        {
            _ = Task.Run(async () =>
            {
                try { await onSend(packet, this); }
                catch (Exception ex) { CallbackErrors.Enqueue(ex); }
            });
        }

        return Result.Ok();
    }

    /// <summary>Delivers a packet from the panel. Returns the panel's sender sequence used.</summary>
    public byte Inject(IMessageData message, byte receiverSequence = 0)
    {
        var sequence = unchecked(++_panelSequence);
        _inbound.Writer.TryWrite(new TLinkMessage(DefaultHeader, Wire.Encode(sequence, receiverSequence, message)));
        return sequence;
    }

    /// <summary>Acknowledges one of the session's packets.</summary>
    public void Ack(SentPacket packet) => Inject(new SimpleAck(), packet.SenderSequence);

    public void InjectFailure(TLinkErrorCode code)
        => _inbound.Writer.TryWrite(Result<TLinkMessage>.Fail(code, "injected"));

    /// <summary>The panel hangs up.</summary>
    public void Close() => _inbound.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref DisposeCount);
        Close();
        return ValueTask.CompletedTask;
    }
}
