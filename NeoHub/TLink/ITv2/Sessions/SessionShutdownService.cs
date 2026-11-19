using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DSC.TLink.ITv2.Sessions
{
    /// <summary>
    /// Says goodbye to every panel (END_SESSION) as soon as the host starts shutting down.
    /// <para>
    /// This runs in <see cref="StoppingAsync"/>, which the host calls before stopping <em>any</em>
    /// service, the web server included. A plain <see cref="IHostedService.StopAsync"/> would run
    /// only after Kestrel had stopped, and Kestrel first waits (30 s by default) for open
    /// connections, the panel's among them, to drain; the container was killed before the panel
    /// ever heard a goodbye, and it then took minutes to reconnect.
    /// </para>
    /// </summary>
    internal sealed class SessionShutdownService : IHostedLifecycleService
    {
        /// <summary>Longest the goodbye may hold up shutdown.</summary>
        internal static readonly TimeSpan GoodbyeTimeout = TimeSpan.FromSeconds(5);

        private readonly IITv2SessionManager _sessionManager;
        private readonly TimeProvider _time;
        private readonly ILogger<SessionShutdownService> _logger;

        public SessionShutdownService(IITv2SessionManager sessionManager, TimeProvider time, ILogger<SessionShutdownService> logger)
        {
            _sessionManager = sessionManager;
            _time = time;
            _logger = logger;
        }

        public async Task StoppingAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Application shutting down — disconnecting all sessions");
            try
            {
                await _sessionManager.DisconnectAllAsync().WaitAsync(GoodbyeTimeout, _time, cancellationToken);
                _logger.LogInformation("All panel sessions closed");
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Panel sessions did not close within {Timeout}; continuing shutdown", GoodbyeTimeout);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error disconnecting sessions during shutdown");
            }
        }

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
