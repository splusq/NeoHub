using System.Collections.Immutable;
using System.Text;
using DSC.TLink.ITv2.Encryption;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Logging;

namespace DSC.TLink.ITv2.Protocol;

/// <summary>
/// One ITv2 connection as a pure state machine: <see cref="Step"/> takes the current state and
/// one input and returns the next state plus the effects to perform, in order. It never reads a
/// clock (time arrives in each input), never touches I/O, and never uses randomness (our
/// handshake initializer is in <see cref="ConnectionConfig"/>), so the same inputs always give
/// the same outputs.
///
/// Behavior matches the previous ITv2Session packet for packet (sequence numbers, acks,
/// handshake order, reply matching), with these deliberate changes:
/// <list type="bullet">
/// <item>one command transaction in flight at a time (replies are matched partly by type);</item>
/// <item>every request has a deadline once the session is ready;</item>
/// <item>readiness is bounded by MaxReadyWait; the handshake by HandshakeTimeout;</item>
/// <item>a completed or cancelled request is removed at once, so it can't claim a later reply;</item>
/// <item>a heartbeat that gets no traffic at all MaxMissedHeartbeats times closes the connection.</item>
/// </list>
/// </summary>
internal static class Connection
{
    public static ConnectionState Create(DateTimeOffset now, ConnectionConfig config)
    {
        if (config.LocalRandom.Length != ConnectionConfig.LocalRandomLength)
            throw new ArgumentException($"LocalRandom must be {ConnectionConfig.LocalRandomLength} bytes", nameof(config));
        return new ConnectionState
        {
            Config = config,
            HandshakeDeadline = now + config.Timings.HandshakeTimeout,
            LastReceiveAt = now,
        };
    }

    /// <summary>When the shell should send the next <see cref="Tick"/>, or null if nothing is scheduled.</summary>
    public static DateTimeOffset? NextDeadline(ConnectionState s)
    {
        switch (s.Phase)
        {
            case Phase.Closed:
                return null;
            case Phase.Bursting:
                return s.ReadyDue;
            case Phase.Ready:
                DateTimeOffset? next = s.PollId is null ? s.NextPollAt : null;
                foreach (var p in s.Pending)
                    next = Min(next, p.Deadline);
                foreach (var q in s.Queue)
                    if (q.Deadline is { } d)
                        next = Min(next, d);
                return next;
            default:
                return s.HandshakeDeadline;
        }
    }

    public static Transition Step(ConnectionState state, ConnectionInput input)
    {
        var fx = ImmutableArray.CreateBuilder<ConnectionEffect>();
        var s = state;

        if (s.Phase != Phase.Closed)
            s = Advance(s, input.Now, fx);

        if (s.Phase == Phase.Closed)
        {
            if (input is SendRequested closedSend)
                fx.Add(new CompleteRequest(closedSend.Id, Disconnected("The connection is closed")));
            return new Transition(s, fx.ToImmutable());
        }

        s = input switch
        {
            FrameReceived f => s.Phase < Phase.Bursting ? HandshakeFrame(s, f, fx) : SessionFrame(s, f, fx),
            CredentialsResolved c => Credentials(s, c, fx),
            SendRequested r => Accept(s, r, fx),
            CancelRequested c => Cancel(s, c, fx),
            TransportClosed => Close(s, "The panel closed the connection", fx),
            CloseRequested => Goodbye(s, input.Now, fx),
            Tick => s,
            _ => s,
        };

        return new Transition(s, fx.ToImmutable());
    }

