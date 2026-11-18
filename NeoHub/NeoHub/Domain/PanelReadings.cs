using System.Collections.Immutable;
using DSC.TLink.ITv2.Messages;
using NeoHub.Services.Models;
using NeoHub.Services.PanelConfiguration;
using NeoHub.Services.PanelConfiguration.Sections;

namespace NeoHub.Domain
{
    /// <summary>
    /// Pure translations from panel replies and installer configuration into domain events' data.
    /// Ported unchanged from the configuration pull.
    /// </summary>
    public static class PanelReadings
    {
        /// <summary>Partition status flags → status, in priority order.</summary>
        public static PartitionReading Partition(ModulePartitionStatus response)
        {
            var s1 = response.Status1;
            var s2 = response.Status2;

            var status =
                s2.HasFlag(ModulePartitionStatus.PartitionStatus2.PartitionInAlarm) ? PartitionStatus.Triggered :
                s1.HasFlag(ModulePartitionStatus.PartitionStatus1.EntryDelayInProgress) ? PartitionStatus.Pending :
                s1.HasFlag(ModulePartitionStatus.PartitionStatus1.StayArmed) ? PartitionStatus.ArmedHome :
                s1.HasFlag(ModulePartitionStatus.PartitionStatus1.NightArmed) ? PartitionStatus.ArmedNight :
                s1.HasFlag(ModulePartitionStatus.PartitionStatus1.Armed) ? PartitionStatus.ArmedAway :
                PartitionStatus.Disarmed;

            var isReady = !s1.HasFlag(ModulePartitionStatus.PartitionStatus1.Armed)
                          && (s1.HasFlag(ModulePartitionStatus.PartitionStatus1.ReadyToArm)
                              || s1.HasFlag(ModulePartitionStatus.PartitionStatus1.ReadyToForceArm));

            return new PartitionReading((byte)response.Partition, status, isReady,
                s1.HasFlag(ModulePartitionStatus.PartitionStatus1.ExitDelayInProgress));
        }

        /// <summary>Zone status bytes → readings, skipping zones the installer config marks as null zones.</summary>
        public static ImmutableArray<ZoneReading> Zones(ModuleZoneStatus response, IReadOnlyList<ZoneDefinition>? definitions)
        {
            var readings = ImmutableArray.CreateBuilder<ZoneReading>();
            for (int i = 0; i < response.ZoneStatusBytes.Length; i++)
            {
                var zone = (byte)(response.ZoneStart + i);
                if (definitions is not null && zone - 1 < definitions.Count && definitions[zone - 1] == ZoneDefinition.NullZone)
                    continue;
                var status = response.ZoneStatusBytes[i];
                readings.Add(new ZoneReading(zone,
                    status.HasFlag(ModuleZoneStatus.ZoneStatusEnum.Open),
                    status.HasFlag(ModuleZoneStatus.ZoneStatusEnum.Fault),
                    status.HasFlag(ModuleZoneStatus.ZoneStatusEnum.Tamper),
                    status.HasFlag(ModuleZoneStatus.ZoneStatusEnum.Bypass)));
            }
            return readings.ToImmutable();
        }

        /// <summary>
        /// Partition names and zone definitions, labels and assignments from the installer
        /// configuration: only enabled partitions and non-null zones.
        /// </summary>
        public static (ImmutableArray<PartitionSetup> Partitions, ImmutableArray<ZoneSetup> Zones) Setup(
            PanelConfigurationState config, int effectiveZones, int maxPartitions)
        {
            var partitions = ImmutableArray.CreateBuilder<PartitionSetup>();
            var partEnables = config.PartitionEnables.Values;
            var partLabels = config.PartitionLabels.Values;
            for (int p = 0; p < maxPartitions; p++)
            {
                if (p < partEnables.Count && partEnables[p] != PartitionEnable.Enabled)
                    continue;
                string? name = null;
                if (p < partLabels.Count && !string.IsNullOrEmpty(partLabels[p]))
                    name = partLabels[p].Length >= 14 ? partLabels[p][..14].TrimEnd() : partLabels[p].TrimEnd();
                partitions.Add(new PartitionSetup((byte)(p + 1), name));
            }

            var zones = ImmutableArray.CreateBuilder<ZoneSetup>();
            var zoneDefs = config.ZoneDefinitions.Values;
            var zoneLabels = config.ZoneLabels.Values;
            var assignments = config.ZoneAssignments.Values;
            for (int z = 0; z < effectiveZones; z++)
            {
                if (z < zoneDefs.Count && zoneDefs[z] == ZoneDefinition.NullZone)
                    continue;

                ZoneDefinition? definition = z < zoneDefs.Count ? zoneDefs[z] : null;
                string? line1 = null, line2 = null;
                var hasLabel = z < zoneLabels.Count && !string.IsNullOrEmpty(zoneLabels[z]);
                if (hasLabel)
                {
                    var label = zoneLabels[z];
                    line1 = label.Length >= 14 ? label[..14].TrimEnd() : label.TrimEnd();
                    line2 = label.Length > 14 ? label[14..Math.Min(28, label.Length)].TrimEnd() : null;
                }

                var zonePartitions = ImmutableArray.CreateBuilder<byte>();
                for (int p = 0; p < maxPartitions; p++)
                    if (p < assignments.GetLength(0) && z < assignments.GetLength(1) && assignments[p, z])
                        zonePartitions.Add((byte)(p + 1));

                zones.Add(new ZoneSetup((byte)(z + 1), definition, line1, line2, hasLabel, zonePartitions.ToImmutable()));
            }

            return (partitions.ToImmutable(), zones.ToImmutable());
        }
    }
}
