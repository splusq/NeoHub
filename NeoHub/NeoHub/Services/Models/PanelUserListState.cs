using System.Collections.Immutable;

namespace NeoHub.Services.Models;

/// <summary>
/// Immutable. Wraps everything that makes up "the panel's user list as NeoHub knows it" — the slots
/// themselves plus read-operation metadata. One instance per session, stored on
/// <see cref="SessionState.UserList"/>. Parallels <see cref="PanelConfiguration.PanelConfigurationState"/>.
/// </summary>
/// <remarks>
/// The panel-level capability <see cref="SessionState.MaxUsers"/> stays on the session because it
/// is a capability fact reported at handshake time, not scoped to the user-read operation.
/// </remarks>
public sealed record PanelUserListState
{
    /// <summary>
    /// All user slots keyed by 1-based <see cref="PanelUserState.UserIndex"/>. Immutable: the UI
    /// can enumerate it while a read is in progress, because a read replaces it instead.
    /// </summary>
    public ImmutableDictionary<int, PanelUserState> Users { get; init; } = ImmutableDictionary<int, PanelUserState>.Empty;

    /// <summary>Wall-clock time of the last successful full read.</summary>
    public DateTime? LastReadAt { get; init; }

    // ── Read progress (cleared by PanelUserService at end of ReadAllAsync) ──

    public bool IsReading { get; init; }
    public int ReadCurrent { get; init; }
    public int ReadTotal { get; init; }
    public string? ReadProgress { get; init; }
}
