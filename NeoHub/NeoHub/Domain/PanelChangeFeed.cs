namespace NeoHub.Domain
{
    /// <summary>
    /// Delivers a store subscription's changes, in order, to one callback (a page, a layout).
    /// The first change is always <see cref="Resynchronize"/>: re-read the snapshot. If the
    /// subscriber falls too far behind, it is resubscribed and gets another one. Dispose to stop.
    /// </summary>
    public sealed class PanelChangeFeed : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;

        private PanelChangeFeed(Services.IPanelStateService store, Action<PanelChange> onChange, ILogger? logger)
            => _run = RunAsync(store, onChange, logger, _stop.Token);

        public static PanelChangeFeed Start(Services.IPanelStateService store, Action<PanelChange> onChange, ILogger? logger = null)
            => new(store, onChange, logger);

        /// <summary>Completes when the feed has stopped.</summary>
        public Task Completion => _run;

        private static async Task RunAsync(Services.IPanelStateService store, Action<PanelChange> onChange, ILogger? logger, CancellationToken stop)
        {
            await Task.Yield();
            while (!stop.IsCancellationRequested)
            {
                using var subscription = store.Subscribe();
                try
                {
                    await foreach (var change in subscription.Changes.ReadAllAsync(stop))
                        Invoke(onChange, change, logger);
                    return;   // the store has shut down
                }
                catch (SubscriberOverflowException)
                {
                    // The new subscription starts with its own Resynchronize.
                    logger?.LogWarning("State subscriber resynchronizing after falling behind");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        private static void Invoke(Action<PanelChange> onChange, PanelChange change, ILogger? logger)
        {
            try { onChange(change); }
            catch (Exception ex) { logger?.LogError(ex, "A state subscriber failed handling {Change}", change.GetType().Name); }
        }

        public void Dispose()
        {
            _stop.Cancel();
        }
    }
}
