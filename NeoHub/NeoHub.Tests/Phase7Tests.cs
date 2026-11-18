using System.Text.Json.Nodes;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using DSC.TLink.ITv2.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NeoHub.Domain;
using NeoHub.Services;
using NeoHub.Services.Diagnostics;
using NeoHub.Services.Settings;

namespace NeoHub.Tests;

// ── Settings store ──

public class SettingsStoreTests : IAsyncLifetime
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);   // hang guard only

    public sealed class Counter { public const string SectionName = "Counter"; public int N { get; set; } }
    public sealed class Note { public const string SectionName = "Note"; public string Text { get; set; } = ""; }

    private readonly string _dir = Directory.CreateTempSubdirectory("neohub-settings-").FullName;
    private readonly Counter _counter = new();
    private readonly Note _note = new();
    private SettingsPersistenceService _store = null!;

    public Task InitializeAsync()
    {
        _store = new SettingsPersistenceService(_dir, t => t == typeof(Counter) ? _counter : t == typeof(Note) ? _note : throw new ArgumentException(t.Name),
            NullLogger<SettingsPersistenceService>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private JsonObject File() => (JsonObject)JsonNode.Parse(System.IO.File.ReadAllText(_store.SettingsFilePath))!;

    [Fact]
    public async Task ConcurrentChanges_AreAppliedOneAtATime_NoneLost()
    {
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => _store.UpdateAsync<Counter>(c => { c.N++; return true; }))))
            .WaitAsync(Guard);

        Assert.Equal(200, _counter.N);
        Assert.Equal(200, File()["Counter"]!["N"]!.GetValue<int>());
    }

    [Fact]
    public async Task ConcurrentSavesOfDifferentSections_AllLand_AndLeaveNoTempFiles()
    {
        System.IO.File.WriteAllText(Path.Combine(_dir, "userSettings.json"), """{ "Logging": { "LogLevel": { "Default": "Warning" } } }""");

        await Task.WhenAll(
            Task.Run(() => _store.SaveSettingsAsync(typeof(Note), new Note { Text = "hello" })),
            Task.Run(() => _store.UpdateAsync<Counter>(c => { c.N = 7; return true; }))).WaitAsync(Guard);

        var file = File();
        Assert.Equal("hello", file["Note"]!["Text"]!.GetValue<string>());
        Assert.Equal(7, file["Counter"]!["N"]!.GetValue<int>());
        Assert.Equal("Warning", file["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>());   // untouched sections survive
        Assert.Equal(["userSettings.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Save_CapturesTheValueWhenCalled()
    {
        var note = new Note { Text = "before" };
        var saved = _store.SaveSettingsAsync(typeof(Note), note);
        note.Text = "after";
        await saved.WaitAsync(Guard);

        Assert.Equal("before", File()["Note"]!["Text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChangeThatChangesNothing_WritesNothing()
    {
        Assert.False(await _store.UpdateAsync<Counter>(_ => false).WaitAsync(Guard));
        Assert.False(System.IO.File.Exists(_store.SettingsFilePath));
    }

    [Fact]
    public async Task FailingChange_FailsOnlyItsCaller()
    {
        var bad = _store.UpdateAsync<Counter>(_ => throw new InvalidOperationException("bug"));
        var good = _store.UpdateAsync<Counter>(c => { c.N = 1; return true; });

        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.WaitAsync(Guard));
        Assert.True(await good.WaitAsync(Guard));
        Assert.Equal(1, File()["Counter"]!["N"]!.GetValue<int>());
    }
}

// ── Connection settings decisions ──

public class ConnectionSettingsRulesTests
{
    private static ConnectionSettings Conn(EncryptionType type, string? key1 = null, string? key2 = null)
        => new() { SessionId = "200000000001", EncryptionType = type, IntegrationAccessCodeType1 = key1, IntegrationAccessCodeType2 = key2 };

    [Fact]
    public void Complete_IsUsed_AsACopy()
    {
        var stored = Conn(EncryptionType.Type1, key1: "11112222");
        var r = ConnectionSettingsRules.Resolve(stored, EncryptionType.Type1);

        Assert.Equal(ResolveOutcome.Configured, r.Outcome);
        Assert.NotSame(stored, r.Settings);
        Assert.Equal("11112222", r.Settings!.IntegrationAccessCodeType1);
    }

    [Fact]
    public void PanelSwitchedEncryptionType_UsesThePanelsType()
    {
        // Stored as Type1, but the panel now speaks Type2 and the Type2 key is configured.
        var stored = Conn(EncryptionType.Type1, key1: "11112222", key2: "AB".PadRight(32, 'C'));
        var r = ConnectionSettingsRules.Resolve(stored, EncryptionType.Type2);

        Assert.Equal(ResolveOutcome.Configured, r.Outcome);
        Assert.Equal(EncryptionType.Type2, r.Settings!.EncryptionType);
        Assert.Equal(EncryptionType.Type1, stored.EncryptionType);   // the shared object is not touched
    }

    [Fact]
    public void MissingKey_TriesTheFactoryDefault()
    {
        var r = ConnectionSettingsRules.Resolve(Conn(EncryptionType.Type2, key2: "  "), EncryptionType.Type2);

        Assert.Equal(ResolveOutcome.FactoryDefault, r.Outcome);
        Assert.Equal(ConnectionSettings.FactoryDefaultType2, r.Settings!.IntegrationAccessCodeType2);
    }

    [Fact]
    public void KeyPresentButIncomplete_ResolvesToNothing()
    {
        var r = ConnectionSettingsRules.Resolve(new ConnectionSettings { SessionId = "", IntegrationAccessCodeType1 = "11112222" }, EncryptionType.Type1);
        Assert.Equal(ResolveOutcome.Incomplete, r.Outcome);
        Assert.Null(r.Settings);
    }

    [Fact]
    public void AddPlaceholder_IsIdempotent_AndReplacesTheList()
    {
        var settings = new PanelConnectionsSettings();
        var before = settings.Connections;

        Assert.True(ConnectionSettingsRules.AddPlaceholder(settings, "200000000001", EncryptionType.Type1));
        Assert.False(ConnectionSettingsRules.AddPlaceholder(settings, "200000000001", EncryptionType.Type1));

        Assert.Single(settings.Connections);
        Assert.Empty(before);   // anyone holding the old list still sees it unchanged
    }

    [Fact]
    public void ConfirmDefaults_FillsOnlyTheMissingKeyForItsType()
    {
        var settings = new PanelConnectionsSettings { Connections = [Conn(EncryptionType.Type1)] };

        Assert.True(ConnectionSettingsRules.ConfirmDefaults(settings, "200000000001"));
        Assert.Equal(ConnectionSettings.FactoryDefaultType1, settings.Connections[0].IntegrationAccessCodeType1);
        Assert.Null(settings.Connections[0].IntegrationAccessCodeType2);
        Assert.False(ConnectionSettingsRules.ConfirmDefaults(settings, "200000000001"));   // complete now
    }
}

// ── Waiting on panel state (programming mode) ──

public class SessionWatchTests : IAsyncLifetime
{
    private const string Panel = "200000000001";
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly PanelStore _store = new(NullLogger<PanelStore>.Instance);
    private readonly FakeTimeProvider _time = new(T);
    private IPanelMessageSink Sink => _store;

    public async Task InitializeAsync()
    {
        Sink.Started(Panel, A, T);
        await _store.DispatchAsync(new UserListUpdated("barrier", l => l)).WaitAsync(Guard);
    }

    public async Task DisposeAsync() => await _store.DisposeAsync();

    private static ProgrammingLeadInOut Lead(bool leadIn) => new()
    {
        Partition = 1,
        Programming = leadIn ? ProgrammingLeadInOut.ProgrammingType.LeadIn : ProgrammingLeadInOut.ProgrammingType.LeadOut,
        Mode = ProgrammingMode.InstallersProgramming,
    };

    [Fact]
    public async Task SeesALeadInThatArrivesAfterWatchingStarted()
    {
        using var watch = new SessionWatch(_store, Panel, s => s.IsInProgrammingMode, TimeSpan.FromSeconds(10), _time);
        var waiting = watch.WaitAsync(CancellationToken.None);

        Sink.Accept(new PanelMessage(Panel, A, Lead(true), T));

        Assert.True(await waiting.WaitAsync(Guard));
    }

    [Fact]
    public async Task ConditionAlreadyTrue_ReturnsAtOnce()
    {
        Sink.Accept(new PanelMessage(Panel, A, Lead(true), T));
        await _store.DispatchAsync(new UserListUpdated("barrier", l => l)).WaitAsync(Guard);

        using var watch = new SessionWatch(_store, Panel, s => s.IsInProgrammingMode, TimeSpan.FromSeconds(10), _time);
        Assert.True(await watch.WaitAsync(CancellationToken.None).WaitAsync(Guard));
    }

    [Fact]
    public async Task TimesOut_OnTheGivenClock()
    {
        using var watch = new SessionWatch(_store, Panel, s => s.IsInProgrammingMode, TimeSpan.FromSeconds(10), _time);
        var waiting = watch.WaitAsync(CancellationToken.None);
        await _store.DispatchAsync(new UserListUpdated("barrier", l => l)).WaitAsync(Guard);

        Assert.False(waiting.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(await waiting.WaitAsync(Guard));
    }

    [Fact]
    public async Task SessionGoingAway_EndsTheWait()
    {
        using var watch = new SessionWatch(_store, Panel, s => s.IsInProgrammingMode, TimeSpan.FromSeconds(10), _time);
        var waiting = watch.WaitAsync(CancellationToken.None);
        Sink.Ended(Panel, A);
        Assert.False(await waiting.WaitAsync(Guard));
    }
}

/// <summary>
/// The programming-mode entry against a scripted panel. The panel confirms with LeadIn after
/// replying, as the real one does. Time never advances, so any wait that misses the LeadIn
/// hangs (and the guard fails the test) rather than passing after a timeout.
/// </summary>
public class AccessCodeServiceTests : IAsyncLifetime
{
    private const string Panel = "200000000001";
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly PanelStore _store = new(NullLogger<PanelStore>.Instance);
    private readonly PanelOperations _ops = new(NullLogger<PanelOperations>.Instance);
    private readonly FakeTimeProvider _time = new(T);
    private ScriptedPanel _panel = null!;
    private PanelAccessCodeService _service = null!;

    public async Task InitializeAsync()
    {
        IPanelMessageSink sink = _store;
        sink.Started(Panel, A, T);
        await _store.DispatchAsync(new UserListUpdated("barrier", l => l)).WaitAsync(Guard);
        _panel = new ScriptedPanel(sink);
        _service = new PanelAccessCodeService(_panel, _store,
            new StaticOptions<PanelConnectionsSettings>(new PanelConnectionsSettings()),
            new NoPersistence(), NullLogger<PanelAccessCodeService>.Instance, _ops, _time);
    }

    public async Task DisposeAsync()
    {
        // If a test failed with an operation still waiting on the fake clock, release it so
        // cleanup can't hang the run.
        _time.Advance(TimeSpan.FromHours(1));
        await _ops.DisposeAsync().AsTask().WaitAsync(Guard);
        await _store.DisposeAsync();
    }

    [Fact]
    public async Task EntersProgrammingMode_OnceThePanelConfirmsWithLeadIn()
    {
        var result = await _service.ExecuteAsync(Panel, PanelAccessCodeKind.Installer, "5555", readWrite: false,
            _ => Task.FromResult(42), CancellationToken.None).WaitAsync(Guard);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(42, result.Result);
        Assert.Equal(["ConfigurationEnter", "ConfigurationExit"], _panel.Sent);
    }

    [Fact]
    public async Task PanelNeverConfirms_TimesOutAndExits()
    {
        _panel.ConfirmWithLeadIn = false;
        var running = _service.ExecuteAsync(Panel, PanelAccessCodeKind.Installer, "5555", readWrite: false,
            _ => Task.FromResult(42), CancellationToken.None);
        await _panel.EnterSent.Task.WaitAsync(Guard);

        _time.Advance(TimeSpan.FromSeconds(10));
        var result = await running.WaitAsync(Guard);

        Assert.False(result.Success);
        Assert.Equal(["ConfigurationEnter", "ConfigurationExit"], _panel.Sent);
    }

    private sealed class ScriptedPanel(IPanelMessageSink sink) : IPanelSender
    {
        public List<string> Sent { get; } = [];
        public bool ConfirmWithLeadIn { get; set; } = true;
        public TaskCompletionSource EnterSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SessionResponse> Send(SessionCommand command, CancellationToken cancellationToken = default)
        {
            Sent.Add(command.MessageData.GetType().Name);
            var lead = command.MessageData switch
            {
                ConfigurationEnter when ConfirmWithLeadIn => ProgrammingLeadInOut.ProgrammingType.LeadIn,
                ConfigurationExit => ProgrammingLeadInOut.ProgrammingType.LeadOut,
                _ => (ProgrammingLeadInOut.ProgrammingType?)null,
            };
            if (lead is { } l)
                sink.Accept(new PanelMessage(Panel, A, new ProgrammingLeadInOut { Partition = 1, Programming = l }, T));
            if (command.MessageData is ConfigurationEnter)
                EnterSent.TrySetResult();
            return Task.FromResult(new SessionResponse { Success = true, MessageData = new CommandResponse() });
        }
    }

    private sealed class NoPersistence : ISettingsPersistenceService
    {
        public string PersistPath => "";
        public object GetSettings(Type settingsType) => throw new NotSupportedException();
        public Task SaveSettingsAsync(Type settingsType, object settings) => throw new NotSupportedException();
        public Task<bool> UpdateAsync<T>(Func<T, bool> change) where T : class => throw new NotSupportedException();
    }
}

internal sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

// ── Diagnostics log ──

public class DiagnosticsLogTests : IAsyncLifetime
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private readonly DiagnosticsLogService _log = new(new StaticOptions<DiagnosticsSettings>(new DiagnosticsSettings { MaxLogEntries = 100 }));

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _log.DisposeAsync();

    private static DiagnosticsLogEntry Entry(int i, string category = "NeoHub.Test") => new()
    {
        Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
        LogLevel = LogLevel.Information,
        Category = category,
        Message = $"m{i}",
    };

    [Fact]
    public async Task ManyWriters_BufferKeepsTheNewestMax_AndCategories()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++) _log.AddLog(Entry(w * 100 + i, $"Cat{w}"));
        })));
        await _log.QuiesceAsync().WaitAsync(Guard);

        Assert.Equal(100, _log.GetLogs().Count);
        Assert.Equal(["Cat0", "Cat1", "Cat2", "Cat3"], _log.GetDiscoveredCategories().Order());
    }

    [Fact]
    public async Task Subscription_StartsWithTheBuffer_ThenAppends_NothingMissedOrDoubled()
    {
        for (int i = 0; i < 5; i++) _log.AddLog(Entry(i));
        using var subscription = _log.Subscribe();
        for (int i = 5; i < 10; i++) _log.AddLog(Entry(i));
        _log.Clear();
        _log.AddLog(Entry(10));
        await _log.QuiesceAsync().WaitAsync(Guard);

        // Replay what a page does with the stream.
        var shown = new List<string>();
        while (subscription.Events.TryRead(out var e))
        {
            switch (e)
            {
                case LogReset r: shown = r.Entries.Select(x => x.Message).ToList(); break;
                case LogAppended a: shown.Add(a.Entry.Message); break;
            }
        }
        Assert.Equal(["m10"], shown);
        Assert.Equal(["m10"], _log.GetLogs().Select(x => x.Message));
    }

    [Fact]
    public async Task SlowSubscriber_LosesItsOldestEntries_NeverBlocksLogging()
    {
        using var subscription = _log.Subscribe();
        for (int i = 0; i < DiagnosticsLogService.SubscriberCapacity + 50; i++) _log.AddLog(Entry(i));
        await _log.QuiesceAsync().WaitAsync(Guard);

        var received = new List<LogEvent>();
        while (subscription.Events.TryRead(out var e)) received.Add(e);
        Assert.Equal(DiagnosticsLogService.SubscriberCapacity, received.Count);
        Assert.Equal($"m{DiagnosticsLogService.SubscriberCapacity + 49}", ((LogAppended)received[^1]).Entry.Message);
    }

    [Fact]
    public void LevelTable_UsesTheLongestConfiguredPrefix_CaseInsensitively()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft"] = "Error",
            ["Logging:LogLevel:Microsoft.AspNetCore.Hosting"] = "Information",
        }).Build();
        var table = LogLevelTable.From(config);

        Assert.Equal(LogLevel.Information, table.For("Microsoft.AspNetCore.Hosting.Diagnostics"));
        Assert.Equal(LogLevel.Error, table.For("microsoft.aspnetcore.Routing"));
        Assert.Equal(LogLevel.Warning, table.For("System.Net.Http"));
    }
}
