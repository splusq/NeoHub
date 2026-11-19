using System.Collections.Frozen;
using DSC.TLink.Concurrency;
using DSC.TLink.ITv2;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace NeoHub.Services.Diagnostics
{
    /// <summary>
    /// Custom logger provider that feeds logs into the diagnostics service.
    /// Supports per-category log level overrides. Non-solution categories are
    /// clamped to the floor defined in appsettings.json Logging:LogLevel so
    /// verbose levels (Trace/Debug) only apply to solution code.
    /// </summary>
    public class DiagnosticsLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private static readonly string[] SolutionPrefixes = ["NeoHub", "DSC.TLink"];

        private readonly IDiagnosticsLogService _diagnosticsService;
        private readonly IOptionsMonitor<DiagnosticsSettings> _settings;
        private readonly IConfiguration _configuration;
        private readonly TimeProvider _time;
        // The Logging:LogLevel table, parsed once per configuration (re)load. Immutable; replaced
        // whole by the reload callback, its only writer.
        private readonly Snapshot<LogLevelTable> _levels;
        private readonly IDisposable? _configChangeRegistration;
        private IExternalScopeProvider? _scopeProvider;

        public DiagnosticsLoggerProvider(
            IDiagnosticsLogService diagnosticsService,
            IOptionsMonitor<DiagnosticsSettings> settings,
            IConfiguration configuration,
            TimeProvider time)
        {
            _diagnosticsService = diagnosticsService;
            _settings = settings;
            _configuration = configuration;
            _time = time;
            _levels = new(LogLevelTable.From(configuration));

            _configChangeRegistration = ChangeToken.OnChange(
                () => _configuration.GetReloadToken(),
                () => _levels.Publish(LogLevelTable.From(_configuration)));
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new DiagnosticsLogger(categoryName, _diagnosticsService, _settings, this);
        }

        public void Dispose()
        {
            _configChangeRegistration?.Dispose();
        }

        private class DiagnosticsLogger : ILogger
        {
            private readonly string _category;
            private readonly bool _isSolutionCategory;
            private readonly IDiagnosticsLogService _diagnosticsService;
            private readonly IOptionsMonitor<DiagnosticsSettings> _settings;
            private readonly DiagnosticsLoggerProvider _provider;

            public DiagnosticsLogger(
                string category,
                IDiagnosticsLogService diagnosticsService,
                IOptionsMonitor<DiagnosticsSettings> settings,
                DiagnosticsLoggerProvider provider)
            {
                _category = category;
                _isSolutionCategory = IsSolutionCategory(category);
                _diagnosticsService = diagnosticsService;
                _settings = settings;
                _provider = provider;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
                => _provider._scopeProvider?.Push(state);

            public bool IsEnabled(LogLevel logLevel)
            {
                var settings = _settings.CurrentValue;

                // Per-category override takes precedence, otherwise use global minimum
                var effectiveLevel = settings.CategoryOverrides.TryGetValue(_category, out var categoryLevel)
                    ? categoryLevel
                    : settings.MinimumLogLevel;

                // Non-solution categories: clamp to the appsettings floor so
                // verbose levels only ever apply to solution code
                if (!_isSolutionCategory)
                {
                    var configFloor = _provider._levels.Value.For(_category);
                    effectiveLevel = (LogLevel)Math.Max((int)effectiveLevel, (int)configFloor);
                }

                return logLevel >= effectiveLevel;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                    return;

                _diagnosticsService.AddLog(new DiagnosticsLogEntry
                {
                    Timestamp = _provider._time.GetUtcNow().UtcDateTime,
                    LogLevel = logLevel,
                    Category = _category,
                    Message = formatter(state, exception),
                    Exception = exception,
                    SessionId = ExtractSessionId()
                });
            }

            private string? ExtractSessionId()
            {
                string? sessionId = null;
                _provider._scopeProvider?.ForEachScope((scope, _) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object>> kvps)
                    {
                        foreach (var kvp in kvps)
                        {
                            if (kvp.Key == ConnectionSettings.LogScopeKey)
                                sessionId = kvp.Value?.ToString();
                        }
                    }
                }, 0);
                return sessionId;
            }

            private static bool IsSolutionCategory(string category)
            {
                foreach (var prefix in SolutionPrefixes)
                {
                    if (category.StartsWith(prefix, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
        }
    }

    /// <summary>
    /// Logging:LogLevel as data. <see cref="For"/> walks progressively shorter category prefixes,
    /// the same precedence ASP.NET Core uses.
    /// </summary>
    public sealed class LogLevelTable
    {
        private readonly FrozenDictionary<string, LogLevel> _levels;
        private readonly LogLevel _default;

        private LogLevelTable(FrozenDictionary<string, LogLevel> levels, LogLevel defaultLevel)
        {
            _levels = levels;
            _default = defaultLevel;
        }

        public static LogLevelTable From(IConfiguration configuration)
        {
            var levels = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in configuration.GetSection("Logging:LogLevel").GetChildren())
                if (child.Value is { } value && Enum.TryParse<LogLevel>(value, out var level))
                    levels[child.Key] = level;
            var defaultLevel = levels.GetValueOrDefault("Default", LogLevel.Information);
            return new LogLevelTable(levels.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), defaultLevel);
        }

        public LogLevel For(string category)
        {
            var prefix = category;
            while (true)
            {
                if (_levels.TryGetValue(prefix, out var level))
                    return level;
                var lastDot = prefix.LastIndexOf('.');
                if (lastDot < 0)
                    return _default;
                prefix = prefix[..lastDot];
            }
        }
    }
}
