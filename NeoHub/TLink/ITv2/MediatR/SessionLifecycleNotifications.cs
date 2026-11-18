using MediatR;

namespace DSC.TLink.ITv2.MediatR
{
    /// <param name="ConnectionLifetime">Cancelled when this particular connection ends (including being replaced by a reconnect).</param>
    public record SessionConnectedNotification(string SessionId, CancellationToken ConnectionLifetime = default) : INotification;
    public record SessionDisconnectedNotification(string SessionId) : INotification;
}