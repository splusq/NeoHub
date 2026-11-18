using MediatR;

namespace DSC.TLink.ITv2.MediatR
{
    /// <param name="SessionId">The panel's integration ID.</param>
    /// <param name="ConnectionLifetime">
    /// Cancelled when this particular connection ends (including being replaced by a reconnect
    /// with the same ID). Work started for this connection should stop when it fires, so it
    /// never runs against, or writes state for, the connection that replaced it.
    /// </param>
    public record SessionConnectedNotification(string SessionId, CancellationToken ConnectionLifetime = default) : INotification;
    public record SessionDisconnectedNotification(string SessionId) : INotification;
}
