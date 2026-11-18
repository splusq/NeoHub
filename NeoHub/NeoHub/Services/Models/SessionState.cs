using System.Collections.Concurrent;
using NeoHub.Services.PanelConfiguration;

namespace NeoHub.Services.Models
{
    /// <summary>
    /// Represents a complete session (panel connection) with its partitions and zones.
    /// </summary>
    public class SessionState
    {
        public required string SessionId { get; init; }
        public string Name => SessionId; // TODO: Allow friendly names
        public ConnectionPhase ConnectionPhase { get; set; }
        public int MaxZones { get; set; }
        public int MaxPartitions { get; set; }
        public int MaxUsers { get; set; }
        // Concurrent: written by the notification loop and the configuration pull, read by the
        // UI and the WebSocket full_state snapshot at the same time.
        public ConcurrentDictionary<byte, PartitionState> Partitions { get; } = new();
        public ConcurrentDictionary<byte, ZoneState> Zones { get; } = new();

        /// <summary>
        /// User slots and user-read operation state. Parallels <see cref="Configuration"/>.
        /// </summary>
        public PanelUserListState UserList { get; } = new();

        /// <summary>
        /// Installer configuration data read via SectionRead.
        /// Null until a config read is explicitly triggered by the user.
        /// </summary>
        public PanelConfigurationState? Configuration { get; set; }

        /// <summary>
        /// Whether the panel is currently in installer programming mode.
        /// Tracked via ProgrammingLeadInOut notifications from the panel.
        /// </summary>
        public bool IsInProgrammingMode { get; set; }

        /// <summary>
        /// Whether the initial configuration pull (capabilities, labels, status)
        /// has completed. User-initiated operations should wait for this.
        /// </summary>
        public bool IsInitialized { get; set; }

        /// <summary>
        /// Serializes panel programming-mode operations (configuration reads and writes, user
        /// codes, zone bypass), so the initial pull and user-initiated operations never interleave
        /// their enter/exit programming sequences.
        /// Owned by <see cref="PanelStateService"/> per panel ID, not per connection: it survives
        /// a disconnect/reconnect, so an operation still running for the old connection and one
        /// started for the new connection contend for the same lock.
        /// </summary>
        public required SemaphoreSlim ConfigLock { get; init; }

        /// <summary>
        /// When this session was established.
        /// </summary>
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the last message (send or receive) occurred on this session.
        /// </summary>
        public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The panel's reported date/time at the moment of the last broadcast.
        /// </summary>
        public DateTime? PanelDateTime { get; set; }

        /// <summary>
        /// The local UTC time when PanelDateTime was received, used to calculate a running clock offset.
        /// </summary>
        public DateTime? PanelDateTimeSyncedAt { get; set; }

        /// <summary>
        /// Returns the estimated current panel time by adding elapsed time since the last sync.
        /// </summary>
        public DateTime? PanelDateTimeNow =>
            PanelDateTime.HasValue && PanelDateTimeSyncedAt.HasValue
                ? PanelDateTime.Value + (DateTime.UtcNow - PanelDateTimeSyncedAt.Value)
                : null;

        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }
}
