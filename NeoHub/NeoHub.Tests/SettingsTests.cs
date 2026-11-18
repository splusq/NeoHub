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

public class AtomicFileTests
{
    /// <summary>
    /// A reader racing the writer only ever sees a whole file, old or new. It can't fail
    /// spuriously (any interleaving must satisfy it); it fails if writes are made in place.
    /// </summary>
    [Fact]
    public async Task ConcurrentReader_NeverSeesAPartialFile()
    {
        var dir = Directory.CreateTempSubdirectory("neohub-atomic-").FullName;
        var path = Path.Combine(dir, "userSettings.json");
        var a = new string('a', 256 * 1024);
        var b = new string('b', 256 * 1024);
        await AtomicFile.WriteAllTextAsync(path, a);

        using var stop = new CancellationTokenSource();
        var reads = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var text = System.IO.File.ReadAllText(path);
                Assert.True(text == a || text == b, $"partial read of {text.Length} chars");
                reads++;
            }
        });
        for (int i = 0; i < 100; i++)
            await AtomicFile.WriteAllTextAsync(path, i % 2 == 0 ? b : a);
        stop.Cancel();
        await reader.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(reads > 0);
        Assert.Equal(["userSettings.json"], Directory.GetFiles(dir).Select(Path.GetFileName));
        Directory.Delete(dir, recursive: true);
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
