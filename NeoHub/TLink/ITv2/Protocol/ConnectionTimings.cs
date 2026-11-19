namespace DSC.TLink.ITv2.Protocol;

/// <summary>Timeouts and intervals for a connection. Production uses <see cref="Default"/>.</summary>
internal sealed record ConnectionTimings
{
    public static ConnectionTimings Default { get; } = new();

    /// <summary>A peer that connects but doesn't finish the handshake is dropped after this.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>After the handshake the panel sends a burst; ready after this long with no traffic.</summary>
    public TimeSpan ReadyQuietPeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A panel that never goes quiet is declared ready after this anyway.</summary>
    public TimeSpan MaxReadyWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Longest a request waits for its turn and the panel's reply, once ready.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Time from one heartbeat poll finishing to the next being sent.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>How long a heartbeat poll waits for the panel's ack.</summary>
    public TimeSpan HeartbeatAckTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive polls with no traffic at all before the connection is closed.</summary>
    public int MaxMissedHeartbeats { get; init; } = 3;
}
