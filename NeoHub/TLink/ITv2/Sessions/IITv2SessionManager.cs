namespace DSC.TLink.ITv2.Sessions
{
    /// <summary>
    /// The application's view of live panel connections. Implemented by
    /// <see cref="Runtime.PanelRegistry"/>.
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
}
