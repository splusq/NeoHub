using System.Collections.Immutable;

namespace NeoHub.Services.Models
{
    /// <summary>
    /// State of a single zone (belongs to session, associated with partitions). Immutable:
    /// changed only by <see cref="Domain.PanelReducer"/>, which returns a new instance.
    /// </summary>
    public sealed record ZoneState
    {
        public byte ZoneNumber { get; init; }
        public string? DisplayNameLine1 { get; init; }
        public string? DisplayNameLine2 { get; init; }
        public string DisplayName =>
            (DisplayNameLine1, DisplayNameLine2) switch
            {
                (not null, not null) => $"{DisplayNameLine1} {DisplayNameLine2}",
                (not null, null) => DisplayNameLine1,
                _ => $"Zone {ZoneNumber}"
            };
        public bool IsOpen { get; init; }
        public bool IsFaulted { get; init; }
        public bool IsTampered { get; init; }
        public bool IsBypassed { get; init; }

        /// <summary>Zone definition from installer section [001]; null unless the installer config was read.</summary>
        public PanelConfiguration.Sections.ZoneDefinition? Definition { get; init; }

        /// <summary>Which partitions this zone is associated with.</summary>
        public ImmutableArray<byte> Partitions { get; init; } = ImmutableArray<byte>.Empty;

        public DateTime LastUpdated { get; init; }

        /// <summary>Store version of the last live change (orders pulled status against live events).</summary>
        public long Version { get; init; }
    }
}
