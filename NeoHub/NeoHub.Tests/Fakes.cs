using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.MediatR;
using NeoHub.Services;

namespace NeoHub.Tests;

/// <summary>
/// An in-memory WebSocket. Records sent text messages, counts overlapping SendAsync calls
/// (which a real WebSocket rejects), and lets a test feed inbound messages or a close.
/// </summary>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type)> _inbound = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
    private WebSocketState _state = WebSocketState.Open;
    private int _sendsInFlight;
    private int _maxSendsInFlight;

    public ConcurrentQueue<string> Sent { get; } = new();
    public int MaxConcurrentSends => Volatile.Read(ref _maxSendsInFlight);
    public TimeSpan SendDelay { get; set; } = TimeSpan.FromMilliseconds(1);

    /// <summary>When set, every send waits on it (simulates a stalled client).</summary>
    public TaskCompletionSource? SendGate { get; set; }

    public int CloseOutputCount;
    public bool Aborted;

    public IEnumerable<string> SentOfType(string type) => Sent.Where(m => m.Contains($"\"type\":\"{type}\""));

    public void ReceiveText(string json) => _inbound.Writer.TryWrite((Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text));
    public void ReceiveClose() => _inbound.Writer.TryWrite((Array.Empty<byte>(), WebSocketMessageType.Close));

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override void Abort()
    {
        Aborted = true;
        _state = WebSocketState.Aborted;
        _inbound.Writer.TryComplete(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        await SendCore(null, cancellationToken);
        Interlocked.Increment(ref CloseOutputCount);
        _state = WebSocketState.Closed;
    }

    public override void Dispose() { }

    // Remainder of a message larger than the caller's buffer, delivered in pieces like a real socket.
    private byte[]? _partial;
    private int _partialOffset;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_partial is not null)
            return DeliverChunk(buffer);

        (byte[] Bytes, WebSocketMessageType Type) item;
        try
        {
            item = await _inbound.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException ex) when (ex.InnerException is WebSocketException wse)
        {
            throw wse;
        }

        if (item.Type == WebSocketMessageType.Close)
        {
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        _partial = item.Bytes;
        _partialOffset = 0;
        return DeliverChunk(buffer);
    }

    private WebSocketReceiveResult DeliverChunk(ArraySegment<byte> buffer)
    {
        var count = Math.Min(buffer.Count, _partial!.Length - _partialOffset);
        Array.Copy(_partial, _partialOffset, buffer.Array!, buffer.Offset, count);
        _partialOffset += count;
        var end = _partialOffset == _partial.Length;
        if (end)
            _partial = null;
        return new WebSocketReceiveResult(count, WebSocketMessageType.Text, end);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => SendCore(buffer.ToArray(), cancellationToken);

    private async Task SendCore(byte[]? bytes, CancellationToken cancellationToken)
    {
        if (_state == WebSocketState.Aborted)
            throw new WebSocketException(WebSocketError.InvalidState);

        var inFlight = Interlocked.Increment(ref _sendsInFlight);
        int max;
        while (inFlight > (max = Volatile.Read(ref _maxSendsInFlight))
               && Interlocked.CompareExchange(ref _maxSendsInFlight, inFlight, max) != max) { }
        try
        {
            if (SendGate is { } gate)
                await gate.Task.WaitAsync(cancellationToken);
            if (SendDelay > TimeSpan.Zero)
                await Task.Delay(SendDelay, cancellationToken);
            if (bytes is not null)
                Sent.Enqueue(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            Interlocked.Decrement(ref _sendsInFlight);
        }
    }
}

internal sealed class FakeSessionManager(params string[] sessionIds) : IITv2SessionManager
{
    void IITv2SessionManager.RegisterSession(string sessionId, IITv2Session session) { }
    void IITv2SessionManager.UnregisterSession(string sessionId, IITv2Session session) { }
    IITv2Session? IITv2SessionManager.GetSession(string sessionId) => null;
    public IEnumerable<string> GetActiveSessions() => sessionIds;
    public Task DisconnectSessionAsync(string sessionId) => Task.CompletedTask;
    public Task DisconnectAllAsync() => Task.CompletedTask;
}

internal sealed class FakeCommandService : IPanelCommandService
{
    public Task<PanelCommandResult> ArmAsync(string sessionId, byte partition, ArmingMode mode, string? accessCode = null)
        => Task.FromResult(PanelCommandResult.Ok());
    public Task<PanelCommandResult> DisarmAsync(string sessionId, byte partition, string? accessCode = null)
        => Task.FromResult(PanelCommandResult.Ok());
    public Task<PanelCommandResult> BypassZoneAsync(string sessionId, byte partition, byte zoneNumber, bool bypass, string? accessCode = null)
        => Task.FromResult(PanelCommandResult.Ok());
}
