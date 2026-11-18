// DSC TLink - a communications library for DSC Powerseries NEO alarm panels
// Copyright (C) 2024 Brian Humlicek
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using DSC.TLink.Extensions;
using DSC.TLink.ITv2.Encryption;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Logging;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace DSC.TLink.ITv2;

/// <summary>
/// An ITv2 protocol session over TLink transport.
/// Manages session state, encryption, sequence numbers, and message routing.
/// 
/// Lifecycle:
///   var result = await ITv2Session.CreateAsync(pipe, settings, logger, ct);
///   if (result.IsSuccess)
///   {
///       var session = result.Value;
///       await foreach (var notification in session.GetNotificationsAsync(ct))
///           // handle notification
///   }
/// </summary>
internal sealed class ITv2Session : IITv2Session
{
    private readonly ITLinkTransport _transport;
    private readonly IConnectionSettingsProvider _settingsProvider;
    private readonly ILogger<ITv2Session> _logger;
    private readonly Channel<IMessageData> _notificationChannel;

    // Cancelled when the session ends for any reason (transport closed, END_SESSION, dispose).
    // Never disposed, so Lifetime stays safe to read from any thread at any time.
    private readonly CancellationTokenSource _shutdownCts = new();

    // Serializes everything written to the wire: sequence numbers, encryption and the pipe.
    // The receive pump's acks take it too; PipeWriter and the AES instance are single-writer.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // One command transaction in flight at a time. Panel replies are matched partly by
    // command type, so two concurrent requests of the same type could swap replies.
    // Notifications (e.g. the heartbeat poll) are matched by sequence number and skip the gate.
    private readonly SemaphoreSlim _transactionGate = new(1, 1);

    // Pending receivers for outbound messages awaiting responses. Mutated by senders and
    // the receive pump; every access holds _receiversLock.
    private readonly object _receiversLock = new();
    private readonly List<IMessageReceiver> _pendingReceivers = new();

    private readonly ITv2SessionTimings _timings;

    private Task _receivePump = Task.CompletedTask;
    private Task _heartbeat = Task.CompletedTask;

    // Sequence state
    private byte _localSequence = 1;
    private byte _remoteSequence = 0;
    private byte _commandSequence = 0;

    // Encryption state
    private EncryptionHandler? _encryption;
    private ConnectionSettings? _connectionSettings;

    // Logging scope — set once SessionId is known, covers the rest of the session lifetime
    private IDisposable? _sessionScope;

    // Session readiness — panels blast buffered messages on connect;
    // we ack them but discard notifications until the burst subsides (ReadyQuietPeriod with
    // no traffic). A panel that never goes quiet is declared ready after MaxReadyWait anyway,
    // otherwise every send would wait forever.
    private readonly TaskCompletionSource _sessionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Timer _readyTimer;
    private long _readyWaitStartedTicks;

    // Liveness — a half-open TCP connection (panel lost power or its link dropped) is
    // otherwise never noticed. Any inbound packet proves the panel is alive.
    private long _lastReceiveTicks = Environment.TickCount64;

    private int _disposed;

    private ITv2Session(
        ITLinkTransport transport,
        IConnectionSettingsProvider settingsProvider,
        ILogger<ITv2Session> logger,
        ITv2SessionTimings? timings = null)
    {
        _timings = timings ?? ITv2SessionTimings.Default;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _notificationChannel = Channel.CreateUnbounded<IMessageData>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        // The callback only completes the TCS; the timer is disposed once, in DisposeAsync,
        // so the receive pump can never call Change on a timer disposed under it.
        _readyTimer = new Timer(_ =>
        {
            if (_sessionReady.TrySetResult())
                _logger.LogInformation("Session ready — initial message burst complete");
        });

        Lifetime = _shutdownCts.Token;
    }

    public string SessionId { get; private set; } = string.Empty;

    /// <inheritdoc />
    public CancellationToken Lifetime { get; }

    #region Factory + Handshake

    /// <summary>
    /// Establish a new ITv2 session by performing the handshake sequence.
    /// Returns a connected session or a failure result.
    /// </summary>
    public static async Task<Result<ITv2Session>> CreateAsync(
        IDuplexPipe pipe,
        IConnectionSettingsProvider settingsProvider,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var transport = new TLinkTransport(pipe, loggerFactory.CreateLogger<TLinkTransport>());
        var session = new ITv2Session(transport, settingsProvider, loggerFactory.CreateLogger<ITv2Session>());

        Result handshakeResult;
        using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            handshakeCts.CancelAfter(session._timings.HandshakeTimeout);
            handshakeResult = await session.PerformHandshakeAsync(handshakeCts.Token);
        }