    // ── Time ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Fires everything due at or before <paramref name="now"/>.</summary>
    private static ConnectionState Advance(ConnectionState s, DateTimeOffset now, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        if (s.Phase < Phase.Bursting && now >= s.HandshakeDeadline)
            return Close(s, $"Handshake did not complete within {s.Config.Timings.HandshakeTimeout.TotalSeconds:0}s", fx);

        if (s.Phase == Phase.Bursting && now >= s.ReadyDue)
            s = BecomeReady(s, now, fx);

        if (s.Phase != Phase.Ready)
            return s;

        // Expire requests whose deadline has passed.
        foreach (var p in s.Pending.Where(p => p.Deadline <= now).ToList())
        {
            s = s with { Pending = s.Pending.Remove(p) };
            if (p.Internal)
                s = PollTimedOut(s, p, fx);
            else
                fx.Add(new CompleteRequest(p.Id, TimedOut(p.Sent, s.Config.Timings)));
            if (s.Phase == Phase.Closed)
                return s;
        }
        foreach (var q in s.Queue.Where(q => q.Deadline is { } d && d <= now).ToList())
        {
            s = s with { Queue = s.Queue.Remove(q) };
            fx.Add(new CompleteRequest(q.Id, TimedOut(q.Message, s.Config.Timings)));
        }

        // Heartbeat.
        if (s.PollId is null && s.NextPollAt is { } pollAt && now >= pollAt)
        {
            var id = new RequestId(s.NextInternalId);
            s = s with { NextInternalId = s.NextInternalId - 1, PollId = id, PollSentAt = now, NextPollAt = null };
            s = Write(s, id, new ConnectionPoll(), now + s.Config.Timings.HeartbeatAckTimeout, isInternal: true, fx);
        }

        return Flush(s, now, fx);
    }

    private static ConnectionState BecomeReady(ConnectionState s, DateTimeOffset now, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        // Use the due time, not "now": a late Tick must not shift the schedule.
        var readyAt = s.ReadyDue;
        fx.Add(new SessionReady());
        fx.Add(new Diagnostic(LogLevel.Information, "Session ready — initial message burst complete"));
        var deadline = readyAt + s.Config.Timings.CommandTimeout;
        return s with
        {
            Phase = Phase.Ready,
            NextPollAt = readyAt + s.Config.Timings.HeartbeatInterval,
            // Requests that arrived before readiness start their clock now.
            Queue = s.Queue.Select(q => q with { Deadline = q.Deadline ?? deadline }).ToImmutableList(),
        };
    }

    private static ConnectionState PollTimedOut(ConnectionState s, Pending poll, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        var timings = s.Config.Timings;
        s = s with { PollId = null, NextPollAt = poll.Deadline + timings.HeartbeatInterval };

        // Any traffic at all since the poll went out proves the panel is alive.
        if (s.LastReceiveAt >= s.PollSentAt)
            return s with { MissedPolls = 0 };

        var missed = s.MissedPolls + 1;
        fx.Add(new Diagnostic(LogLevel.Warning, $"Panel did not answer heartbeat ({missed}/{timings.MaxMissedHeartbeats})"));
        s = s with { MissedPolls = missed };
        return missed >= timings.MaxMissedHeartbeats
            ? Close(s, "Panel unresponsive; closing so it can reconnect", fx)
            : s;
    }

    // ── Requests ──────────────────────────────────────────────────────────────────────────

    private static ConnectionState Accept(ConnectionState s, SendRequested r, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        DateTimeOffset? deadline = s.Phase == Phase.Ready ? r.Now + s.Config.Timings.CommandTimeout : null;
        s = s with { Queue = s.Queue.Add(new Queued(r.Id, r.Message, deadline)) };
        return Flush(s, r.Now, fx);
    }

    private static ConnectionState Cancel(ConnectionState s, CancelRequested c, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        var queued = s.Queue.FirstOrDefault(q => q.Id == c.Id);
        var pending = s.Pending.FirstOrDefault(p => p.Id == c.Id);
        if (queued is null && pending is null)
            return s;

        s = s with
        {
            Queue = queued is null ? s.Queue : s.Queue.Remove(queued),
            Pending = pending is null ? s.Pending : s.Pending.Remove(pending),
        };
        fx.Add(new CompleteRequest(c.Id, Result<IMessageData>.Fail(TLinkErrorCode.Cancelled, "Cancelled by the caller")));
        return Flush(s, c.Now, fx);
    }

    /// <summary>
    /// Sends queued requests that may go now: notifications always; commands only when no
    /// other command is waiting for its reply, strictly in order.
    /// </summary>
    private static ConnectionState Flush(ConnectionState s, DateTimeOffset now, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        if (s.Phase != Phase.Ready)
            return s;

        foreach (var q in s.Queue.ToList())
        {
            var isCommand = q.Message is ICommandMessage;
            if (isCommand && s.HoldsCommand)
                continue;
            s = s with { Queue = s.Queue.Remove(q) };
            s = Write(s, q.Id, q.Message, q.Deadline ?? now + s.Config.Timings.CommandTimeout, isInternal: false, fx);
        }
        return s;
    }

