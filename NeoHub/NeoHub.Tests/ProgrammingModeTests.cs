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
        var result = await _service.ExecuteAsync(Panel, PanelAccessCodeKind.Installer, "1234", readWrite: false,
            _ => Task.FromResult(42), CancellationToken.None).WaitAsync(Guard);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(42, result.Result);
        Assert.Equal(["ConfigurationEnter", "ConfigurationExit"], _panel.Sent);
    }

    [Fact]
    public async Task PanelNeverConfirms_TimesOutAndExits()
    {
        _panel.ConfirmWithLeadIn = false;
        var running = _service.ExecuteAsync(Panel, PanelAccessCodeKind.Installer, "1234", readWrite: false,
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
