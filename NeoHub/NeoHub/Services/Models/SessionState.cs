using System.Collections.Immutable;
using NeoHub.Services.PanelConfiguration;

namespace NeoHub.Services.Models
{
    /// <summary>
    /// Represents a complete session (panel connection) with its partitions and zones.
    /// Immutable: changed only by <see cref="Domain.PanelReducer"/>.
    /// Programming-mode operations are serialized by <see cref="Domain.IPanelOperations"/>, not by
    /// anything stored here.
    /// </summary>
    public sealed record SessionState
    {
        public required string SessionId { get; init; }

        /// <summary>The connection this state belongs to; Guid.Empty when the panel isn't connected.</summary>
        public Guid ConnectionId { get; init; }
        public string Name => SessionId; // TODO: Allow friendly names
        public ConnectionPhase ConnectionPhase { get; init; }
        public int MaxZones { get; init; }
        public int MaxPartitions { get; init; }
        public int MaxUsers { get; init; }
        public ImmutableDictionary<byte, PartitionState> Partitions { get; init; } = ImmutableDictionary<byte, PartitionState>.Empty;
        public ImmutableDictionary<byte, ZoneState> Zones { get; init; } = ImmutableDictionary<byte, ZoneState>.Empty;

        /// <summary>
        /// User slots and user-read operation state. Parallels <see cref="Configuration"/>.
        /// </summary>
        public PanelUserListState UserList { get; init; } = new();

        /// <summary>
        /// Installer configuration data read via SectionRead.
        /// Null until a config read is explicitly triggered by the user.
        /// </summary>
        public PanelConfigurationState? Configuration { get; init; }

        /// <summary>
        /// Whether the panel is currently in installer programming mode.
        /// Tracked via ProgrammingLeadInOut notifications from the panel.
        /// </summary>
        public bool IsInProgrammingMode { get; init; }

        /// <summary>
        /// Whether the initial configuration pull (capabilities, labels, status)
        /// has completed. User-initiated operations should wait for this.
        /// </summary>
        public bool IsInitialized { get; init; }


        /// <summary>
        /// When this session was established.
        /// </summary>
        public DateTime ConnectedAt { get; init; }

        /// <summary>
        /// When the last message (send or receive) occurred on this session.
        /// </summary>
        public DateTime LastMessageAt { get; init; }

        /// <summary>
        /// The panel's reported date/time at the moment of the last broadcast.
        /// </summary>
        public DateTime? PanelDateTime { get; init; }

        /// <summary>
        /// The local UTC time when PanelDateTime was received, used to calculate a running clock offset.
        /// </summary>
        public DateTime? PanelDateTimeSyncedAt { get; init; }

        /// <summary>The estimated panel time at <paramref name="nowUtc"/>: the last broadcast plus the time since.</summary>
        public DateTime? PanelDateTimeAt(DateTime nowUtc) =>
            PanelDateTime.HasValue && PanelDateTimeSyncedAt.HasValue
                ? PanelDateTime.Value + (nowUtc - PanelDateTimeSyncedAt.Value)
                : null;

        public DateTime LastUpdated { get; init; }
    }
}
