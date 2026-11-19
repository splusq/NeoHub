using DSC.TLink.ITv2.Messages;

namespace DSC.TLink.ITv2.Sessions
{
    /// <summary>
    /// Command to send a message to a specific panel session.
    /// Sent with <see cref="IPanelSender"/>.
    /// </summary>
    public record SessionCommand
    {
        public required string SessionID { get; init; }
        public required IMessageData MessageData { get; init; }
    }
}
