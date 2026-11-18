namespace DSC.TLink.ITv2;

/// <summary>
/// Timeouts and intervals for an <see cref="ITv2Session"/>. Production uses <see cref="Default"/>;
/// tests pass shorter values.
/// </summary>
internal sealed record ITv2SessionTimings
{
    public static ITv2SessionTimings Default { get; } = new();

    /// <summary>
    /// Longest a send waits for readiness, its turn and the panel's reply, unless the caller's
    /// token fires first. Without it a panel that never answers hangs the caller forever.
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A peer that connects but never completes the handshake is dropped after this.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Panels send a burst of buffered messages on connect. The session becomes ready after
    /// this long with no inbound traffic.
    /// </summary>
    public TimeSpan ReadyQuietPeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A panel that never goes quiet is declared ready after this anyway.</summary>
    public TimeSpan MaxReadyWait { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>How long one heartbeat poll waits for any reply.</summary>
    public TimeSpan HeartbeatAckTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive silent polls before the session is closed.</summary>
    public int MaxMissedHeartbeats { get; init; } = 3;

    /// <summary>Bound on END_SESSION, waiting for the receive pump and the write lock during dispose.</summary>
    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
