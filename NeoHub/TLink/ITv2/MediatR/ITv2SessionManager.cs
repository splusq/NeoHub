using System.Collections.Concurrent;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DSC.TLink.ITv2.MediatR
{
    /// <summary>
    /// Manages active ITv2 session instances and routes commands to the correct session.
    /// </summary>
    public interface IITv2SessionManager
    {
        internal void RegisterSession(string sessionId, IITv2Session session);
        internal void UnregisterSession(string sessionId, IITv2Session session);
        internal IITv2Session? GetSession(string sessionId);
        IEnumerable<string> GetActiveSessions();
        Task DisconnectSessionAsync(string sessionId);
        Task DisconnectAllAsync();
    }

    internal class ITv2SessionManager : IITv2SessionManager
    {
        private readonly ConcurrentDictionary<string, IITv2Session> _sessions = new();
        private readonly IMediator _mediator;
        private readonly ILogger<ITv2SessionManager> _logger;

        public ITv2SessionManager(IMediator mediator, ILogger<ITv2SessionManager> logger)
        {
            _mediator = mediator;
            _logger = logger;
        }

        /// <summary>
        /// Register a newly connected session. A panel that reconnects (for example after a
        /// network outage) reuses its session ID while the old, half-open connection may still
        /// be registered. The newest connection always wins: the stale one is replaced and
        /// disposed, so commands and state go to the connection the panel is actually using.
        /// </summary>
        public void RegisterSession(string sessionId, IITv2Session session)
        {
            IITv2Session? stale = null;
            bool alreadyRegistered = false;
            _sessions.AddOrUpdate(sessionId, session, (_, existing) =>
            {
                alreadyRegistered = ReferenceEquals(existing, session);
                stale = alreadyRegistered ? null : existing;
                return session;
            });

            if (alreadyRegistered)
                return;

            if (stale is null)
            {
                _logger.LogInformation("Session registered. Active sessions: {Count}",
                    _sessions.Count);
                PublishLifecycleNotification(new SessionConnectedNotification(sessionId));
                return;
            }

            _logger.LogWarning("Panel reconnected while a previous session was still registered; replacing the stale session");

            // Reset state for the old connection before the new one starts its configuration pull.
            PublishLifecycleNotifications(
                new SessionDisconnectedNotification(sessionId),
                new SessionConnectedNotification(sessionId));

            _ = DisposeStaleSessionAsync(stale);
        }

        /// <summary>
        /// Unregister a session when its connection closes. Only removes the entry if it still
        /// belongs to this session instance; a superseded session closing must not remove the
        /// live connection that replaced it.
        /// </summary>
        public void UnregisterSession(string sessionId, IITv2Session session)
        {
            if (_sessions.TryRemove(new KeyValuePair<string, IITv2Session>(sessionId, session)))
            {
                _logger.LogInformation("Session unregistered. Active sessions: {Count}",
                    _sessions.Count);
                PublishLifecycleNotification(new SessionDisconnectedNotification(sessionId));
            }
            else
            {
                _logger.LogInformation("Superseded session closed; the current session stays registered");
            }
        }

        private async Task DisposeStaleSessionAsync(IITv2Session stale)
        {
            try
            {
                await stale.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing stale session");
            }
        }

        public IITv2Session? GetSession(string sessionId)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? session : null;
        }

        public async Task DisconnectSessionAsync(string sessionId)
        {
            if (_sessions.TryRemove(sessionId, out var session))
            {
                _logger.LogInformation("Disconnecting session {SessionId}", sessionId);
                await session.DisposeAsync();
                PublishLifecycleNotification(new SessionDisconnectedNotification(sessionId));
            }
        }

        public async Task DisconnectAllAsync()
        {
            var sessionIds = _sessions.Keys.ToList();
            if (sessionIds.Count == 0)
                return;

            _logger.LogInformation("Disconnecting all {Count} active session(s)", sessionIds.Count);
            await Task.WhenAll(sessionIds.Select(DisconnectSessionAsync));
        }

        public IEnumerable<string> GetActiveSessions()
        {
            return _sessions.Keys.ToList();
        }

        private void PublishLifecycleNotification(INotification notification)
            => PublishLifecycleNotifications(notification);

        /// <summary>
        /// Publish notifications in order, without blocking the caller.
        /// </summary>
        private async void PublishLifecycleNotifications(params INotification[] notifications)
        {
            foreach (var notification in notifications)
            {
                try
                {
                    await _mediator.Publish(notification);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error publishing session lifecycle notification");
                }
            }
        }
    }
}