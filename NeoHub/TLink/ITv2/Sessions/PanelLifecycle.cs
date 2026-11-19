using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DSC.TLink.ITv2.Sessions
{
    public abstract record PanelLifecycleEvent(string SessionId);

    /// <param name="ConnectionLifetime">Cancelled when this particular connection ends (including being replaced by a reconnect).</param>
    /// <param name="ConnectionId">Which connection this is; state updates for it carry this ID so a replaced connection can't touch the new one's state.</param>
    public sealed record SessionConnectedNotification(string SessionId, CancellationToken ConnectionLifetime = default, Guid ConnectionId = default)
        : PanelLifecycleEvent(SessionId);

    public sealed record SessionDisconnectedNotification(string SessionId) : PanelLifecycleEvent(SessionId);

    /// <summary>
    /// Told about panels connecting and disconnecting, one event at a time, in order (a reconnect
    /// is always "disconnected" then "connected"). Observers run one after another, in
    /// registration order; the next event waits until every observer has finished this one.
    /// </summary>
    public interface IPanelLifecycleObserver
    {
        Task OnLifecycleAsync(PanelLifecycleEvent e, CancellationToken cancellationToken);
    }

    /// <summary>The ordered stream of lifecycle events: written by the registry, read by the dispatcher.</summary>
    internal sealed class PanelLifecycle
    {
        private readonly Channel<PanelLifecycleEvent> _events = Channel.CreateUnbounded<PanelLifecycleEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        public void Post(PanelLifecycleEvent e) => _events.Writer.TryWrite(e);
        public void Complete() => _events.Writer.TryComplete();
        public ChannelReader<PanelLifecycleEvent> Events => _events.Reader;
    }

    /// <summary>Delivers lifecycle events to the observers (the only reader of <see cref="PanelLifecycle"/>).</summary>
    internal sealed class PanelLifecycleDispatcher : BackgroundService
    {
        private readonly PanelLifecycle _lifecycle;
        private readonly IEnumerable<IPanelLifecycleObserver> _observers;
        private readonly ILogger<PanelLifecycleDispatcher> _logger;

        public PanelLifecycleDispatcher(PanelLifecycle lifecycle, IEnumerable<IPanelLifecycleObserver> observers, ILogger<PanelLifecycleDispatcher> logger)
        {
            _lifecycle = lifecycle;
            _observers = observers;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var observers = _observers.ToList();
            try
            {
                await foreach (var e in _lifecycle.Events.ReadAllAsync(stoppingToken))
                {
                    foreach (var observer in observers)
                    {
                        try
                        {
                            await observer.OnLifecycleAsync(e, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "{Observer} failed handling {Event}", observer.GetType().Name, e.GetType().Name);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
}
