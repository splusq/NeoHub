using NeoHub.Services.Models;

namespace NeoHub.Services
{
    /// <summary>
    /// Read panel state (immutable snapshots) and post changes as events. Implemented by
    /// <see cref="Domain.PanelStore"/>; all changes go through <see cref="Domain.PanelReducer"/>.
    /// </summary>
    public interface IPanelStateService
    {
        SessionState? GetSession(string sessionId);
        IReadOnlyDictionary<string, SessionState> GetAllSessions();
        PartitionState? GetPartition(string sessionId, byte partitionNumber);
        IReadOnlyDictionary<byte, PartitionState> GetPartitions(string sessionId);
        ZoneState? GetZone(string sessionId, byte zoneNumber);
        IReadOnlyDictionary<byte, ZoneState> GetZones(string sessionId);

        /// <summary>Version of the last applied event (used to order pulled status against live events).</summary>
        long Version { get; }

        /// <summary>Posts an event; returns at once.</summary>
        void Dispatch(Domain.PanelEvent e);

        /// <summary>Posts an event; completes once it is applied and subscribers have been told.</summary>
        Task DispatchAsync(Domain.PanelEvent e);

        event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;
        event EventHandler<PartitionStateChangedEventArgs>? PartitionStateChanged;
        event EventHandler<ZoneStateChangedEventArgs>? ZoneStateChanged;
        event EventHandler<ConfigurationCompleteEventArgs>? ConfigurationComplete;
    }

    public class SessionStateChangedEventArgs : EventArgs
    {
        public required string SessionId { get; init; }
        public required SessionState Session { get; init; }
    }

    public class PartitionStateChangedEventArgs : EventArgs
    {
        public required string SessionId { get; init; }
        public required PartitionState Partition { get; init; }
    }

    public class ZoneStateChangedEventArgs : EventArgs
    {
        public required string SessionId { get; init; }
        public required ZoneState Zone { get; init; }
    }

    public class ConfigurationCompleteEventArgs : EventArgs
    {
        public required string SessionId { get; init; }
    }
}