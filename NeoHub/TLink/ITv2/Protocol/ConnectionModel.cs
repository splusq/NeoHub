using System.Collections.Immutable;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Logging;

namespace DSC.TLink.ITv2.Protocol;

/// <summary>Identifies a caller's request so its result can be routed back.</summary>
internal readonly record struct RequestId(long Value);

/// <summary>What the core needs from configuration to finish the handshake.</summary>
internal sealed record ConnectionCredentials(EncryptionType EncryptionType, string? Type1AccessCode, string? Type2AccessCode);

/// <summary>
/// Fixed for the life of one connection. <see cref="LocalRandom"/> is the only randomness the
/// protocol needs (our RequestAccess initializer); it is supplied here so the core stays pure.
/// </summary>
internal sealed record ConnectionConfig(ConnectionTimings Timings, byte[] LocalRandom)
{
    public const int LocalRandomLength = 32;
}

// ─── Inputs: everything that can happen to a connection. Each carries the time it happened. ───

internal abstract record ConnectionInput(DateTimeOffset Now);

/// <summary>A TLink frame arrived: header (the panel's integration ID) and payload (possibly encrypted).</summary>
internal sealed record FrameReceived(DateTimeOffset Now, byte[] Header, byte[] Payload) : ConnectionInput(Now);

/// <summary>Answer to <see cref="ResolveCredentials"/>; null when the panel isn't configured.</summary>
internal sealed record CredentialsResolved(DateTimeOffset Now, ConnectionCredentials? Credentials) : ConnectionInput(Now);

/// <summary>A caller wants to send a message and get its result.</summary>
internal sealed record SendRequested(DateTimeOffset Now, RequestId Id, IMessageData Message) : ConnectionInput(Now);

/// <summary>The caller gave up on a request.</summary>
internal sealed record CancelRequested(DateTimeOffset Now, RequestId Id) : ConnectionInput(Now);

/// <summary>Time passed. Sent by the shell at <see cref="Connection.NextDeadline"/>.</summary>
internal sealed record Tick(DateTimeOffset Now) : ConnectionInput(Now);

/// <summary>The socket closed.</summary>
internal sealed record TransportClosed(DateTimeOffset Now) : ConnectionInput(Now);

/// <summary>We are closing (replaced, shutting down): say goodbye if possible.</summary>
internal sealed record CloseRequested(DateTimeOffset Now) : ConnectionInput(Now);

// ─── Effects: what the shell must do, in order. ───

internal abstract record ConnectionEffect;

/// <summary>Write one packet. <see cref="Wire"/> is what goes on the socket (encrypted once keys exist).</summary>
internal sealed record WriteFrame(ITv2Packet Packet, byte[] Plaintext, byte[] Wire) : ConnectionEffect;

/// <summary>An inbound packet was decrypted and decoded (for logging: the counterpart of <see cref="WriteFrame"/>).</summary>
internal sealed record FrameDecoded(ITv2Packet Packet, byte[] Plaintext) : ConnectionEffect;

/// <summary>Look up credentials for this panel; answer with <see cref="CredentialsResolved"/>.</summary>
internal sealed record ResolveCredentials(string SessionId, EncryptionType EncryptionType) : ConnectionEffect;

/// <summary>A caller's request finished.</summary>
internal sealed record CompleteRequest(RequestId Id, Result<IMessageData> Result) : ConnectionEffect;

/// <summary>An inbound notification for the application.</summary>
internal sealed record Publish(IMessageData Message) : ConnectionEffect;

/// <summary>The handshake finished; the connection now has a session ID and encryption.</summary>
internal sealed record SessionEstablished(string SessionId) : ConnectionEffect;

/// <summary>The connect-time burst is over; requests and notifications flow from here.</summary>
internal sealed record SessionReady : ConnectionEffect;

/// <summary>The connection is over; close the socket.</summary>
internal sealed record CloseConnection(string Reason) : ConnectionEffect;

/// <summary>Something worth logging.</summary>
internal sealed record Diagnostic(LogLevel Level, string Message) : ConnectionEffect;

// ─── State ───

internal enum Phase
{
    AwaitingOpenSession,
    AwaitingCredentials,
    AwaitingOpenSessionAck,
    AwaitingOpenSessionEchoResponse,
    AwaitingRequestAccess,
    AwaitingRequestAccessAck,
    AwaitingOurRequestAccessResponse,
    Bursting,
    Ready,
    Closed,
}

internal enum PendingKind
{
    /// <summary>A notification we sent (e.g. the heartbeat poll): done when the panel acks it.</summary>
    Notification,
    /// <summary>A command: done when its response or a matching error arrives.</summary>
    Command,
    /// <summary>A CommandRequestMessage: done when the requested data arrives.</summary>
    CommandRequest,
}

/// <summary>An outbound message waiting for the panel's reply. Matching rules: <see cref="ReplyMatching"/>.</summary>
internal sealed record Pending(
    RequestId Id,
    PendingKind Kind,
    byte SenderSequence,
    byte? CommandSequence,
    ITv2Command? SentCommand,
    ITv2Command? ExpectedCommand,
    Type? ExpectedType,
    bool Acknowledged,
    DateTimeOffset Deadline,
    bool Internal,
    IMessageData Sent);

/// <summary>A request accepted but not yet sent (waiting for readiness or its turn).</summary>
internal sealed record Queued(RequestId Id, IMessageData Message, DateTimeOffset? Deadline);

internal sealed record ConnectionState
{
    public required ConnectionConfig Config { get; init; }
    public Phase Phase { get; init; } = Phase.AwaitingOpenSession;
    public DateTimeOffset HandshakeDeadline { get; init; }

    public string? SessionId { get; init; }
    public EncryptionType EncryptionType { get; init; }
    public ConnectionCredentials? Credentials { get; init; }

    // Sequence numbers (byte, wrapping), exactly as the panel expects them.
    public byte LocalSequence { get; init; } = 1;
    public byte RemoteSequence { get; init; }
    public byte CommandSequence { get; init; }

    public byte[]? InboundKey { get; init; }
    public byte[]? OutboundKey { get; init; }

    /// <summary>The panel's OpenSession, echoed back during the handshake.</summary>
    public OpenSession? PanelOpenSession { get; init; }

    /// <summary>Sent and waiting for a reply. At most one is a command.</summary>
    public ImmutableList<Pending> Pending { get; init; } = [];

    /// <summary>Accepted, not yet sent. Commands go out one at a time, in order.</summary>
    public ImmutableList<Queued> Queue { get; init; } = [];

    // Readiness
    public DateTimeOffset BurstStartedAt { get; init; }
    public DateTimeOffset ReadyDue { get; init; }

    // Heartbeat
    public DateTimeOffset? NextPollAt { get; init; }
    public RequestId? PollId { get; init; }
    public DateTimeOffset PollSentAt { get; init; }
    public DateTimeOffset LastReceiveAt { get; init; }
    public int MissedPolls { get; init; }
    public long NextInternalId { get; init; } = -1;

    public bool HoldsCommand => Pending.Any(p => p.Kind != PendingKind.Notification);
}

/// <summary>The result of one step: the new state and the effects to perform, in order.</summary>
internal readonly record struct Transition(ConnectionState State, ImmutableArray<ConnectionEffect> Effects);
