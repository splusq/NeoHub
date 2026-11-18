using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Sessions;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Options;
using NeoHub.Services.Models;
using NeoHub.Services.Settings;

namespace NeoHub.Services;

/// <summary>
/// Identifies which panel access code we're dealing with. Drives:
/// - which field on <see cref="ConnectionSettings"/> is read/persisted
/// - which <see cref="ProgrammingMode"/> is used when entering the panel
/// </summary>
public enum PanelAccessCodeKind
{
    Installer,
    Master,
}

/// <summary>Result of an authenticated panel operation. On failure, <see cref="Result"/> is default.</summary>
public record PanelAccessResult<T>(bool Success, T? Result = default, string? ErrorMessage = null);

/// <summary>
/// Single entry point for everything related to a panel's access codes:
/// <list type="bullet">
/// <item>Format validation (4/6/8 numeric digits)</item>
/// <item>Session-scoped caching (prevents re-prompting for the same code across page visits)</item>
/// <item>Connection settings read/update/persist</item>
/// <item>Authenticated panel operations via <see cref="ExecuteAsync"/> — owns the shared
///       panel-mode lock, enters programming mode with the correct privilege, runs the caller's
///       delegate, and always exits + releases.</item>
/// </list>
/// Pages typically interact via the <c>IDialogService.ResolveAccessCodeAsync</c> extension, which
/// walks the cache → settings → prompt ladder and returns a verified code string.
/// </summary>
public interface IPanelAccessCodeService
{
    // ── Format ───────────────────────────────────────────────────────────────
    bool IsValidFormat(string? code);

    /// <summary>Returns null for valid input, or a human-readable error for invalid.</summary>
    string? ValidateFormat(string? code);

    // ── Connection settings ──────────────────────────────────────────────────

    /// <summary>Returns the configured code for the session (or null if no matching connection / empty).</summary>
    string? GetConnectionCode(string sessionId, PanelAccessCodeKind kind);

    /// <summary>
    /// Updates and persists the matching access code on the connection settings entry whose
    /// <c>SessionId</c> matches (case-insensitive). Also updates the in-memory cache. No-ops if
    /// no matching connection exists or the value is already current.
    /// </summary>
    Task<bool> UpdateConnectionCodeAsync(string sessionId, PanelAccessCodeKind kind, string? newCode);

    // ── Session cache ────────────────────────────────────────────────────────

    bool TryGetCached(string sessionId, PanelAccessCodeKind kind, out string? code);
    void SetCached(string sessionId, PanelAccessCodeKind kind, string? code);
    void InvalidateCache(string sessionId, PanelAccessCodeKind kind);

    // ── Authenticated operations ─────────────────────────────────────────────

    /// <summary>
    /// Runs as one queued panel operation (<see cref="NeoHub.Domain.IPanelOperations"/>), enters the programming mode corresponding
    /// to <paramref name="kind"/> authenticated with <paramref name="code"/>, runs
    /// <paramref name="operation"/>, then always exits programming mode and releases the lock.
    /// The shared lock serializes installer and master operations against each other for the
    /// same session, preventing mode conflicts.
    /// </summary>
    Task<PanelAccessResult<T>> ExecuteAsync<T>(
        string sessionId,
        PanelAccessCodeKind kind,
        string code,
        bool readWrite,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct);

    /// <summary>
    /// Tests whether the panel accepts the given code. Implemented as <see cref="ExecuteAsync"/>
    /// with a no-op operation; on success, caches the code.
    /// </summary>
    Task<bool> VerifyAsync(string sessionId, PanelAccessCodeKind kind, string code, CancellationToken ct);
}

