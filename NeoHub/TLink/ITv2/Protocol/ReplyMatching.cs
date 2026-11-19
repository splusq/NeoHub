using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;

namespace DSC.TLink.ITv2.Protocol;

/// <summary>Outcome of offering an inbound message to one pending request.</summary>
internal readonly record struct MatchOutcome(bool Consumed, Pending? Updated, IMessageData? Completion, bool CompletesWithNull, bool PublishCompletion)
{
    public static readonly MatchOutcome NoMatch = new(false, null, null, false, false);
    public bool Completed => Completion is not null || CompletesWithNull;
}

/// <summary>
/// How the panel's replies are matched to what we sent. Ported rule-for-rule from the previous
/// MessageReceiver (commands, notifications) and CommandRequestReceiver (data requests), so the
/// connection behaves exactly as before; only the representation changed (pure functions over
/// immutable records instead of objects mutated by two threads).
/// </summary>
internal static class ReplyMatching
{
    public static ITv2Command CommandOf(IMessageData message) => message.Command;

    /// <summary>Response command for a sent command: the request command with bit 14 set.</summary>
    public static ITv2Command ResponseTo(ITv2Command command) => (ITv2Command)((int)command | 0x4000);

    public static Pending ForMessage(RequestId id, IMessageData message, byte senderSequence, byte? commandSequence,
        DateTimeOffset deadline, bool isInternal)
    {
        if (message is CommandRequestMessage request)
        {
            var requested = CommandOf(request.Request);
            return new Pending(id, PendingKind.CommandRequest, senderSequence, commandSequence,
                SentCommand: null, ExpectedCommand: requested, ExpectedType: MessageFactory.GetMessageType(requested),
                Acknowledged: false, deadline, isInternal, message);
        }
        if (message is ICommandMessage)
        {
            var command = CommandOf(message);
            return new Pending(id, PendingKind.Command, senderSequence, commandSequence,
                SentCommand: command, ExpectedCommand: ResponseTo(command), ExpectedType: null,
                Acknowledged: false, deadline, isInternal, message);
        }
        return new Pending(id, PendingKind.Notification, senderSequence, null,
            null, null, null, false, deadline, isInternal, message);
    }

    /// <summary>Offer a whole inbound packet to a pending request.</summary>
    public static MatchOutcome TryPacket(Pending p, ITv2Packet packet)
        => p.Kind == PendingKind.CommandRequest ? CommandRequestPacket(p, packet) : MessagePacket(p, packet);

    /// <summary>Offer one sub-message of a MultipleMessagePacket.</summary>
    public static MatchOutcome TrySubMessage(Pending p, IMessageData message)
        => p.Kind == PendingKind.CommandRequest ? CommandRequestSub(p, message) : MessageSub(p, message);

    // ── Commands and notifications (was MessageReceiver) ──

    private static MatchOutcome MessagePacket(Pending p, ITv2Packet packet)
    {
        var msg = packet.Message;

        if (msg is CommandError globalError && globalError.NackCommand == p.SentCommand)
            return Complete(msg);

        // Some commands ack on the original transaction and deliver the response as a new
        // transaction: accept the response command id from any sequence.
        if (p.CommandSequence is not null && msg is not SimpleAck && CommandOf(msg) == p.ExpectedCommand)
            return Complete(msg);

        if (packet.ReceiverSequence == p.SenderSequence)
        {
            if (msg is CommandError error && error.NackCommand == p.SentCommand)
                return Complete(msg);
            if (msg is not SimpleAck && CommandOf(msg) == p.ExpectedCommand)
                return Complete(msg);
            if (msg is SimpleAck && p.CommandSequence is null)
                return new MatchOutcome(true, null, null, CompletesWithNull: true, false);
            if (msg is SimpleAck && p.CommandSequence is not null)
                return new MatchOutcome(true, p, null, false, false); // protocol ack; keep waiting
        }

        return msg is ICommandMessage ? MessageSub(p, msg) : MatchOutcome.NoMatch;
    }

    private static MatchOutcome MessageSub(Pending p, IMessageData message)
    {
        if (p.CommandSequence is not null)
        {
            if (message is CommandError error && error.NackCommand == p.SentCommand)
                return Complete(error);
            if (CommandOf(message) == p.ExpectedCommand)
                return Complete(message);
            if (message is ICommandMessage cmd && cmd.CommandSequence == p.CommandSequence)
                return Complete(message);
        }
        return MatchOutcome.NoMatch;
    }

    // ── Data requests (was CommandRequestReceiver) ──
    // The requested data arrives as a new transaction; it completes the request and is also
    // published as a notification. An ack (SimpleAck or CommandResponse) may come first.

    private static MatchOutcome CommandRequestPacket(Pending p, ITv2Packet packet)
    {
        var msg = packet.Message;
        if (IsExpected(p, msg))
            return CompleteAndPublish(msg);

        if (!p.Acknowledged)
        {
            if (packet.ReceiverSequence == p.SenderSequence && msg is SimpleAck)
                return new MatchOutcome(true, p with { Acknowledged = true }, null, false, false);
            if (msg is ICommandMessage cmd && cmd.CommandSequence == p.CommandSequence)
                return CompleteAndPublish(msg);
        }
        return MatchOutcome.NoMatch;
    }

    private static MatchOutcome CommandRequestSub(Pending p, IMessageData message)
    {
        if (IsExpected(p, message))
            return CompleteAndPublish(message);

        if (!p.Acknowledged && message is ICommandMessage cmd && cmd.CommandSequence == p.CommandSequence)
        {
            if (cmd is CommandResponse { ResponseCode: not CommandResponseCode.Success })
                return CompleteAndPublish(message);
            return new MatchOutcome(true, p with { Acknowledged = true }, null, false, false);
        }
        return MatchOutcome.NoMatch;
    }

    private static bool IsExpected(Pending p, IMessageData message)
        => (p.ExpectedType is not null && message.GetType() == p.ExpectedType)
           || (message is DefaultMessage dm && dm.Command == p.ExpectedCommand);

    private static MatchOutcome Complete(IMessageData message) => new(true, null, message, false, false);
    private static MatchOutcome CompleteAndPublish(IMessageData message) => new(true, null, message, false, true);
}
