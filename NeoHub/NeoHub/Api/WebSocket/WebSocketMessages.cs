using NeoHub.Api.WebSocket.Models;
using NeoHub.Domain;
using NeoHub.Services;
using NeoHub.Services.PanelConfiguration.Sections;

namespace NeoHub.Api.WebSocket
{
    /// <summary>Pure mapping from panel state and changes to the messages clients receive.</summary>
    public static class WebSocketMessages
    {
        public static FullStateMessage FullState(IPanelStateService state, IEnumerable<string> activeSessions) => new()
        {
            Sessions = activeSessions
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => new SessionDto
                {
                    SessionId = id,
                    Name = id,
                    Partitions = state.GetPartitions(id)
                        .OrderBy(kv => kv.Key)
                        .Select(kv => new PartitionDto
                        {
                            PartitionNumber = kv.Key,
                            Name = kv.Value.DisplayName,
                            Status = kv.Value.EffectiveStatus
                        })
                        .ToList(),
                    Zones = state.GetZones(id)
                        .OrderBy(kv => kv.Key)
                        .Select(kv => new ZoneDto
                        {
                            ZoneNumber = kv.Key,
                            Name = kv.Value.DisplayName,
                            DeviceClass = DeviceClass(kv.Value),
                            Open = kv.Value.IsOpen,
                            Bypassed = kv.Value.IsBypassed,
                            Partitions = kv.Value.Partitions.ToList()
                        })
                        .ToList()
                })
                .ToList()
        };

        /// <summary>What a change means for clients.</summary>
        public static Broadcast For(PanelChange change) => change switch
        {
            PartitionChanged p when !string.IsNullOrWhiteSpace(p.SessionId) => new Broadcast.Send(new PartitionUpdateMessage
            {
                SessionId = p.SessionId,
                PartitionNumber = p.Partition.PartitionNumber,
                Status = p.Partition.EffectiveStatus
            }),
            ZoneChanged z when !string.IsNullOrWhiteSpace(z.SessionId) => new Broadcast.Send(new ZoneUpdateMessage
            {
                SessionId = z.SessionId,
                ZoneNumber = z.Zone.ZoneNumber,
                Open = z.Zone.IsOpen,
                Bypassed = z.Zone.IsBypassed
            }),
            SessionsChanged or ConfigurationCompleted or Resynchronize => new Broadcast.FullState(),
            _ => new Broadcast.Nothing(),
        };

        public abstract record Broadcast
        {
            public sealed record Send(WebSocketMessage Message) : Broadcast;
            public sealed record FullState : Broadcast;
            public sealed record Nothing : Broadcast;
        }

        /// <summary>
        /// Maps the zone's panel definition to a Home Assistant binary_sensor device class.
        /// The definition says how the panel treats the zone, not what hardware is wired to it,
        /// so this is a best guess. Without the installer config (no definition) every zone is "door".
        /// </summary>
        public static string DeviceClass(Services.Models.ZoneState zone) => zone.Definition switch
        {
            ZoneDefinition.Interior or
            ZoneDefinition.InteriorStayAway or
            ZoneDefinition.InteriorDelay or
            ZoneDefinition.NightZone => "motion",

            ZoneDefinition.Delayed24HourFire or
            ZoneDefinition.Standard24HourFire or
            ZoneDefinition.AutoVerifiedFire => "smoke",

            ZoneDefinition.Heat24Hour or
            ZoneDefinition.HighTemperature24Hour => "heat",
            ZoneDefinition.LowTemperature24Hour => "cold",
            ZoneDefinition.CO24Hour => "carbon_monoxide",
            ZoneDefinition.Gas24Hour => "gas",
            ZoneDefinition.Flood24Hour => "moisture",

            ZoneDefinition.Holdup24Hour or
            ZoneDefinition.Panic24Hour or
            ZoneDefinition.Medical24Hour or
            ZoneDefinition.Emergency24Hour or
            ZoneDefinition.Sprinkler24Hour => "safety",

            ZoneDefinition.LatchingTamper24Hour or
            ZoneDefinition.NonLatchingTamper24Hour => "tamper",

            ZoneDefinition.Supervisory24Hour or
            ZoneDefinition.SupervisoryBuzzer24Hour or
            ZoneDefinition.FireSupervisory => "problem",

            _ => "door"
        };
    }
}
