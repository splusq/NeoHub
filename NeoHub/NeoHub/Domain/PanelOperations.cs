using System.Threading.Channels;

namespace NeoHub.Domain
{
    /// <summary>
    /// Runs a panel's programming-mode operations one at a time: the configuration pull, reads
    /// and writes of installer configuration, user and access-code operations, zone bypass.
    /// Each enters programming mode, does its work and leaves; two of them interleaved would
    /// make the panel reject one or leave it in the wrong mode for the other.
    /// </summary>
    public interface IPanelOperations
    {
        /// <summary>
        /// Queues <paramref name="work"/> behind the panel's other operations and returns its
        /// result. <paramref name="ct"/> cancels it while waiting or running. Must not be called
        /// from inside an operation for the same panel (that would wait on itself): doing so
        /// throws instead of deadlocking.
        /// </summary>
        Task<T> RunAsync<T>(string sessionId, string name, Func<CancellationToken, Task<T>> work, CancellationToken ct = default);
    }

    /// <summary>
    /// One <see cref="Channel{T}"/> lane per panel, each drained by a single loop, so a panel's
    /// operations run strictly in the order they were queued and never overlap. Lanes are kept
    /// per panel ID for the life of the process, so this holds across reconnects. Different
    /// panels run independently.
    /// </summary>
    public sealed class PanelOperations : IPanelOperations, IAsyncDisposable
    {
        private abstract record Job(string SessionId, string Name, CancellationToken Ct)
        {
            public abstract Task RunAsync();
            public abstract void Abandon();
        }

        private sealed record Job<T>(string SessionId, string Name, CancellationToken Ct,
            Func<CancellationToken, Task<T>> Work, TaskCompletionSource<T> Done) : Job(SessionId, Name, Ct)
        {
            public override async Task RunAsync()
            {
                if (Ct.IsCancellationRequested)
                {
                    Done.TrySetCanceled(Ct);
                    return;
                }
                try
                {
                    Done.TrySetResult(await Work(Ct));
                }
                catch (OperationCanceledException) when (Ct.IsCancellationRequested)
                {
                    Done.TrySetCanceled(Ct);
                }
                catch (Exception ex)
                {
                    Done.TrySetException(ex);
                }
            }

            public override void Abandon() => Done.TrySetException(new ObjectDisposedException(nameof(PanelOperations)));
        }

        // Which panel's operation the current async flow is running inside, to catch nesting.
        private static readonly AsyncLocal<string?> CurrentPanel = new();

        private readonly Channel<Job> _inbox = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ILogger<PanelOperations> _logger;
        private readonly Task _dispatch;

        public PanelOperations(ILogger<PanelOperations> logger)
        {
            _logger = logger;
            _dispatch = DispatchAsync();
        }

        public Task<T> RunAsync<T>(string sessionId, string name, Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
        {
            if (CurrentPanel.Value == sessionId)
                throw new InvalidOperationException(
                    $"'{name}' was queued from inside another operation for panel {sessionId}; it would wait on itself");

            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inbox.Writer.TryWrite(new Job<T>(sessionId, name, ct, work, done)))
                done.TrySetException(new ObjectDisposedException(nameof(PanelOperations)));
            return done.Task;
        }

        public async ValueTask DisposeAsync()
        {
            _inbox.Writer.TryComplete();
            await _dispatch;
        }

        /// <summary>Routes each job to its panel's lane, creating the lane (and its loop) on first use.</summary>
        private async Task DispatchAsync()
        {
            await Task.Yield();
            var lanes = new Dictionary<string, (Channel<Job> Queue, Task Loop)>();
            await foreach (var job in _inbox.Reader.ReadAllAsync())
            {
                if (!lanes.TryGetValue(job.SessionId, out var lane))
                {
                    var queue = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
                    lane = (queue, RunLaneAsync(job.SessionId, queue.Reader));
                    lanes[job.SessionId] = lane;
                }
                lane.Queue.Writer.TryWrite(job);
            }

            foreach (var lane in lanes.Values)
                lane.Queue.Writer.TryComplete();
            await Task.WhenAll(lanes.Values.Select(l => l.Loop));
        }

        private async Task RunLaneAsync(string sessionId, ChannelReader<Job> jobs)
        {
            await Task.Yield();
            CurrentPanel.Value = sessionId;   // flows into every job this loop runs
            await foreach (var job in jobs.ReadAllAsync())
            {
                _logger.LogDebug("Operation starting: {Operation}", job.Name);
                await job.RunAsync();
                _logger.LogDebug("Operation finished: {Operation}", job.Name);
            }
        }
    }
}