    /// <summary>Starts a transaction: next sequence numbers, write, and wait for its reply.</summary>
    private static ConnectionState Write(ConnectionState s, RequestId id, IMessageData message, DateTimeOffset deadline,
        bool isInternal, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        var sender = unchecked((byte)(s.LocalSequence + 1));
        byte? commandSequence = null;
        if (message is CommandMessageBase command)
        {
            commandSequence = unchecked((byte)(s.CommandSequence + 1));
            message = command with { CommandSequence = commandSequence.Value };
        }

        s = s with
        {
            LocalSequence = sender,
            CommandSequence = commandSequence ?? s.CommandSequence,
            Pending = s.Pending.Add(ReplyMatching.ForMessage(id, message, sender, commandSequence, deadline, isInternal)),
        };
        return Emit(s, new ITv2Packet(sender, s.RemoteSequence, message), fx);
    }

    private static ConnectionState Ack(ConnectionState s, byte remoteSender, ImmutableArray<ConnectionEffect>.Builder fx)
        => Emit(s, new ITv2Packet(s.LocalSequence, remoteSender, new SimpleAck()), fx);

    private static ConnectionState Emit(ConnectionState s, ITv2Packet packet, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        var plaintext = PacketCodec.Encode(packet);
        fx.Add(new WriteFrame(packet, plaintext, PacketCodec.Protect(plaintext, s.OutboundKey)));
        return s;
    }

    // ── Inbound, after the handshake ──────────────────────────────────────────────────────

    private static ConnectionState SessionFrame(ConnectionState s, FrameReceived f, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        // Any frame (even one we can't parse) means the panel is still bursting.
        if (s.Phase == Phase.Bursting)
            s = s with { ReadyDue = Min(f.Now + s.Config.Timings.ReadyQuietPeriod, s.BurstStartedAt + s.Config.Timings.MaxReadyWait) };

        var decoded = PacketCodec.Decode(f.Payload, s.InboundKey);
        if (decoded.IsFailure)
        {
            fx.Add(new Diagnostic(LogLevel.Warning, $"ITv2 parse error: {decoded.Error}"));
            return s;
        }

        var packet = decoded.Value;
        s = s with { RemoteSequence = packet.SenderSequence, LastReceiveAt = f.Now };

        // Every non-ack inbound message is acked first, before anything it triggers.
        if (packet.Message is not SimpleAck)
            s = Ack(s, packet.SenderSequence, fx);

        if (packet.Message is ConnectionEndSession)
            return Close(s, "The panel ended the session", fx);

        if (Route(ref s, p => ReplyMatching.TryPacket(p, packet), fx))
            return Flush(s, f.Now, fx);

        if (s.Phase != Phase.Ready)
        {
            if (packet.Message is not SimpleAck)
                fx.Add(new Diagnostic(LogLevel.Debug, $"Discarding pre-ready notification: {packet.Message.GetType().Name}"));
            return s;
        }

        if (packet.Message is MultipleMessagePacket multiple)
        {
            foreach (var sub in multiple.Messages)
                if (!Route(ref s, p => ReplyMatching.TrySubMessage(p, sub), fx))
                    fx.Add(new Publish(sub));
        }
        else if (packet.Message is not SimpleAck)
        {
            fx.Add(new Publish(packet.Message));
        }

        return Flush(s, f.Now, fx);
    }

    /// <summary>Offers a message to pending requests in order; the first that consumes it wins.</summary>
    private static bool Route(ref ConnectionState s, Func<Pending, MatchOutcome> offer, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        foreach (var p in s.Pending)
        {
            var outcome = offer(p);
            if (!outcome.Consumed)
                continue;

            if (!outcome.Completed)
            {
                s = s with { Pending = s.Pending.Replace(p, outcome.Updated ?? p) };
                return true;
            }

            s = s with { Pending = s.Pending.Remove(p) };
            if (outcome.PublishCompletion && outcome.Completion is not null)
                fx.Add(new Publish(outcome.Completion));

            if (p.Internal)
                s = PollAnswered(s);
            else
                fx.Add(new CompleteRequest(p.Id, Result<IMessageData>.Ok(outcome.Completion ?? p.Sent)));
            return true;
        }
        return false;
    }

    private static ConnectionState PollAnswered(ConnectionState s)
        => s with
        {
            PollId = null,
            MissedPolls = 0,
            NextPollAt = s.LastReceiveAt + s.Config.Timings.HeartbeatInterval,
        };

