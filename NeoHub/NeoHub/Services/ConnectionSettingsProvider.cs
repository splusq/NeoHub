using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using Microsoft.Extensions.Options;
using NeoHub.Services.Settings;

namespace NeoHub.Services;

/// <summary>
/// Resolves per-connection settings from <see cref="PanelConnectionsSettings.Connections"/>.
/// Resolving only reads; anything it learns (an unknown panel, its encryption type, factory
/// defaults that worked) is saved as a change on the settings store's loop, so the settings are
/// never changed from two threads at once.
/// </summary>
public class ConnectionSettingsProvider : IConnectionSettingsProvider
{
    private readonly IOptionsMonitor<PanelConnectionsSettings> _settings;
    private readonly ISettingsPersistenceService _persistence;
    private readonly ISessionMonitor _sessionMonitor;
    private readonly ILogger<ConnectionSettingsProvider> _logger;

    public ConnectionSettingsProvider(
        IOptionsMonitor<PanelConnectionsSettings> settings,
        ISettingsPersistenceService persistence,
        ISessionMonitor sessionMonitor,
        ILogger<ConnectionSettingsProvider> logger)
    {
        _settings = settings;
        _persistence = persistence;
        _sessionMonitor = sessionMonitor;
        _logger = logger;
    }

    public ConnectionSettings? ResolveConnection(string sessionId, EncryptionType encryptionType)
    {
        var existing = _settings.CurrentValue.FindBySessionId(sessionId);

        if (existing == null)
        {
            _logger.LogWarning(
                "Unknown panel {SessionId} (encryption: {EncryptionType}). Creating placeholder connection entry.",
                sessionId, encryptionType);
            Persist(s => ConnectionSettingsRules.AddPlaceholder(s, sessionId, encryptionType), notify: true);
            existing = new ConnectionSettings { SessionId = sessionId, EncryptionType = encryptionType };
        }
        else if (encryptionType != EncryptionType.Unknown && existing.EncryptionType != encryptionType)
        {
            _logger.LogInformation(
                "Updating encryption type for {SessionId} from {Old} to {New}",
                sessionId, existing.EncryptionType, encryptionType);
            Persist(s => ConnectionSettingsRules.SetEncryptionType(s, sessionId, encryptionType), notify: false);
        }

        var resolved = ConnectionSettingsRules.Resolve(existing, encryptionType);
        switch (resolved.Outcome)
        {
            case ResolveOutcome.Incomplete:
                _logger.LogWarning("Connection settings for {SessionId} are incomplete. Please configure encryption keys.", sessionId);
                break;
            case ResolveOutcome.Configured:
                _logger.LogInformation("Resolved connection settings for session {SessionId}", sessionId);
                break;
            case ResolveOutcome.FactoryDefault:
                _logger.LogInformation("Connection settings for {SessionId} are incomplete. Trying factory default encryption key.", sessionId);
                break;
        }
        return resolved.Settings;
    }

    public void ConfirmDefaults(string sessionId)
    {
        var existing = _settings.CurrentValue.FindBySessionId(sessionId);
        if (existing == null || existing.IsComplete)
            return;
        _logger.LogInformation("Factory defaults confirmed for {SessionId} — persisting to settings", sessionId);
        Persist(s => ConnectionSettingsRules.ConfirmDefaults(s, sessionId), notify: false);
    }

    // Not awaited: the handshake must not wait on disk. The store applies changes in order.
    private void Persist(Func<PanelConnectionsSettings, bool> change, bool notify) => _ = SaveAsync(change, notify);

    private async Task SaveAsync(Func<PanelConnectionsSettings, bool> change, bool notify)
    {
        try
        {
            var changed = await _persistence.UpdateAsync(change);
            if (changed && notify)
                _sessionMonitor.NotifyChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist connection settings");
        }
    }
}

public enum ResolveOutcome { Configured, FactoryDefault, Incomplete }

public sealed record Resolution(ConnectionSettings? Settings, ResolveOutcome Outcome);

/// <summary>The decisions behind <see cref="ConnectionSettingsProvider"/>, as plain functions.</summary>
public static class ConnectionSettingsRules
{
    /// <summary>
    /// The settings to try for a handshake, always a copy (never the shared settings object), with
    /// the encryption type the panel actually uses: the stored keys when they are complete for it;
    /// the factory default key when that key is missing; otherwise none.
    /// </summary>
    public static Resolution Resolve(ConnectionSettings existing, EncryptionType encryptionType)
    {
        var type = encryptionType == EncryptionType.Unknown ? existing.EncryptionType : encryptionType;
        var effective = Copy(existing, type);
        if (effective.IsComplete)
            return new(effective, ResolveOutcome.Configured);
        if (effective.HasEncryptionKey(type))
            return new(null, ResolveOutcome.Incomplete);   // has the key; something else is missing
        return new(new ConnectionSettings
        {
            SessionId = existing.SessionId,
            EncryptionType = type,
            IntegrationAccessCodeType1 = NullIfBlank(existing.IntegrationAccessCodeType1)
                ?? (type == EncryptionType.Type1 ? ConnectionSettings.FactoryDefaultType1 : null),
            IntegrationAccessCodeType2 = NullIfBlank(existing.IntegrationAccessCodeType2)
                ?? (type == EncryptionType.Type2 ? ConnectionSettings.FactoryDefaultType2 : null),
            DefaultAccessCode = existing.DefaultAccessCode,
            InstallerCode = existing.InstallerCode,
            MasterCode = existing.MasterCode,
            MaxZones = existing.MaxZones
        }, ResolveOutcome.FactoryDefault);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static ConnectionSettings Copy(ConnectionSettings c, EncryptionType type) => new()
    {
        SessionId = c.SessionId,
        EncryptionType = type,
        IntegrationAccessCodeType1 = c.IntegrationAccessCodeType1,
        IntegrationAccessCodeType2 = c.IntegrationAccessCodeType2,
        DefaultAccessCode = c.DefaultAccessCode,
        InstallerCode = c.InstallerCode,
        MasterCode = c.MasterCode,
        MaxZones = c.MaxZones,
    };

    public static bool AddPlaceholder(PanelConnectionsSettings settings, string sessionId, EncryptionType encryptionType)
    {
        if (settings.FindBySessionId(sessionId) is not null)
            return false;
        // A new list, so anyone enumerating the old one is unaffected.
        settings.Connections = [.. settings.Connections, new ConnectionSettings { SessionId = sessionId, EncryptionType = encryptionType }];
        return true;
    }

    public static bool SetEncryptionType(PanelConnectionsSettings settings, string sessionId, EncryptionType encryptionType)
    {
        if (settings.FindBySessionId(sessionId) is not { } conn || conn.EncryptionType == encryptionType)
            return false;
        conn.EncryptionType = encryptionType;
        return true;
    }

    public static bool ConfirmDefaults(PanelConnectionsSettings settings, string sessionId)
    {
        if (settings.FindBySessionId(sessionId) is not { IsComplete: false } conn)
            return false;
        var changed = false;
        if (string.IsNullOrWhiteSpace(conn.IntegrationAccessCodeType1) && conn.EncryptionType == EncryptionType.Type1)
        {
            conn.IntegrationAccessCodeType1 = ConnectionSettings.FactoryDefaultType1;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(conn.IntegrationAccessCodeType2) && conn.EncryptionType == EncryptionType.Type2)
        {
            conn.IntegrationAccessCodeType2 = ConnectionSettings.FactoryDefaultType2;
            changed = true;
        }
        return changed;
    }
}