public class PanelAccessCodeService(
    IPanelSender mediator,
    IPanelStateService panelState,
    IOptionsMonitor<PanelConnectionsSettings> connectionSettings,
    ISettingsPersistenceService persistence,
    ILogger<PanelAccessCodeService> logger,
    NeoHub.Domain.IPanelOperations operations,
    TimeProvider time)
    : IPanelAccessCodeService
{
    /// <summary>How long to wait for the panel's programming-mode confirmation (LeadIn).</summary>
    private static readonly TimeSpan ProgrammingModeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Short settle delay after exiting programming mode before re-entering.</summary>
    private static readonly TimeSpan ReEntryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to wait for the panel to confirm it left programming mode (LeadOut).</summary>
    private static readonly TimeSpan LeadOutTimeout = TimeSpan.FromSeconds(3);

    private readonly DSC.TLink.Concurrency.Snapshot<System.Collections.Immutable.ImmutableDictionary<(string SessionId, PanelAccessCodeKind Kind), string>> _cache
        = new(System.Collections.Immutable.ImmutableDictionary.Create<(string SessionId, PanelAccessCodeKind Kind), string>(new CacheKeyComparer()));

    // ── Format ───────────────────────────────────────────────────────────────

    public bool IsValidFormat(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        if (code.Length != 4 && code.Length != 6 && code.Length != 8) return false;
        foreach (var ch in code)
            if (ch < '0' || ch > '9') return false;
        return true;
    }

    public string? ValidateFormat(string? code) =>
        IsValidFormat(code) ? null : "Access code must be 4, 6, or 8 digits";

    // ── Connection settings ──────────────────────────────────────────────────

    public string? GetConnectionCode(string sessionId, PanelAccessCodeKind kind)
    {
        var conn = FindConnection(sessionId);
        var value = conn is null ? null : kind switch
        {
            PanelAccessCodeKind.Installer => conn.InstallerCode,
            PanelAccessCodeKind.Master => conn.MasterCode,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public async Task<bool> UpdateConnectionCodeAsync(string sessionId, PanelAccessCodeKind kind, string? newCode)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;

        var normalized = string.IsNullOrEmpty(newCode) ? null : newCode;

        // Changed and saved on the settings store's loop, never alongside another change.
        var changed = await persistence.UpdateAsync<PanelConnectionsSettings>(settings =>
        {
            var conn = settings.FindBySessionId(sessionId);
            if (conn is null) return false;
            var current = kind switch
            {
                PanelAccessCodeKind.Installer => conn.InstallerCode,
                PanelAccessCodeKind.Master => conn.MasterCode,
                _ => null,
            };
            if (current == normalized) return false;
            switch (kind)
            {
                case PanelAccessCodeKind.Installer: conn.InstallerCode = normalized; break;
                case PanelAccessCodeKind.Master: conn.MasterCode = normalized; break;
            }
            return true;
        });
        if (!changed) return false;

        // Keep cache coherent with the persisted value.
        if (string.IsNullOrEmpty(normalized))
            InvalidateCache(sessionId, kind);
        else
            SetCached(sessionId, kind, normalized);

        logger.LogInformation(
            "Updated {Kind} code in connection settings for session {SessionId}", kind, sessionId);
        return true;
    }

    // ── Cache ────────────────────────────────────────────────────────────────

    public bool TryGetCached(string sessionId, PanelAccessCodeKind kind, out string? code)
    {
        if (_cache.Value.TryGetValue((sessionId, kind), out var cached))
        {
            code = cached;
            return true;
        }
        code = null;
        return false;
    }

    public void SetCached(string sessionId, PanelAccessCodeKind kind, string? code)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(code))
        {
            InvalidateCache(sessionId, kind);
            return;
        }
        _cache.Update(c => c.SetItem((sessionId, kind), code));
    }

    public void InvalidateCache(string sessionId, PanelAccessCodeKind kind) =>
        _cache.Update(c => c.Remove((sessionId, kind)));

    // ── Authenticated operations ─────────────────────────────────────────────

    public async Task<PanelAccessResult<T>> ExecuteAsync<T>(
        string sessionId,
        PanelAccessCodeKind kind,
        string code,
        bool readWrite,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code))
            return new PanelAccessResult<T>(false, default, "Access code is required");

        if (panelState.GetSession(sessionId) is null)
            return new PanelAccessResult<T>(false, default, "Session not found");

        // One panel operation for the full scope: verify, reads and writes for the same panel
        // never fight over its single programming-mode slot (or interleave with the pull, a
        // configuration write or a bypass).
        return await operations.RunAsync(sessionId, $"{kind} code operation", async opCt =>
        {
            var mode = MapProgrammingMode(kind);

            if (!await EnterProgrammingModeAsync(sessionId, mode, code, readWrite, opCt))
                return new PanelAccessResult<T>(false, default, $"Panel rejected {kind} code");

            try
            {
                var result = await operation(opCt);
                return new PanelAccessResult<T>(true, result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Authenticated operation threw for {Kind} on session {SessionId}", kind, sessionId);
                return new PanelAccessResult<T>(false, default, ex.Message);
            }
            finally
            {
                await ExitProgrammingModeAsync(sessionId, opCt);
            }
        }, ct);
    }

    public async Task<bool> VerifyAsync(string sessionId, PanelAccessCodeKind kind, string code, CancellationToken ct)
    {
        var result = await ExecuteAsync(
            sessionId, kind, code, readWrite: false,
            operation: _ => Task.FromResult(true),
            ct);

        if (result.Success)
            SetCached(sessionId, kind, code);

        return result.Success;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private ConnectionSettings? FindConnection(string? sessionId) =>
        connectionSettings.CurrentValue.FindBySessionId(sessionId);

    private static ProgrammingMode MapProgrammingMode(PanelAccessCodeKind kind) => kind switch
    {
        PanelAccessCodeKind.Installer => ProgrammingMode.InstallersProgramming,
        PanelAccessCodeKind.Master => ProgrammingMode.AccessCodeProgramming,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Sends <see cref="ConfigurationEnter"/> and waits for the panel to confirm programming mode
    /// via the LeadIn notification. Exits first if the panel is already in programming mode (avoids
    /// the "partition busy" error).
    /// </summary>
    private async Task<bool> EnterProgrammingModeAsync(
        string sessionId, ProgrammingMode mode, string code, bool readWrite, CancellationToken ct)
    {
        // Read now (not before the operation was queued): state is an immutable snapshot.
        if (panelState.GetSession(sessionId)?.IsInProgrammingMode == true)
        {
            logger.LogDebug("Panel already in programming mode on {SessionId}, exiting before re-enter", sessionId);
            using (var leadOut = new NeoHub.Domain.SessionWatch(panelState, sessionId, s => !s.IsInProgrammingMode, LeadOutTimeout, time))
            {
                await ExitProgrammingModeAsync(sessionId, ct);
                if (!await leadOut.WaitAsync(ct))
                    logger.LogDebug("No LeadOut within {Timeout} on {SessionId}; re-entering anyway", LeadOutTimeout, sessionId);
            }
            await Task.Delay(ReEntryDelay, time, ct);
        }

        logger.LogInformation("Entering {Mode} mode on {SessionId}", mode, sessionId);

        // Watching starts before the request, so a LeadIn that beats the reply is not missed.
        using var leadIn = new NeoHub.Domain.SessionWatch(panelState, sessionId, s => s.IsInProgrammingMode, ProgrammingModeTimeout, time);

        var enterResponse = await mediator.Send(new SessionCommand
        {
            SessionID = sessionId,
            MessageData = new ConfigurationEnter
            {
                Partition = 1,
                ProgrammingMode = mode,
                AccessCode = code,
                ReadWrite = readWrite
                    ? ConfigurationEnter.ReadWriteAccessEnum.ReadWriteMode
                    : ConfigurationEnter.ReadWriteAccessEnum.ReadOnlyMode
            }
        }, ct);

        if (!enterResponse.Success)
        {
            logger.LogWarning("Failed to enter {Mode} on {SessionId}: {Error}",
                mode, sessionId, enterResponse.ErrorMessage);
            return false;
        }

        // Wait for LeadIn. Without this, the panel sometimes rejects the first real command
        // sent inside the scope with a spurious "not ready" error.
        if (!await leadIn.WaitAsync(ct))
        {
            logger.LogWarning("Timed out waiting for LeadIn after {Seconds}s on {SessionId}",
                ProgrammingModeTimeout.TotalSeconds, sessionId);
            await ExitProgrammingModeAsync(sessionId, ct);
            return false;
        }

        logger.LogDebug("Panel confirmed {Mode} on {SessionId}", mode, sessionId);
        return true;
    }

    /// <summary>Best-effort exit. Swallows and logs any failure since we never want cleanup to throw.</summary>
    private async Task ExitProgrammingModeAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await mediator.Send(new SessionCommand
            {
                SessionID = sessionId,
                MessageData = new ConfigurationExit { Partition = 1 }
            }, ct);
            logger.LogDebug("Exited programming mode on {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to exit programming mode on {SessionId}", sessionId);
        }
    }

    /// <summary>Case-insensitive equality for the session-id portion of the cache key.</summary>
    private sealed class CacheKeyComparer : IEqualityComparer<(string SessionId, PanelAccessCodeKind Kind)>
    {
        public bool Equals((string SessionId, PanelAccessCodeKind Kind) x, (string SessionId, PanelAccessCodeKind Kind) y)
            => x.Kind == y.Kind && string.Equals(x.SessionId, y.SessionId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string SessionId, PanelAccessCodeKind Kind) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SessionId), obj.Kind);
    }
}
