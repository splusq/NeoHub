using DSC.TLink.ITv2.Messages;

namespace DSC.TLink.ITv2;

/// <summary>A notification from a panel, stamped with when its frame arrived and which connection it came on.</summary>
public sealed record PanelMessage(string SessionId, Guid ConnectionId, IMessageData Message, DateTimeOffset ReceivedAt);

/// <summary>
/// Where a connection delivers its panel's notifications, in arrival order. The application
/// registers one (the panel state store); TLink knows nothing about what it does with them.
/// Accept must not block: it is called from the connection's delivery loop.
/// </summary>
public interface IPanelMessageSink
{
    /// <summary>A connection finished its handshake. Called before it is registered and before any of its messages.</summary>
    void Started(string sessionId, Guid connectionId, DateTimeOffset at);

    /// <summary>A notification, in arrival order.</summary>
    void Accept(PanelMessage message);

    /// <summary>The connection ended. Called after its last message.</summary>
    void Ended(string sessionId, Guid connectionId);
}
