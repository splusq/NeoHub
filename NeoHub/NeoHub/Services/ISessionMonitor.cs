namespace NeoHub.Services
{
    /// <summary>Signals that the set of panels or their settings changed.</summary>
    public interface ISessionMonitor
    {
        void NotifyChanged();
    }

    /// <summary>Posts the signal into the state store, so it reaches subscribers in order with everything else.</summary>
    public class SessionMonitor(IPanelStateService store) : ISessionMonitor
    {
        public void NotifyChanged() => store.Dispatch(new Domain.SessionsListChanged());
    }
}
