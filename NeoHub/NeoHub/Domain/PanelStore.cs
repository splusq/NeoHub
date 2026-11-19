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
    /// immutable snapshot, and hands the resulting <see cref="PanelChange"/>s to each subscriber
    /// through that subscriber's own bounded channel. Readers get the latest snapshot without
    /// waiting; writers post events (and can await their application).
    ///
    /// Also the connection's <see cref="IPanelMessageSink"/>: a panel's started/messages/ended
    /// stream lands here in arrival order.
    /// </summary>
    public sealed class PanelStore : IPanelStateService, IPanelMessageSink, IAsyncDisposable
    {
        /// <summary>How far a subscriber may fall behind before it is told to resynchronize.</summary>
        public const int SubscriberCapacity = 1024;

        private abstract record Message;
        private sealed record Apply(PanelEvent Event, TaskCompletionSource? Applied) : Message;
        private sealed record Add(Channel<PanelChange> Subscriber) : Message;
        private sealed record Drop(Channel<PanelChange> Subscriber) : Message;

        private readonly Channel<Message> _inbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ILogger<PanelStore> _logger;
        private readonly Task _run;

        // Written only by the loop; immutable, so readers always see a complete state.
        private readonly DSC.TLink.Concurrency.Snapshot<PanelStoreState> _state = new(PanelStoreState.Empty);

        public PanelStore(ILogger<PanelStore> logger)
        {
            _logger = logger;
            _run = RunAsync();
        }

        // ── Writes ──

        public void Dispatch(PanelEvent e) => _inbox.Writer.TryWrite(new Apply(e, null));

        /// <summary>Posts an event and completes once it has been applied (and subscribers have it).</summary>
        public Task DispatchAsync(PanelEvent e)
        {
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inbox.Writer.TryWrite(new Apply(e, applied)))
                applied.TrySetResult();
            return applied.Task;
        }

        // ── Subscriptions ──

        /// <summary>
        /// A stream that starts with <see cref="Resynchronize"/> (the snapshot is current as of that
        /// point: read it then) followed by every later change, in order, with nothing missed in
        /// between. If the subscriber falls <see cref="SubscriberCapacity"/> changes behind, its stream
        /// ends with <see cref="SubscriberOverflowException"/>: subscribe anew.
        /// </summary>
        public PanelSubscription Subscribe()
        {
            var channel = Channel.CreateBounded<PanelChange>(new BoundedChannelOptions(SubscriberCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,   // TryWrite fails when full; the loop never blocks
            });
            if (!_inbox.Writer.TryWrite(new Add(channel)))
                channel.Writer.TryComplete();
            return new PanelSubscription(channel.Reader, () => _inbox.Writer.TryWrite(new Drop(channel)));
        }

        // ── Reads (snapshot; a disconnected panel reads as absent) ──

        public long Version => _state.Value.Version;

        public SessionState? GetSession(string sessionId)
            => _state.Value.Sessions.GetValueOrDefault(sessionId) is { ConnectionId: var c } s && c != Guid.Empty ? s : null;

        public IReadOnlyDictionary<string, SessionState> GetAllSessions()
            => _state.Value.Sessions.Where(kv => kv.Value.ConnectionId != Guid.Empty).ToImmutableDictionary();

        public PartitionState? GetPartition(string sessionId, byte partitionNumber)
            => GetSession(sessionId)?.Partitions.GetValueOrDefault(partitionNumber);

        public IReadOnlyDictionary<byte, PartitionState> GetPartitions(string sessionId)
            => (IReadOnlyDictionary<byte, PartitionState>?)GetSession(sessionId)?.Partitions ?? ImmutableDictionary<byte, PartitionState>.Empty;

        public ZoneState? GetZone(string sessionId, byte zoneNumber)
            => GetSession(sessionId)?.Zones.GetValueOrDefault(zoneNumber);

        public IReadOnlyDictionary<byte, ZoneState> GetZones(string sessionId)
            => (IReadOnlyDictionary<byte, ZoneState>?)GetSession(sessionId)?.Zones ?? ImmutableDictionary<byte, ZoneState>.Empty;

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
            var subscribers = new List<Channel<PanelChange>>();
            try
            {
                await foreach (var message in _inbox.Reader.ReadAllAsync())
                {
                    switch (message)
                    {
                        case Add add:
                            // Written by the loop, so no change can fall between this and the next one.
                            if (add.Subscriber.Writer.TryWrite(new Resynchronize()))
                                subscribers.Add(add.Subscriber);
                            break;

                        case Drop drop:
                            if (subscribers.Remove(drop.Subscriber))
                                drop.Subscriber.Writer.TryComplete();
                            break;

                        case Apply apply:
                            try
                            {
                                var (next, changes) = PanelReducer.Apply(_state.Value, apply.Event);
                                _state.Publish(next);
                                foreach (var change in changes)
                                    Deliver(change, subscribers);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Applying {Event} failed", apply.Event.GetType().Name);
                            }
                            finally
                            {
                                apply.Applied?.TrySetResult();
                            }
                            break;
                    }
                }
            }
            finally
            {
                foreach (var s in subscribers)
                    s.Writer.TryComplete();
            }
        }

        private void Deliver(PanelChange change, List<Channel<PanelChange>> subscribers)
        {
            // State as it changes, so the log alone shows what the panel reported (numbers only, no labels).
            switch (change)
            {
                case SessionRemoved removed:
                    _logger.LogInformation("Removed session state for {Session}", removed.SessionId);
                    break;
                case ConfigurationCompleted:
                    _logger.LogInformation("Configuration complete");
                    break;
                case PartitionChanged { Partition: var p }:
                    _logger.LogDebug("State: partition {Partition} {Status} ready={Ready} exitDelay={ExitDelay}",
                        p.PartitionNumber, p.Status, p.IsReady, p.ExitDelayActive);
                    break;
                case ZoneChanged { Zone: var z }:
                    _logger.LogDebug("State: zone {Zone} {Open}{Bypassed}{Faulted}{Tampered}",
                        z.ZoneNumber, z.IsOpen ? "open" : "closed",
                        z.IsBypassed ? " bypassed" : "", z.IsFaulted ? " faulted" : "", z.IsTampered ? " tampered" : "");
                    break;
            }

            for (int i = subscribers.Count - 1; i >= 0; i--)
            {
                if (subscribers[i].Writer.TryWrite(change))
                    continue;
                // Too far behind: end its stream; it must resynchronize from the snapshot.
                _logger.LogWarning("A state subscriber fell {Capacity} changes behind; asking it to resynchronize", SubscriberCapacity);
                subscribers[i].Writer.TryComplete(new SubscriberOverflowException());
                subscribers.RemoveAt(i);
            }
        }
    }

    /// <summary>A subscriber's ordered stream of changes. Dispose to stop.</summary>
    public sealed class PanelSubscription(ChannelReader<PanelChange> changes, Action drop) : IDisposable
    {
        public ChannelReader<PanelChange> Changes { get; } = changes;
        public void Dispose() => drop();
    }

    /// <summary>The subscriber fell too far behind; read the snapshot again and resubscribe.</summary>
    public sealed class SubscriberOverflowException() : Exception("The subscriber fell too far behind and must resynchronize");
}
