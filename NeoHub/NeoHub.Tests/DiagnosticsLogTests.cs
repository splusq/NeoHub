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
