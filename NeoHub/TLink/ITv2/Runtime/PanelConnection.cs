using System.Threading.Channels;
using DSC.TLink.Extensions;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using DSC.TLink.ITv2.Protocol;
using Microsoft.Extensions.Logging;

namespace DSC.TLink.ITv2.Runtime;

/// <summary>
/// Runs one panel connection: the imperative shell around the pure <see cref="Connection"/> core.
///
/// Everything that can happen (a frame, a request, a cancellation, a timer, a close) becomes a
/// message in one <see cref="Channel{T}"/>. A single loop reads it, steps the core, and performs
/// the effects in order. That loop is the only code that touches the connection's state and
/// the only writer to the socket, so nothing needs a lock, and work is processed strictly in
/// arrival order. Callers only post messages and await results.
/// </summary>
internal sealed class PanelConnection : IITv2Session
{
    private abstract record Envelope;
    private sealed record Core(ConnectionInput Input) : Envelope;
    private sealed record Send(IMessageData Message, TaskCompletionSource<Result<IMessageData>> Reply) : Envelope;
    private sealed record Cancel(TaskCompletionSource<Result<IMessageData>> Reply) : Envelope;
    private sealed record CloseNow : Envelope;
    private sealed record Barrier(TaskCompletionSource Reached) : Envelope;

    /// <summary>Bound on a single socket write; a peer that stops reading must not stall the loop.</summary>
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    private readonly ITLinkTransport _transport;
    private readonly Func<string, EncryptionType, ConnectionCredentials?> _resolveCredentials;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<Envelope> _inbox = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<IMessageData> _notifications = Channel.CreateUnbounded<IMessageData>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<string> _established = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _run;

    private PanelConnection(
        ITLinkTransport transport,
        Func<string, EncryptionType, ConnectionCredentials?> resolveCredentials,
        TimeProvider time,
        ConnectionConfig config,
        ILogger logger)
    {
        _transport = transport;
        _resolveCredentials = resolveCredentials;
        _time = time;
        _logger = logger;
        Lifetime = _lifetime.Token;
        _run = RunAsync(config);
    }

    /// <summary>Starts the connection's loop over an accepted transport.</summary>
    public static PanelConnection Start(
        ITLinkTransport transport,
        Func<string, EncryptionType, ConnectionCredentials?> resolveCredentials,
        TimeProvider time,
        ConnectionConfig config,
        ILogger logger)
        => new(transport, resolveCredentials, time, config, logger);

    /// <summary>Completes with the session ID once the handshake succeeds; cancelled if it never does.</summary>
    public Task<string> Established => _established.Task;

    /// <summary>Completes when the connection is fully closed and cleaned up.</summary>
    public Task Completion => _run;

    public string SessionId => _established.Task.IsCompletedSuccessfully ? _established.Task.Result : string.Empty;

    /// <summary>Cancelled when this connection ends, for any reason.</summary>
    public CancellationToken Lifetime { get; }

    public async Task<Result<IMessageData>> SendAsync(IMessageData message, CancellationToken ct = default)
    {
        var reply = new TaskCompletionSource<Result<IMessageData>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inbox.Writer.TryWrite(new Send(message, reply)))
            return Result<IMessageData>.Fail(TLinkErrorCode.Disconnected, "The connection is closed");

        using var registration = ct.CanBeCanceled
            ? ct.Register(() => _inbox.Writer.TryWrite(new Cancel(reply)))
            : default;

        var result = await reply.Task;
        if (result.IsFailure && result.Error!.Value.Code == TLinkErrorCode.Cancelled)
            ct.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Notifications from the panel, in arrival order. Ends when the connection closes.</summary>
    public IAsyncEnumerable<IMessageData> GetNotificationsAsync(CancellationToken ct = default)
        => _notifications.Reader.ReadAllAsync(ct);

