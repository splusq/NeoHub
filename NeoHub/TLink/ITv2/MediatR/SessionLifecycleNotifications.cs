using MediatR;

namespace DSC.TLink.ITv2.MediatR
{
    /// <param name="ConnectionLifetime">Cancelled when this particular connection ends (including being replaced by a reconnect).</param>
    /// <param name="ConnectionId">Which connection this is; state updates for it carry this ID so a replaced connection can't touch the new one's state.</param>
    public record SessionConnectedNotification(string SessionId, CancellationToken ConnectionLifetime = default, Guid ConnectionId = default) : INotification;
    public record SessionDisconnectedNotification(string SessionId) : INotification;
}