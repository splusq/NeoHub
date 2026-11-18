using System.Collections.Immutable;
using System.Threading.Channels;
using DSC.TLink.ITv2;
using NeoHub.Services;
using NeoHub.Services.Models;

namespace NeoHub.Domain
{
    /// <summary>
    /// Owns panel state. Every change is a <see cref="PanelEvent"/> in one <see cref="Channel{T}"/>;
    /// a single loop applies them in order with <see cref="PanelReducer"/>, publishes the new
    /// immutable snapshot, and tells subscribers what changed. Readers get the latest snapshot
    /// without waiting; writers post events (and can await their application).
    ///
    /// Also the connection's <see cref="IPanelMessageSink"/>: a panel's started/messages/ended
    /// stream lands here in arrival order.
    /// </summary>
    public sealed class PanelStore : IPanelStateService, IPanelMessageSink, IAsyncDisposable
    {
        private sealed record Item(PanelEvent Event, TaskCompletionSource? Applied);

        private readonly Channel<Item> _inbox = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ILogger<PanelStore> _logger;
        private readonly Task _run;

        // Written only by the loop; immutable, so readers always see a complete state.
        private PanelStoreState _state = PanelStoreState.Empty;

        public PanelStore(ILogger<PanelStore> logger)
        {
            _logger = logger;
            _run = RunAsync();
        }

        public event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;
        public event EventHandler<PartitionStateChangedEventArgs>? PartitionStateChanged;
        public event EventHandler<ZoneStateChangedEventArgs>? ZoneStateChanged;
        public event EventHandler<ConfigurationCompleteEventArgs>? ConfigurationComplete;

        // ── Writes ──

        public void Dispatch(PanelEvent e) => _inbox.Writer.TryWrite(new Item(e, null));

        /// <summary>Posts an event and completes once it has been applied (and subscribers told).</summary>
        public Task DispatchAsync(PanelEvent e)
        {
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inbox.Writer.TryWrite(new Item(e, applied)))
                applied.TrySetResult();
            return applied.Task;
        }

        // ── Reads (snapshot; a disconnected panel reads as absent) ──

        public long Version => Volatile.Read(ref _state).Version;

        public SessionState? GetSession(string sessionId)
            => Volatile.Read(ref _state).Sessions.GetValueOrDefault(sessionId) is { ConnectionId: var c } s && c != Guid.Empty ? s : null;

        public IReadOnlyDictionary<string, SessionState> GetAllSessions()
            => Volatile.Read(ref _state).Sessions.Where(kv => kv.Value.ConnectionId != Guid.Empty).ToImmutableDictionary();

        public PartitionState? GetPartition(string sessionId, byte partitionNumber)
            => GetSession(sessionId)?.Partitions.GetValueOrDefault(partitionNumber);

        public IReadOnlyDictionary<byte, PartitionState> GetPartitions(string sessionId)
            => (IReadOnlyDictionary<byte, PartitionState>?)GetSession(sessionId)?.Partitions ?? ImmutableDictionary<byte, PartitionState>.Empty;

        public ZoneState? GetZone(string sessionId, byte zoneNumber)
            => GetSession(sessionId)?.Zones.GetValueOrDefault(zoneNumber);

        public IReadOnlyDictionary<byte, ZoneState> GetZones(string sessionId)
            => (IReadOnlyDictionary<byte, ZoneState>?)GetSession(sessionId)?.Zones ?? ImmutableDictionary<byte, ZoneState>.Empty;

        /// <summary>The programming lock for a panel, if it has connected (interim, until the operations phase).</summary>
        public SemaphoreSlim? GetConfigLock(string sessionId)
            => Volatile.Read(ref _state).Sessions.GetValueOrDefault(sessionId)?.ConfigLock;

        // ── IPanelMessageSink ──

        void IPanelMessageSink.Started(string sessionId, Guid connectionId, DateTimeOffset at)
            => Dispatch(new SessionStarted(sessionId, connectionId, at));

        void IPanelMessageSink.Accept(PanelMessage message)
            => Dispatch(new MessageReceived(message.SessionId, message.ConnectionId, message.Message, message.ReceivedAt));

        void IPanelMessageSink.Ended(string sessionId, Guid connectionId)
            => Dispatch(new SessionEnded(sessionId, connectionId));

        public async ValueTask DisposeAsync()
        {
            _inbox.Writer.TryComplete();
            await _run;
        }

        // ── The loop ──

        private async Task RunAsync()
        {
            await Task.Yield();
            await foreach (var item in _inbox.Reader.ReadAllAsync())
            {
                try
                {
                    var (next, changes) = PanelReducer.Apply(_state, item.Event);
                    Volatile.Write(ref _state, next);
                    foreach (var change in changes)
                        Announce(change);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Applying {Event} failed", item.Event.GetType().Name);
                }
                finally
                {
                    item.Applied?.TrySetResult();
                }
            }
        }

        private void Announce(PanelChange change)
        {
            switch (change)
            {
                case SessionChanged c:
                    Raise(SessionStateChanged, new SessionStateChangedEventArgs { SessionId = c.SessionId, Session = c.Session });
                    break;
                case SessionRemoved r:
                    _logger.LogInformation("Removed session state for {Session}", r.SessionId);
                    break;
                case PartitionChanged p:
                    Raise(PartitionStateChanged, new PartitionStateChangedEventArgs { SessionId = p.SessionId, Partition = p.Partition });
                    break;
                case ZoneChanged z:
                    Raise(ZoneStateChanged, new ZoneStateChangedEventArgs { SessionId = z.SessionId, Zone = z.Zone });
                    break;
                case ConfigurationCompleted c:
                    _logger.LogInformation("Configuration complete");
                    Raise(ConfigurationComplete, new ConfigurationCompleteEventArgs { SessionId = c.SessionId });
                    break;
                case UserLabelsReceived u:
                    ApplyUserLabels(u);
                    break;
            }
        }

        /// <summary>Interim: the user list is still a mutable object (until the operations phase).</summary>
        private void ApplyUserLabels(UserLabelsReceived u)
        {
            if (GetSession(u.SessionId) is not { } session)
                return;
            for (int i = 0; i < u.Labels.Length; i++)
                if (session.UserList.Users.TryGetValue(u.Start + i, out var user))
                    user.UserLabel = u.Labels[i];
        }

        private void Raise<T>(EventHandler<T>? handler, T args)
        {
            if (handler is null)
                return;
            // One subscriber throwing must not stop the others or the store.
            foreach (var subscriber in handler.GetInvocationList().Cast<EventHandler<T>>())
            {
                try { subscriber(this, args); }
                catch (Exception ex) { _logger.LogError(ex, "State change subscriber failed"); }
            }
        }
    }
}