    // ── Handshake ─────────────────────────────────────────────────────────────────────────
    // Panel → us: OpenSession (we answer, then echo it); RequestAccess (we answer, then send
    // ours). Encryption: outbound from our answer to the panel's RequestAccess; inbound from
    // the panel's answer to ours.

    private static ConnectionState HandshakeFrame(ConnectionState s, FrameReceived f, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        if (s.Phase == Phase.AwaitingCredentials)
        {
            fx.Add(new Diagnostic(LogLevel.Warning, "Frame received while resolving credentials; ignored"));
            return s;
        }

        var decoded = PacketCodec.Decode(f.Payload, s.InboundKey);
        if (decoded.IsFailure)
        {
            fx.Add(new Diagnostic(LogLevel.Warning, $"ITv2 parse error: {decoded.Error}"));
            return s;
        }

        var packet = decoded.Value;
        s = s with { RemoteSequence = packet.SenderSequence, LastReceiveAt = f.Now };

        switch (s.Phase)
        {
            case Phase.AwaitingOpenSession:
            {
                if (packet.Message is not OpenSession open)
                    return Close(s, $"Handshake failed: expected OpenSession, got {packet.Message.GetType().Name}", fx);
                var sessionId = Encoding.UTF8.GetString(f.Header);
                fx.Add(new Diagnostic(LogLevel.Information, $"Connection from Integration Identification Number {sessionId}"));
                fx.Add(new ResolveCredentials(sessionId, open.EncryptionType));
                return s with
                {
                    Phase = Phase.AwaitingCredentials,
                    SessionId = sessionId,
                    EncryptionType = open.EncryptionType,
                    CommandSequence = open.CommandSequence,
                    PanelOpenSession = open,
                };
            }

            case Phase.AwaitingOpenSessionAck:
            {
                // Echo the panel's OpenSession back as our own command.
                var commandSequence = unchecked((byte)(s.CommandSequence + 1));
                var sender = unchecked((byte)(s.LocalSequence + 1));
                var echo = s.PanelOpenSession! with { CommandSequence = commandSequence };
                s = s with { Phase = Phase.AwaitingOpenSessionEchoResponse, CommandSequence = commandSequence, LocalSequence = sender };
                return Emit(s, new ITv2Packet(sender, s.RemoteSequence, echo), fx);
            }

            case Phase.AwaitingOpenSessionEchoResponse:
                s = Ack(s, packet.SenderSequence, fx);
                return s with { Phase = Phase.AwaitingRequestAccess };

            case Phase.AwaitingRequestAccess:
            {
                if (packet.Message is not RequestAccess request)
                    return Close(s, $"Handshake failed: expected RequestAccess, got {packet.Message.GetType().Name}", fx);

                var outbound = OutboundKey(s, request.Initializer);
                if (outbound is null)
                    return Close(s, "Handshake failed: the panel's encryption initializer did not validate", fx);

                fx.Add(new Diagnostic(LogLevel.Information, "Handshake: RequestAccess received"));
                // Our answer is the first encrypted message.
                s = s with { Phase = Phase.AwaitingRequestAccessAck, OutboundKey = outbound };
                return Emit(s, new ITv2Packet(s.LocalSequence, packet.SenderSequence,
                    new CommandResponse { CommandSequence = request.CommandSequence }), fx);
            }

            case Phase.AwaitingRequestAccessAck:
            {
                var (inbound, initializer) = InboundKey(s);
                var commandSequence = unchecked((byte)(s.CommandSequence + 1));
                var sender = unchecked((byte)(s.LocalSequence + 1));
                s = s with { Phase = Phase.AwaitingOurRequestAccessResponse, CommandSequence = commandSequence, LocalSequence = sender };
                s = Emit(s, new ITv2Packet(sender, s.RemoteSequence,
                    new RequestAccess { Initializer = initializer, CommandSequence = commandSequence }), fx);
                // The panel's reply to our RequestAccess is the first encrypted inbound message.
                return s with { InboundKey = inbound };
            }

            case Phase.AwaitingOurRequestAccessResponse:
                s = Ack(s, packet.SenderSequence, fx);
                fx.Add(new Diagnostic(LogLevel.Information, "Handshake complete, encryption active"));
                fx.Add(new SessionEstablished(s.SessionId!));
                return s with
                {
                    Phase = Phase.Bursting,
                    BurstStartedAt = f.Now,
                    ReadyDue = Min(f.Now + s.Config.Timings.ReadyQuietPeriod, f.Now + s.Config.Timings.MaxReadyWait),
                };

            default:
                return s;
        }
    }

