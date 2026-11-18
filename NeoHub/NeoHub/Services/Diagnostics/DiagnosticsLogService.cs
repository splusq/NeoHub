using System.Collections.Immutable;
using System.Threading.Channels;
using DSC.TLink.Concurrency;
using Microsoft.Extensions.Options;

namespace NeoHub.Services.Diagnostics
{
    /// <summary>What a log subscriber receives: first a reset to the current buffer, then appends.</summary>
    public abstract record LogEvent;
    /// <summary>Replace everything shown with <paramref name="Entries"/> (also sent after Clear).</summary>
    public sealed record LogReset(ImmutableList<DiagnosticsLogEntry> Entries) : LogEvent;
    public sealed record LogAppended(DiagnosticsLogEntry Entry) : LogEvent;

    /// <summary>The diagnostics page's buffer of recent log entries.</summary>
    public interface IDiagnosticsLogService
    {
        /// <summary>Never blocks (called from every logger, on any thread).</summary>
        void AddLog(DiagnosticsLogEntry entry);
        IReadOnlyList<DiagnosticsLogEntry> GetLogs();
        IReadOnlySet<string> GetDiscoveredCategories();
        void Clear();

        /// <summary>A <see cref="LogReset"/> with the buffer as it is now, then every later entry, in order.</summary>
        LogSubscription Subscribe();
    }

    /// <summary>
    /// One loop owns the buffer. Entries, clears and subscriptions are messages in one channel,
    /// applied in order; readers get an immutable snapshot; each subscriber has its own bounded
    /// channel that drops its oldest entries if the subscriber falls behind (logs are best-effort,
    /// the logger must never wait).
    /// </summary>
    public sealed class DiagnosticsLogService : IDiagnosticsLogService, IAsyncDisposable
    {
        public const int SubscriberCapacity = 1000;

        private abstract record Message;
        private sealed record Append(DiagnosticsLogEntry Entry) : Message;
        private sealed record ClearAll : Message;
        private sealed record Join(Channel<LogEvent> Subscriber) : Message;
        private sealed record Leave(Channel<LogEvent> Subscriber) : Message;
        private sealed record Barrier(TaskCompletionSource Done) : Message;

        private sealed record State(ImmutableList<DiagnosticsLogEntry> Entries, ImmutableHashSet<string> Categories)
        {
            public static readonly State Empty = new([], []);
        }

        private readonly IOptionsMonitor<DiagnosticsSettings> _settings;
        private readonly Channel<Message> _inbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Snapshot<State> _state = new(State.Empty);
        private readonly Task _run;

        public DiagnosticsLogService(IOptionsMonitor<DiagnosticsSettings> settings)
        {
            _settings = settings;
            _run = RunAsync();
        }

        public void AddLog(DiagnosticsLogEntry entry) => _inbox.Writer.TryWrite(new Append(entry));
        public void Clear() => _inbox.Writer.TryWrite(new ClearAll());
        public IReadOnlyList<DiagnosticsLogEntry> GetLogs() => _state.Value.Entries;
        public IReadOnlySet<string> GetDiscoveredCategories() => _state.Value.Categories;

        public LogSubscription Subscribe()
        {
            var channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(SubscriberCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
            if (!_inbox.Writer.TryWrite(new Join(channel)))
                channel.Writer.TryComplete();
            return new LogSubscription(channel.Reader, () => _inbox.Writer.TryWrite(new Leave(channel)));
        }

        /// <summary>Completes once everything posted before it is applied (for tests).</summary>
        internal Task QuiesceAsync()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inbox.Writer.TryWrite(new Barrier(done)))
                done.TrySetResult();
            return done.Task;
        }

        public async ValueTask DisposeAsync()
        {
            _inbox.Writer.TryComplete();
            await _run;
        }

        private async Task RunAsync()
        {
            await Task.Yield();
            var subscribers = new List<Channel<LogEvent>>();
            try
            {
                await foreach (var message in _inbox.Reader.ReadAllAsync())
                {
                    var state = _state.Value;
                    switch (message)
                    {
                        case Append(var entry):
                        {
                            var entries = state.Entries.Add(entry);
                            var excess = entries.Count - Math.Max(1, _settings.CurrentValue.MaxLogEntries);
                            if (excess > 0)
                                entries = entries.RemoveRange(0, excess);
                            _state.Publish(new State(entries, state.Categories.Add(entry.Category)));
                            foreach (var s in subscribers)
                                s.Writer.TryWrite(new LogAppended(entry));
                            break;
                        }
                        case ClearAll:
                            _state.Publish(state with { Entries = [] });
                            foreach (var s in subscribers)
                                s.Writer.TryWrite(new LogReset([]));
                            break;
                        case Join(var subscriber):
                            subscriber.Writer.TryWrite(new LogReset(state.Entries));
                            subscribers.Add(subscriber);
                            break;
                        case Leave(var subscriber):
                            if (subscribers.Remove(subscriber))
                                subscriber.Writer.TryComplete();
                            break;
                        case Barrier(var done):
                            done.TrySetResult();
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
    }

    /// <summary>A log subscriber's stream. Dispose to stop.</summary>
    public sealed class LogSubscription(ChannelReader<LogEvent> events, Action leave) : IDisposable
    {
        public ChannelReader<LogEvent> Events { get; } = events;
        public void Dispose() => leave();
    }

    /// <summary>Runs a callback for each <see cref="LogEvent"/>, in order, on a background task (pages use InvokeAsync in it).</summary>
    public sealed class DiagnosticsLogFeed : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        private DiagnosticsLogFeed(IDiagnosticsLogService service, Action<LogEvent> onEvent)
            => Completion = RunAsync(service, onEvent, _stop.Token);

        public static DiagnosticsLogFeed Start(IDiagnosticsLogService service, Action<LogEvent> onEvent) => new(service, onEvent);

        public Task Completion { get; }

        private static async Task RunAsync(IDiagnosticsLogService service, Action<LogEvent> onEvent, CancellationToken stop)
        {
            await Task.Yield();
            using var subscription = service.Subscribe();
            try
            {
                await foreach (var e in subscription.Events.ReadAllAsync(stop))
                {
                    try { onEvent(e); }
                    catch (Exception) { /* a page's bug must not stop its feed; it can't log here (it would feed itself) */ }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        public void Dispose() => _stop.Cancel();
    }
}
