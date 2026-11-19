using NeoHub.Services;
using NeoHub.Services.Models;

namespace NeoHub.Domain
{
    /// <summary>
    /// Waits for a panel's state to meet a condition, without polling: it reads the store's change
    /// stream. Create it <em>before</em> doing what should cause the change (e.g. sending
    /// ConfigurationEnter), so the change can't slip past between the action and the wait. The
    /// timeout starts when it is created, too, so it covers the action and is never set late.
    /// </summary>
    public sealed class SessionWatch : IDisposable
    {
        private readonly IPanelStateService _state;
        private readonly string _sessionId;
        private readonly Func<SessionState, bool> _condition;
        private readonly PanelSubscription _subscription;
        private readonly CancellationTokenSource _expiry;

        public SessionWatch(IPanelStateService state, string sessionId, Func<SessionState, bool> condition, TimeSpan timeout, TimeProvider time)
        {
            _state = state;
            _sessionId = sessionId;
            _condition = condition;
            _expiry = new CancellationTokenSource(timeout, time);
            _subscription = state.Subscribe();
        }

        /// <summary>
        /// True once the condition holds; false if the timeout passes first or the session goes
        /// away. Throws only if <paramref name="ct"/> is cancelled.
        /// </summary>
        public async Task<bool> WaitAsync(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _expiry.Token);
            try
            {
                await foreach (var change in _subscription.Changes.ReadAllAsync(linked.Token))
                {
                    switch (change)
                    {
                        // The snapshot as of subscribing (or of resubscribing).
                        case Resynchronize when _state.GetSession(_sessionId) is { } now && _condition(now):
                            return true;
                        case SessionChanged changed when Same(changed.SessionId) && _condition(changed.Session):
                            return true;
                        case SessionRemoved removed when Same(removed.SessionId):
                            return false;
                    }
                }
                return false;   // the store stopped
            }
            catch (SubscriberOverflowException)
            {
                return _state.GetSession(_sessionId) is { } now && _condition(now);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;   // timed out
            }
        }

        private bool Same(string sessionId) => string.Equals(sessionId, _sessionId, StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            _subscription.Dispose();
            _expiry.Dispose();
        }
    }
}
