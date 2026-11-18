using DSC.TLink.ITv2.MediatR;
using Microsoft.Extensions.Logging.Abstractions;

namespace DSC.TLink.Tests;

public class ITv2SessionManagerTests
{
    private const string PanelId = "200623947012";

    private readonly RecordingMediator _mediator = new();
    private readonly IITv2SessionManager _manager;

    public ITv2SessionManagerTests()
    {
        _manager = new ITv2SessionManager(_mediator, NullLogger<ITv2SessionManager>.Instance);
    }

    // Lifecycle notifications are published fire-and-forget; give them a moment to land.
    private static Task Settle() => Task.Delay(50);

    [Fact]
    public async Task Reconnect_ReplacesStaleSession_AndDisposesIt()
    {
        var stale = new FakeSession(PanelId, "stale");
        var live = new FakeSession(PanelId, "live");

        _manager.RegisterSession(PanelId, stale);
        _manager.RegisterSession(PanelId, live);
        await Settle();

        Assert.Same(live, _manager.GetSession(PanelId));
        Assert.Equal(1, stale.DisposeCount);
        Assert.Equal(0, live.DisposeCount);
    }

    /// <summary>
    /// Replays the 2026-11-17 outage: the panel reconnected while its old, half-open
    /// session was still registered, then the old session's connection closed. Before the
    /// fix, closing the old session removed the panel ID and orphaned the live connection
    /// for two hours.
    /// </summary>
    [Fact]
    public async Task StaleSessionClosingAfterReconnect_KeepsLiveSessionRegistered()
    {
        var stale = new FakeSession(PanelId, "stale");
        var live = new FakeSession(PanelId, "live");

        _manager.RegisterSession(PanelId, stale);
        _manager.RegisterSession(PanelId, live);
        _manager.UnregisterSession(PanelId, stale);
        await Settle();

        Assert.Same(live, _manager.GetSession(PanelId));
        Assert.Single(_manager.GetActiveSessions());
    }

    [Fact]
    public async Task Reconnect_PublishesDisconnectThenConnect_SoStateIsRebuilt()
    {
        _manager.RegisterSession(PanelId, new FakeSession(PanelId, "stale"));
        _manager.RegisterSession(PanelId, new FakeSession(PanelId, "live"));
        await Settle();

        Assert.Equal(
            ["SessionConnected", "SessionDisconnected", "SessionConnected"],
            _mediator.Events);
    }

    [Fact]
    public async Task SupersededSessionClosing_PublishesNoDisconnect()
    {
        var stale = new FakeSession(PanelId, "stale");
        _manager.RegisterSession(PanelId, stale);
        _manager.RegisterSession(PanelId, new FakeSession(PanelId, "live"));
        await Settle();
        var before = _mediator.Events.Count;

        _manager.UnregisterSession(PanelId, stale);
        await Settle();

        Assert.Equal(before, _mediator.Events.Count);
    }

    [Fact]
    public async Task LiveSessionClosing_RemovesItAndPublishesDisconnect()
    {
        var live = new FakeSession(PanelId, "live");
        _manager.RegisterSession(PanelId, live);
        _manager.UnregisterSession(PanelId, live);
        await Settle();

        Assert.Null(_manager.GetSession(PanelId));
        Assert.Empty(_manager.GetActiveSessions());
        Assert.Equal(["SessionConnected", "SessionDisconnected"], _mediator.Events);
    }

    [Fact]
    public async Task RegisteringSameInstanceTwice_DoesNotDisposeIt()
    {
        var session = new FakeSession(PanelId, "only");
        _manager.RegisterSession(PanelId, session);
        _manager.RegisterSession(PanelId, session);
        await Settle();

        Assert.Same(session, _manager.GetSession(PanelId));
        Assert.Equal(0, session.DisposeCount);
        Assert.Equal(["SessionConnected"], _mediator.Events);
    }

    [Fact]
    public async Task DifferentPanels_AreIndependent()
    {
        var a = new FakeSession("panel-a", "a");
        var b = new FakeSession("panel-b", "b");
        _manager.RegisterSession("panel-a", a);
        _manager.RegisterSession("panel-b", b);
        _manager.UnregisterSession("panel-a", a);
        await Settle();

        Assert.Null(_manager.GetSession("panel-a"));
        Assert.Same(b, _manager.GetSession("panel-b"));
        Assert.Equal(0, b.DisposeCount);
    }
}
