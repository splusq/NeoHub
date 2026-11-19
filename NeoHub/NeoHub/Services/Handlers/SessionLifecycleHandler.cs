using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Sessions;

namespace NeoHub.Services.Handlers
{
    /// <summary>
    /// Confirms connection defaults and announces the changed session list.
    /// </summary>
    public class SessionLifecycleHandler : IPanelLifecycleObserver
    {
        private readonly ISessionMonitor _monitor;
        private readonly IPanelStateService _panelState;
        private readonly IConnectionSettingsProvider _settingsProvider;
        private readonly ILogger<SessionLifecycleHandler> _logger;

        public SessionLifecycleHandler(
            ISessionMonitor monitor,
            IPanelStateService panelState,
            IConnectionSettingsProvider settingsProvider,
            ILogger<SessionLifecycleHandler> logger)
        {
            _monitor = monitor;
            _panelState = panelState;
            _settingsProvider = settingsProvider;
            _logger = logger;
        }

        public Task OnLifecycleAsync(PanelLifecycleEvent e, CancellationToken cancellationToken) => e switch
        {
            SessionConnectedNotification c => Handle(c, cancellationToken),
            SessionDisconnectedNotification d => Handle(d, cancellationToken),
            _ => Task.CompletedTask,
        };

        public Task Handle(SessionConnectedNotification notification, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Session connected");
            _settingsProvider.ConfirmDefaults(notification.SessionId);
            _monitor.NotifyChanged();
            return Task.CompletedTask;
        }

        public Task Handle(SessionDisconnectedNotification notification, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Session disconnected");
            // State is removed by the connection's own ordered stream (SessionEnded).
            _monitor.NotifyChanged();
            return Task.CompletedTask;
        }
    }
}