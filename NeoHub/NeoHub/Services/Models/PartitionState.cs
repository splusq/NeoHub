namespace NeoHub.Services.Models
{
    /// <summary>
    /// State of a single partition. Immutable: changed only by <see cref="Domain.PanelReducer"/>,
    /// which returns a new instance.
    /// </summary>
    public sealed record PartitionState
    {
        public byte PartitionNumber { get; init; }
        public string? Name { get; init; }
        public string DisplayName => Name ?? $"Partition {PartitionNumber}";
        public bool IsReady { get; init; }
        public PartitionStatus Status { get; init; } = PartitionStatus.Unknown;
        public DateTime LastUpdated { get; init; }

        /// <summary>Store version of the last live change (orders pulled status against live events).</summary>
        public long Version { get; init; }

        // Exit delay tracking
        public bool ExitDelayActive { get; init; }
        public DateTime? ExitDelayStartedAt { get; init; }
        public int ExitDelayDurationSeconds { get; init; }
        public bool ExitDelayAudible { get; init; }
        public bool ExitDelayUrgent { get; init; }

        /// <summary>
        /// Whether the partition is in any armed state (including arming during exit delay).
        /// </summary>
        public bool IsArmed => Status is PartitionStatus.ArmedAway
                                      or PartitionStatus.ArmedHome
                                      or PartitionStatus.ArmedNight
                                      or PartitionStatus.Arming;

        /// <summary>
        /// The effective status, accounting for exit delay.
        /// During exit delay, this returns Arming regardless of the stored Status.
        /// </summary>
        public PartitionStatus EffectiveStatusAt(DateTime nowUtc) =>
            ExitDelayActive && ExitDelayRemainingAt(nowUtc) is { } left && left > TimeSpan.Zero
                ? PartitionStatus.Arming
                : Status;

        /// <summary>Remaining exit delay at <paramref name="nowUtc"/>, or null if no delay is active.</summary>
        public TimeSpan? ExitDelayRemainingAt(DateTime nowUtc)
        {
            if (!ExitDelayActive || !ExitDelayStartedAt.HasValue)
                return null;
            var elapsed = nowUtc - ExitDelayStartedAt.Value;
            var remaining = TimeSpan.FromSeconds(ExitDelayDurationSeconds) - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
}