        if (handshakeResult.IsFailure)
        {
            await session.DisposeAsync();
            return Result<ITv2Session>.Fail(handshakeResult.Error!.Value);
        }

        session._logger.LogInformation("Session connected successfully");
        session.Start();
        return session;
    }

    /// <summary>
    /// Builds a session over an already-established transport, skipping the handshake.
    /// Only for tests: no encryption, session ID supplied by the caller.
    /// </summary>
    internal static ITv2Session CreateForTesting(
        ITLinkTransport transport, string sessionId, ILoggerFactory loggerFactory, ITv2SessionTimings? timings = null)
    {
        var session = new ITv2Session(transport, new NoSettingsProvider(), loggerFactory.CreateLogger<ITv2Session>(), timings)
        {
            SessionId = sessionId
        };
        session.Start();
        return session;
    }

    private sealed class NoSettingsProvider : IConnectionSettingsProvider
    {
        public ConnectionSettings? ResolveConnection(string sessionId, EncryptionType encryptionType) => null;
        public void ConfirmDefaults(string sessionId) { }
    }

    private void Start()
    {
        _receivePump = ReceivePumpAsync();
        _heartbeat = HeartbeatLoopAsync();
    }

    private async Task<Result> PerformHandshakeAsync(CancellationToken ct)
    {
        try
        {
            return await DoHandshakeAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return Result.Fail(TLinkErrorCode.Cancelled, "Handshake was cancelled");
        }
        catch (Exception ex)
        {
            return Result.Fail(TLinkErrorCode.PacketParseError, $"Handshake failed: {ex.Message}");
        }
    }

    private async Task<Result> DoHandshakeAsync(CancellationToken ct)
    {
        async Task AckInboundCommandAsync(byte commandSequence, byte remoteSenderSeq)
        {
            var response = new CommandResponse { CommandSequence = commandSequence };
            await SendPacketAsync(new ITv2Packet(_localSequence, remoteSenderSeq, response), ct);
        }

        // Send an outbound command and complete the transaction (receive CommandResponse + send SimpleAck).
        async Task SendCommandAsync(ICommandMessage message)
        {
            message.CommandSequence = GetNextCommandSequence();
            var senderSeq = GetNextLocalSequence();
            await SendPacketAsync(new ITv2Packet(senderSeq, _remoteSequence, (IMessageData)message), ct);

            // Receive CommandResponse
            var responsePacket = await ReceivePacketAsync(ct);
            // Send SimpleAck to close the transaction
            await SendSimpleAckAsync(responsePacket.SenderSequence, ct);
        }

        // Steps 1-3: Panel → Us: OpenSession command transaction
        // 1. Receive OpenSession
        var initialPacket = await ReceivePacketAsync(ct);

        SessionId = System.Text.Encoding.UTF8.GetString(_transport.DefaultHeader.Span);
        _sessionScope = _logger.BeginScope(ITv2ConnectionHandler.CreateLogScope(SessionId));
        _logger.LogInformation("Connection from Integration Identification Number {SessionId}", SessionId);
        OpenSession openSession = initialPacket.Message.As<OpenSession>();

        // Resolve per-connection settings now that we know the session ID and encryption type
        _connectionSettings = _settingsProvider.ResolveConnection(SessionId, openSession.EncryptionType);
        if (_connectionSettings == null)
            return Result.Fail(TLinkErrorCode.ConfigurationError,
                $"No valid connection settings for session {SessionId}");

        if (!_connectionSettings.HasEncryptionKey(openSession.EncryptionType))
            return Result.Fail(TLinkErrorCode.ConfigurationError,
                $"Session {SessionId} requires {openSession.EncryptionType} encryption but the access code is not configured");

        _commandSequence = openSession.CommandSequence;

        _logger.LogInformation("Handshake: OpenSession with encryption type {Type}", openSession.EncryptionType);
        // 2. Send CommandResponse
        await AckInboundCommandAsync(openSession.CommandSequence, initialPacket.SenderSequence);
        // 3. Receive SimpleAck
        await ReceivePacketAsync(ct);

        // Steps 4-6: Us → Panel: Echo OpenSession command transaction
        await SendCommandAsync(openSession);

        // Initialize encryption handler (not yet active on the wire)
        SetEncryptionHandler(openSession.EncryptionType);

        // Steps 7-9: Panel → Us: RequestAccess command transaction
        // 7. Receive RequestAccess
        var requestAccessPacket = await ReceivePacketAsync(ct);
        RequestAccess requestAccess = requestAccessPacket.Message.As<RequestAccess>();

        _logger.LogInformation("Handshake: RequestAccess received");

        // Configure outbound encryption IMMEDIATELY — step 8 is encrypted
        _encryption!.ConfigureOutboundEncryption(requestAccess.Initializer);

        // 8. Send CommandResponse (encrypted — first encrypted message)
        await AckInboundCommandAsync(requestAccess.CommandSequence, requestAccessPacket.SenderSequence);
        // 9. Receive SimpleAck
        await ReceivePacketAsync(ct);

        // Steps 10-12: Us → Panel: RequestAccess command transaction
        var ourRequestAccess = new RequestAccess { Initializer = _encryption.ConfigureInboundEncryption() };
        await SendCommandAsync(ourRequestAccess);

        _logger.LogInformation("Handshake complete, encryption active");
        return Result.Ok();
    }

    #endregion

    #region Public API

    public async Task<Result<IMessageData>> SendAsync(IMessageData message, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || Lifetime.IsCancellationRequested)
            return Result<IMessageData>.Fail(TLinkErrorCode.Disconnected, "Session is closed");

        // One deadline covers waiting for readiness, waiting for our turn, and the reply.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, Lifetime);
        linkedCts.CancelAfter(_timings.CommandTimeout);

        bool isTransaction = message is ICommandMessage;
        bool holdsGate = false;
        try
        {
            await _sessionReady.Task.WaitAsync(linkedCts.Token);

            if (isTransaction)
            {
                await _transactionGate.WaitAsync(linkedCts.Token);
                holdsGate = true;
            }

            return await SendMessageAsync(message, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The caller's own cancellation still throws; ours becomes a result.
            if (Lifetime.IsCancellationRequested)
                return Result<IMessageData>.Fail(TLinkErrorCode.Disconnected,
                    $"Session closed before the panel answered {message.GetType().Name}");

            _logger.LogWarning("No reply to {MessageType} within {Timeout}", message.GetType().Name, _timings.CommandTimeout);
            return Result<IMessageData>.Fail(TLinkErrorCode.Timeout,
                $"The panel did not answer {message.GetType().Name} within {_timings.CommandTimeout.TotalSeconds:0}s");
        }
        finally
        {
            if (holdsGate)
                _transactionGate.Release();
        }
    }

    public async IAsyncEnumerable<IMessageData> GetNotificationsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The stream ends when the channel completes (transport closed, END_SESSION, heartbeat
        // gave up, or dispose). It is not tied to Lifetime, so notifications already queued
        // when the panel hangs up are still delivered.
        var reader = _notificationChannel.Reader;
        while (await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var notification))
                yield return notification;
        }
    }

    #endregion

    #region Receive Pump

    private async Task ReceivePumpAsync()
    {
        var ct = Lifetime;
        try
        {
            Interlocked.Exchange(ref _readyWaitStartedTicks, Environment.TickCount64);
            RestartReadyTimer();

            await foreach (var tlinkResult in _transport.ReadAllAsync(ct))
            {
                RestartReadyTimer();

                if (tlinkResult.IsFailure)
                {
                    _logger.LogWarning("TLink error: {Error}", tlinkResult.Error);
                    continue;
                }

                var packetResult = ParseITv2Packet(tlinkResult.Value.Payload);
                if (packetResult.IsFailure)
                {
                    _logger.LogWarning("ITv2 parse error: {Error}", packetResult.Error);
                    continue;
                }

                var packet = packetResult.Value;
                _remoteSequence = packet.SenderSequence;
                Interlocked.Exchange(ref _lastReceiveTicks, Environment.TickCount64);

                if (packet.Message is not SimpleAck)
                {
                    // Every non-ack inbound message gets an ack
                    // (We don't support inbound command transactions that would need a CommandResponse)
                    await SendSimpleAckAsync(packet.SenderSequence, ct);
                }

                if (packet.Message is ConnectionEndSession)
                {
                    _logger.LogInformation("Received END_SESSION from panel, shutting down");
                    break;
                }

                if (TryRouteToReceiver(r => r.TryReceive(packet)))
                    continue;

                // Discard unsolicited notifications during the initial message burst
                if (!_sessionReady.Task.IsCompleted)
                {
                    if (packet.Message is not SimpleAck)
                        _logger.LogDebug("Discarding pre-ready notification: {MessageType}", packet.Message.GetType().Name);
                    continue;
                }

                // Session is ready — publish as notification(s)
                PublishNotifications(packet);
            }

            _logger.LogInformation("Transport completed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Receive pump cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in receive pump");
        }
        finally
        {
            // Whatever ended the pump, the session is over: stop the heartbeat, fail pending
            // sends, and end the notification stream once it drains.
            _notificationChannel.Writer.TryComplete();
            _shutdownCts.Cancel();
        }
    }

    /// <summary>
    /// (Re)arms the readiness timer: ready after a quiet period, but no later than MaxReadyWait
    /// after the pump started.
    /// </summary>
    private void RestartReadyTimer()
    {
        if (_sessionReady.Task.IsCompleted)
            return;

        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _readyWaitStartedTicks));
        var remaining = _timings.MaxReadyWait - elapsed;
        var due = remaining <= TimeSpan.Zero
            ? TimeSpan.Zero
            : (remaining < _timings.ReadyQuietPeriod ? remaining : _timings.ReadyQuietPeriod);

        try
        {
            _readyTimer.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently; the session is shutting down.
        }
    }

    /// <summary>
    /// Offers an inbound message to the pending receivers, first match wins. Completed
    /// receivers are skipped so a timed-out request can't swallow a later reply.
    /// </summary>
    private bool TryRouteToReceiver(Func<IMessageReceiver, bool> tryReceive)
    {
        lock (_receiversLock)
        {
            foreach (var receiver in _pendingReceivers)
            {
                if (!receiver.IsCompleted && tryReceive(receiver))
                    return true;
            }
            return false;
        }
    }

    private void AddReceiver(IMessageReceiver receiver)
    {
        lock (_receiversLock)
            _pendingReceivers.Add(receiver);
    }

    private void RemoveReceiver(IMessageReceiver receiver)
    {
        lock (_receiversLock)
            _pendingReceivers.Remove(receiver);
        receiver.Dispose();
    }

    /// <summary>
    /// Publish an inbound message (or expanded multi-message sub-messages) as notifications.
    /// Embedded command responses are routed to pending receivers instead of published.
    /// </summary>
    private void PublishNotifications(ITv2Packet packet)
    {
        if (packet.Message is MultipleMessagePacket multiMsg)
        {
            _logger.LogDebug("Expanding MultipleMessagePacket: {Count} sub-messages", multiMsg.Messages.Length);

            foreach (var subMessage in multiMsg.Messages)
            {
                if (TryRouteToReceiver(r => r.TryReceiveSubMessage(subMessage)))
                {
                    _logger.LogDebug("Routed sub-message to pending receiver: {MessageType}", subMessage.GetType().Name);
                    continue;
                }

                _logger.LogDebug("Publishing notification: {MessageType}", subMessage.GetType().Name);
                _notificationChannel.Writer.TryWrite(subMessage);
            }
        }
        else
        {
            _logger.LogDebug("Publishing notification: {MessageType}", packet.Message.GetType().Name);
            _notificationChannel.Writer.TryWrite(packet.Message);
        }
    }

    #endregion

    #region Send/Receive Primitives

    /// <summary>
    /// Send a message and wait for the transaction to complete.
    /// For notifications: waits for SimpleAck, returns null.
    /// For commands: waits for command response (sync or async), returns the response.
    ///
    /// The write lock covers only building and writing the packet, not the wait for the reply,
    /// so the receive pump (which needs it to ack) keeps running while we wait.
    /// </summary>
    private async Task<Result<IMessageData>> SendMessageAsync(IMessageData message, CancellationToken ct)
    {
        IMessageReceiver receiver;

        await _writeLock.WaitAsync(ct);
        try
        {
            var senderSeq = GetNextLocalSequence();

            if (message is CommandRequestMessage cmdReq)
            {
                var cmdSeq = GetNextCommandSequence();
                cmdReq.CommandSequence = cmdSeq;
                var requestCommand = cmdReq.Request.Command;
                receiver = new CommandRequestReceiver(senderSeq, cmdSeq, requestCommand,
                    MessageFactory.GetMessageType(requestCommand),
                    msg => _notificationChannel.Writer.TryWrite(msg));
            }
            else if (message is ICommandMessage cmd)
            {
                var cmdSeq = GetNextCommandSequence();
                cmd.CommandSequence = cmdSeq;
                receiver = MessageReceiver.CreateCommandReceiver(senderSeq, cmdSeq, cmd.Command);
            }
            else
            {
                receiver = MessageReceiver.CreateNotificationReceiver(senderSeq);
            }

            // Registered before the write so a fast reply can't arrive unmatched.
            AddReceiver(receiver);

            var packet = new ITv2Packet(senderSeq, _remoteSequence, message);
            var sendResult = await WritePacketLockedAsync(packet, ct);

            if (sendResult.IsFailure)
            {
                RemoveReceiver(receiver);
                return Result<IMessageData>.Fail(sendResult.Error!.Value);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        try
        {
            var response = await receiver.Result(ct);
            return Result<IMessageData>.Ok(response ?? message);
        }
        finally
        {
            // Always deregister: a receiver left behind after a timeout would otherwise
            // match (and swallow) the reply meant for the next request.
            RemoveReceiver(receiver);
        }
    }

    /// <summary>Writes one packet, taking the write lock.</summary>
    private async Task<Result> SendPacketAsync(ITv2Packet packet, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            return await WritePacketLockedAsync(packet, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Writes one packet. The caller must hold <see cref="_writeLock"/>.</summary>
    private async Task<Result> WritePacketLockedAsync(ITv2Packet packet, CancellationToken ct)
    {
        var plaintext = SerializePacket(packet);

        _logger.LogDebug("TX [{SenderSeq}→{ReceiverSeq}] {MessageType}",
            packet.SenderSequence, packet.ReceiverSequence, packet.Message.GetType().Name);
        _logger.LogTrace("TX bytes: {Bytes}", new HexBytes(plaintext));

        var encoded = _encryption?.HandleOutboundData(plaintext) ?? plaintext;
        return await _transport.SendAsync(encoded, ct);
    }

    private async Task SendSimpleAckAsync(byte remoteSenderSeq, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var ack = new ITv2Packet(_localSequence, remoteSenderSeq, new SimpleAck());
            await WritePacketLockedAsync(ack, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<ITv2Packet> ReceivePacketAsync(CancellationToken ct)
    {
        while (true)
        {
            var tlinkResult = await _transport.ReadMessageAsync(ct);
            if (tlinkResult.IsFailure)
                throw new InvalidOperationException($"Transport error during handshake: {tlinkResult.Error}");

            var packetResult = ParseITv2Packet(tlinkResult.Value.Payload);
            if (packetResult.IsSuccess)
            {
                _remoteSequence = packetResult.Value.SenderSequence;
                return packetResult.Value;
            }

            _logger.LogWarning("ITv2 parse error: {Error}", packetResult.Error);
        }
    }

    #endregion

    #region Serialization

    private byte[] SerializePacket(ITv2Packet packet)
    {
        var bytes = new List<byte>
        ([
            packet.SenderSequence,
            packet.ReceiverSequence,
            ..packet.Message.Serialize()
        ]);

        ITv2Framing.AddFraming(bytes);
        return bytes.ToArray();
    }

    private Result<ITv2Packet> ParseITv2Packet(ReadOnlyMemory<byte> payload)
    {
        try
        {
            var decrypted = _encryption?.HandleInboundData(payload.ToArray()) ?? payload.ToArray();
            var span = new ReadOnlySpan<byte>(decrypted);
            _logger.LogTrace("RX bytes: {Bytes}", new HexBytes(decrypted));
            ITv2Framing.RemoveFraming(ref span);

            byte senderSeq = span.PopByte();
            byte receiverSeq = span.PopByte();
            var message = MessageFactory.DeserializeMessage(span);

            _logger.LogDebug("RX [{SenderSeq}→{ReceiverSeq}] {MessageType}",
                senderSeq, receiverSeq, message.GetType().Name);

            if (_logger.IsEnabled(LogLevel.Debug) && message is not SimpleAck)
                _logger.LogDebug("RX detail: {Message}", new MessageLog(message));

            return new ITv2Packet(senderSeq, receiverSeq, message);
        }
        catch (Exception ex)
        {
            var detail = ex is TypeInitializationException { InnerException: { } inner }
                ? $"{ex.Message} → {inner.Message}"
                : ex.Message;

            return Result<ITv2Packet>.Fail(
                TLinkErrorCode.PacketParseError,
                $"Failed to parse ITv2 packet: {detail}");
        }
    }

    #endregion

    #region State Management

    private byte GetNextLocalSequence() => ++_localSequence;
    private byte GetNextCommandSequence() => ++_commandSequence;

    private void SetEncryptionHandler(EncryptionType type)
    {
        if (_encryption is not null)
            throw new InvalidOperationException("Encryption already initialized");

        _encryption = type switch
        {
            EncryptionType.Type1 => new Type1EncryptionHandler(_connectionSettings!),
            EncryptionType.Type2 => new Type2EncryptionHandler(_connectionSettings!),
            _ => throw new NotSupportedException($"Unsupported encryption type: {type}")
        };

        _logger.LogInformation("Encryption handler initialized: {Type}", type);
    }

    #endregion

    #region Heartbeat

    private async Task HeartbeatLoopAsync()
    {
        var ct = Lifetime;
        try
        {
            await _sessionReady.Task.WaitAsync(ct);

            int missed = 0;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_timings.HeartbeatInterval, ct);

                long sentAt = Environment.TickCount64;
                bool acked;
                using (var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    pollCts.CancelAfter(_timings.HeartbeatAckTimeout);
                    try
                    {
                        acked = (await SendAsync(new ConnectionPoll(), pollCts.Token)).IsSuccess;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        acked = false;
                    }
                }

                // Count a miss only when the panel sent nothing at all since the poll went out.
                if (acked || Interlocked.Read(ref _lastReceiveTicks) >= sentAt)
                {
                    if (missed > 0)
                        _logger.LogInformation("Panel responding again after {Missed} missed heartbeat(s)", missed);
                    missed = 0;
                    continue;
                }

                missed++;
                _logger.LogWarning("Panel did not answer heartbeat ({Missed}/{Max})", missed, _timings.MaxMissedHeartbeats);

                if (missed >= _timings.MaxMissedHeartbeats)
                {
                    _logger.LogWarning("Panel unresponsive; closing session so it can reconnect");
                    // Ending the notification stream lets the connection handler unregister
                    // and dispose this session, which closes the socket.
                    _notificationChannel.Writer.TryComplete();
                    _shutdownCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Heartbeat loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in heartbeat loop");
        }
    }

    #endregion

    #region Disposal

    public async ValueTask DisposeAsync()
    {
        // The session manager disposes a session it replaced, and the connection handler
        // disposes it again when its loop ends; only the first call does the work.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Say goodbye while the transport is still up. Bounded: a dead peer must not stall dispose.
        if (_sessionReady.Task.IsCompletedSuccessfully && !Lifetime.IsCancellationRequested)
        {
            try
            {
                using var cts = new CancellationTokenSource(_timings.DisposeTimeout);
                await _writeLock.WaitAsync(cts.Token);
                try
                {
                    var packet = new ITv2Packet(GetNextLocalSequence(), _remoteSequence, new ConnectionEndSession());
                    await WritePacketLockedAsync(packet, cts.Token);
                    _logger.LogInformation("END_SESSION sent");
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send END_SESSION (transport may already be closed)");
            }
        }

        // Stops the pump and heartbeat, and fails every send that is still waiting.
        _shutdownCts.Cancel();
        _notificationChannel.Writer.TryComplete();
        _readyTimer.Dispose();

        List<IMessageReceiver> pending;
        lock (_receiversLock)
        {
            pending = new List<IMessageReceiver>(_pendingReceivers);
            _pendingReceivers.Clear();
        }
        foreach (var receiver in pending)
            receiver.Dispose();

        // Let the pump and heartbeat observe cancellation before the pipe is completed under
        // them (PipeReader/PipeWriter are not safe to complete during a pending operation).
        await Task.WhenAny(Task.WhenAll(_receivePump, _heartbeat), Task.Delay(_timings.DisposeTimeout));

        // A sender that was mid-write holds the lock; wait (bounded) so encryption and the
        // transport aren't torn down under it. Never disposed itself, so late waiters are safe.
        bool gotWriteLock = await _writeLock.WaitAsync(_timings.DisposeTimeout);
        try
        {
            _encryption?.Dispose();
            await _transport.DisposeAsync();
        }
        finally
        {
            if (gotWriteLock)
                _writeLock.Release();
            _sessionScope?.Dispose();
        }
    }

    #endregion
}