    private static ConnectionState Credentials(ConnectionState s, CredentialsResolved c, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        if (s.Phase != Phase.AwaitingCredentials)
            return s;

        var creds = c.Credentials;
        var hasKey = s.EncryptionType switch
        {
            EncryptionType.Type1 => !string.IsNullOrEmpty(creds?.Type1AccessCode),
            EncryptionType.Type2 => !string.IsNullOrEmpty(creds?.Type2AccessCode),
            _ => false,
        };
        if (creds is null || !hasKey)
            return Close(s, $"No valid connection settings for session {s.SessionId} ({s.EncryptionType} encryption)", fx);

        fx.Add(new Diagnostic(LogLevel.Information, $"Handshake: OpenSession with encryption type {s.EncryptionType}"));
        s = s with { Phase = Phase.AwaitingOpenSessionAck, Credentials = creds };
        return Emit(s, new ITv2Packet(s.LocalSequence, s.RemoteSequence,
            new CommandResponse { CommandSequence = s.PanelOpenSession!.CommandSequence }), fx);
    }

    private static byte[]? OutboundKey(ConnectionState s, byte[] remoteInitializer)
    {
        try
        {
            return s.EncryptionType switch
            {
                EncryptionType.Type2 => ITv2Crypto.Type2.OutboundKey(
                    ITv2Crypto.Type2.ParseAccessCode(s.Credentials!.Type2AccessCode!), remoteInitializer),
                EncryptionType.Type1 => ITv2Crypto.Type1.OutboundKey(
                    ITv2Crypto.Type1.ExpandKeyString(s.SessionId!), remoteInitializer),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static (byte[] Key, byte[] Initializer) InboundKey(ConnectionState s)
    {
        var random = s.Config.LocalRandom;
        if (s.EncryptionType == EncryptionType.Type2)
        {
            var initializer = random[..ITv2Crypto.Type2.InitializerLength];
            return (ITv2Crypto.Type2.InboundKey(ITv2Crypto.Type2.ParseAccessCode(s.Credentials!.Type2AccessCode!), initializer), initializer);
        }
        return ITv2Crypto.Type1.Inbound(ITv2Crypto.Type1.ExpandKeyString(s.Credentials!.Type1AccessCode!),
            random.AsSpan(0, ITv2Crypto.Type1.RandomLength));
    }

    // ── Closing ───────────────────────────────────────────────────────────────────────────

    private static ConnectionState Goodbye(ConnectionState s, DateTimeOffset now, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        if (s.Phase == Phase.Ready)
        {
            var sender = unchecked((byte)(s.LocalSequence + 1));
            s = s with { LocalSequence = sender };
            s = Emit(s, new ITv2Packet(sender, s.RemoteSequence, new ConnectionEndSession()), fx);
        }
        return Close(s, "Closed by NeoHub", fx);
    }

    private static ConnectionState Close(ConnectionState s, string reason, ImmutableArray<ConnectionEffect>.Builder fx)
    {
        foreach (var p in s.Pending.Where(p => !p.Internal))
            fx.Add(new CompleteRequest(p.Id, Disconnected($"The connection closed before the panel answered {p.Sent.GetType().Name}")));
        foreach (var q in s.Queue)
            fx.Add(new CompleteRequest(q.Id, Disconnected($"The connection closed before {q.Message.GetType().Name} was sent")));
        fx.Add(new CloseConnection(reason));
        return s with { Phase = Phase.Closed, Pending = [], Queue = [], PollId = null, NextPollAt = null };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    private static Result<IMessageData> Disconnected(string message)
        => Result<IMessageData>.Fail(TLinkErrorCode.Disconnected, message);

    private static Result<IMessageData> TimedOut(IMessageData message, ConnectionTimings timings)
        => Result<IMessageData>.Fail(TLinkErrorCode.Timeout,
            $"The panel did not answer {message.GetType().Name} within {timings.CommandTimeout.TotalSeconds:0}s");

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset b) => a is null || b < a ? b : a;
}
