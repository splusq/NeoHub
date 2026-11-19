using System.Collections.Immutable;
using DSC.TLink.ITv2.Messages;
using NeoHub.Services.Models;
using NeoHub.Services.PanelConfiguration;
using NeoHub.Services.PanelConfiguration.Sections;

namespace NeoHub.Domain
{
    /// <summary>
    /// Everything that can change panel state. Applied in order, one at a time, by
    /// <see cref="PanelReducer"/>.
    ///
    /// <see cref="ConnectionId"/> scopes an event to one connection: when set, the event only
    /// applies if that connection is still the panel's current one. That's how a stale
    /// connection's late messages, or a configuration pull for a connection that has since been
    /// replaced, are kept away from the new connection's state.
    /// </summary>
    public abstract record PanelEvent(string SessionId, Guid? ConnectionId = null);

    // ── Connection lifecycle (from the connection's own ordered stream) ──

    /// <summary>A connection finished its handshake: fresh state for the panel.</summary>
    public sealed record SessionStarted(string SessionId, Guid Connection, DateTimeOffset At) : PanelEvent(SessionId, Connection);

    /// <summary>A connection ended. Ignored if a newer connection has already taken over.</summary>
    public sealed record SessionEnded(string SessionId, Guid Connection) : PanelEvent(SessionId, Connection);

    /// <summary>A notification from the panel.</summary>
    public sealed record MessageReceived(string SessionId, Guid Connection, IMessageData Message, DateTimeOffset At) : PanelEvent(SessionId, Connection);

    // ── Configuration pull ──

    public sealed record ConnectionPhaseChanged(string SessionId, Guid? Connection, ConnectionPhase Phase) : PanelEvent(SessionId, Connection);

    public sealed record CapabilitiesRead(string SessionId, Guid? Connection, int MaxZones, int MaxPartitions, int MaxUsers) : PanelEvent(SessionId, Connection);

    /// <summary>Partition names, zone definitions, labels and assignments from the installer configuration.</summary>
    public sealed record InstallerSetupApplied(
        string SessionId, Guid? Connection,
        ImmutableArray<PartitionSetup> Partitions,
        ImmutableArray<ZoneSetup> Zones) : PanelEvent(SessionId, Connection);

    /// <summary>
    /// A partition's status as read from the panel. <paramref name="RequestedAtVersion"/> is the
    /// store version when the request was sent: if a live event changed the partition after
    /// that, the reading is older than what we have and is ignored.
    /// </summary>
    public sealed record PartitionStatusRead(
        string SessionId, Guid? Connection, PartitionReading Reading, long RequestedAtVersion, DateTimeOffset At) : PanelEvent(SessionId, Connection);

    /// <summary>Zone statuses as read from the panel; each is ignored if the zone changed live since the request.</summary>
    public sealed record ZoneStatusRead(
        string SessionId, Guid? Connection, ImmutableArray<ZoneReading> Readings, long RequestedAtVersion, DateTimeOffset At) : PanelEvent(SessionId, Connection);

    /// <summary>The connect-time pull is over. <paramref name="Completed"/>: it ran to the end (phase Connected).</summary>
    public sealed record InitialPullFinished(string SessionId, Guid? Connection, bool Completed) : PanelEvent(SessionId, Connection);

    // ── Values replaced as a whole ──

    /// <summary>
    /// New installer configuration. The object is handed over: nothing may modify it afterwards
    /// (copy it first; see <see cref="NeoHub.Services.PanelConfiguration.PanelConfigurationState"/>).
    /// </summary>
    public sealed record ConfigurationReplaced(string SessionId, PanelConfigurationState? Configuration) : PanelEvent(SessionId);

    /// <summary>
    /// Changes the user list. <paramref name="Update"/> must be a pure function of the current
    /// list (it is applied by the reducer, in order with every other event).
    /// </summary>
    public sealed record UserListUpdated(string SessionId, Func<PanelUserListState, PanelUserListState> Update) : PanelEvent(SessionId);

    /// <summary>The set of known panels or their connection settings changed (not a state change of one panel).</summary>
    public sealed record SessionsListChanged() : PanelEvent("");

    // ── Data carried by events ──

    public sealed record PartitionSetup(byte Number, string? Name);

    /// <summary>A zone from the installer configuration. Null label lines mean "no label configured": keep the current one.</summary>
    public sealed record ZoneSetup(byte Number, ZoneDefinition? Definition, string? Line1, string? Line2, bool HasLabel, ImmutableArray<byte> Partitions);

    public sealed record PartitionReading(byte Partition, PartitionStatus Status, bool IsReady, bool ExitDelayInProgress);

    public sealed record ZoneReading(byte Zone, bool Open, bool Faulted, bool Tampered, bool Bypassed);

    // ── What changed (for subscribers) ──

    public abstract record PanelChange(string SessionId);
    public sealed record SessionChanged(string SessionId, SessionState Session) : PanelChange(SessionId);
    public sealed record SessionRemoved(string SessionId) : PanelChange(SessionId);
    public sealed record PartitionChanged(string SessionId, PartitionState Partition) : PanelChange(SessionId);
    public sealed record ZoneChanged(string SessionId, ZoneState Zone) : PanelChange(SessionId);
    public sealed record ConfigurationCompleted(string SessionId) : PanelChange(SessionId);
    public sealed record SessionsChanged() : PanelChange("");
    /// <summary>The subscriber missed changes; re-read everything from the snapshot.</summary>
    public sealed record Resynchronize() : PanelChange("");

}