    /// <summary>
    /// Completes once the loop has fully handled everything posted before this call, including
    /// re-aiming its timer. Lets tests advance a fake clock without racing the loop.
    /// </summary>
    internal Task QuiesceAsync()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inbox.Writer.TryWrite(new Barrier(reached)))
            reached.TrySetResult();
        return reached.Task;
    }

    /// <summary>Asks the connection to close (sending END_SESSION if ready) without waiting.</summary>
    public void RequestClose() => _inbox.Writer.TryWrite(new CloseNow());

    public async ValueTask DisposeAsync()
    {
        RequestClose();
        await _run;
    }

    // ── The loop ──────────────────────────────────────────────────────────────────────────

    private async Task RunAsync(ConnectionConfig config)
    {
        await Task.Yield();   // never run the loop on the caller's stack

        var state = Connection.Create(_time.GetUtcNow(), config);
        var replies = new Dictionary<RequestId, TaskCompletionSource<Result<IMessageData>>>();
        var ids = new Dictionary<TaskCompletionSource<Result<IMessageData>>, RequestId>();
        long nextId = 1;
        IDisposable? scope = null;

        using var readerStop = new CancellationTokenSource();
        var reader = ReadFramesAsync(readerStop.Token);
        using var timer = _time.CreateTimer(
            _ => _inbox.Writer.TryWrite(new Core(new Tick(_time.GetUtcNow()))),
            null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        DateTimeOffset? scheduled = null;

        try
        {
            await foreach (var envelope in _inbox.Reader.ReadAllAsync())
            {
                var now = _time.GetUtcNow();
                ConnectionInput? input = envelope switch
                {
                    Core c => c.Input,
                    CloseNow => new CloseRequested(now),
                    Send s => Register(s, now),
                    Cancel c => ids.TryGetValue(c.Reply, out var id) ? new CancelRequested(now, id) : null,
                    Barrier b => Reach(b),
                    _ => null,
                };

                // Process the input and anything it leads to (e.g. credentials) before reading on.
                var work = new Queue<ConnectionInput>();
                if (input is not null)
                    work.Enqueue(input);
                while (work.Count > 0)
                {
                    var transition = Connection.Step(state, work.Dequeue());
                    state = transition.State;
                    foreach (var effect in transition.Effects)
                        await PerformAsync(effect, work);
                }

                if (state.Phase == Phase.Closed)
                    break;

                // One timer, aimed at the core's next deadline.
                var due = Connection.NextDeadline(state);
                if (due != scheduled)
                {
                    scheduled = due;
                    timer.Change(due is { } d ? Max(d - _time.GetUtcNow(), TimeSpan.Zero) : Timeout.InfiniteTimeSpan,
                        Timeout.InfiniteTimeSpan);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Panel connection loop failed");
        }
        finally
        {
            // Stop accepting work, then fail anything still waiting.
            _inbox.Writer.TryComplete();
            while (_inbox.Reader.TryRead(out var leftover))
            {
                if (leftover is Send s)
                    s.Reply.TrySetResult(Result<IMessageData>.Fail(TLinkErrorCode.Disconnected, "The connection is closed"));
                else if (leftover is Barrier b)
                    b.Reached.TrySetResult();
            }
            foreach (var reply in replies.Values)
                reply.TrySetResult(Result<IMessageData>.Fail(TLinkErrorCode.Disconnected, "The connection is closed"));

            _notifications.Writer.TryComplete();
            _established.TrySetCanceled();
            _lifetime.Cancel();

            // The reader must be off the pipe before the transport completes it.
            readerStop.Cancel();
            await reader;
            await _transport.DisposeAsync();
            scope?.Dispose();
        }

        static ConnectionInput? Reach(Barrier b)
        {
            b.Reached.TrySetResult();
            return null;
        }

        SendRequested Register(Send s, DateTimeOffset now)
        {
            var id = new RequestId(nextId++);
            replies[id] = s.Reply;
            ids[s.Reply] = id;
            return new SendRequested(now, id, s.Message);
        }

        async Task PerformAsync(ConnectionEffect effect, Queue<ConnectionInput> work)
        {
            switch (effect)
            {
                case WriteFrame w:
                    _logger.LogDebug("TX [{Sender}→{Receiver}] {Type}", w.Packet.SenderSequence, w.Packet.ReceiverSequence, w.Packet.Message.GetType().Name);
                    _logger.LogTrace("TX bytes: {Bytes}", new HexBytes(w.Plaintext));
                    using (var writeTimeout = new CancellationTokenSource(WriteTimeout, _time))
                    {
                        var written = await _transport.SendAsync(w.Wire, writeTimeout.Token);
                        if (written.IsFailure)
                        {
                            _logger.LogWarning("Write failed: {Error}", written.Error);
                            work.Enqueue(new TransportClosed(_time.GetUtcNow()));
                        }
                    }
                    break;

                case ResolveCredentials r:
                    work.Enqueue(new CredentialsResolved(_time.GetUtcNow(), Resolve(r)));
                    break;

                case CompleteRequest c:
                    if (replies.Remove(c.Id, out var reply))
                    {
                        ids.Remove(reply);
                        reply.TrySetResult(c.Result);
                    }
                    break;

                case Publish p:
                    _notifications.Writer.TryWrite(p.Message);
                    break;

                case SessionEstablished e:
                    scope = _logger.BeginScope(ITv2ConnectionHandler.CreateLogScope(e.SessionId));
                    _established.TrySetResult(e.SessionId);
                    break;

                case SessionReady:
                    break;

                case CloseConnection c:
                    _logger.LogInformation("Connection closing: {Reason}", c.Reason);
                    break;

                case Diagnostic d:
                    _logger.Log(d.Level, "{Message}", d.Message);
                    break;
            }
        }
    }

    private ConnectionCredentials? Resolve(ResolveCredentials r)
    {
        try
        {
            return _resolveCredentials(r.SessionId, r.EncryptionType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resolving connection settings failed");
            return null;
        }
    }

    /// <summary>Turns inbound frames into inputs. Ends (and reports TransportClosed) when the socket closes.</summary>
    private async Task ReadFramesAsync(CancellationToken stop)
    {
        await Task.Yield();
        try
        {
            await foreach (var frame in _transport.ReadAllAsync(stop))
            {
                if (frame.IsFailure)
                {
                    _logger.LogWarning("TLink error: {Error}", frame.Error);
                    continue;
                }
                _inbox.Writer.TryWrite(new Core(new FrameReceived(_time.GetUtcNow(),
                    frame.Value.Header.ToArray(), frame.Value.Payload.ToArray())));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading from the panel failed");
        }
        _inbox.Writer.TryWrite(new Core(new TransportClosed(_time.GetUtcNow())));
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
