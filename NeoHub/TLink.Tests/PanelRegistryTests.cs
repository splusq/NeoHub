using System.Threading.Channels;
using DSC.TLink.ITv2.Sessions;
using DSC.TLink.ITv2.Messages;
using DSC.TLink.ITv2.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace DSC.TLink.Tests;

/// <summary>
/// The registry with real connections on a fake clock. Covers the 2026-11-17 outage: a panel
/// reconnecting while its old connection is still registered.
/// </summary>
public class PanelRegistryTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PanelLifecycle _lifecycle = new();
        public PanelRegistry Registry { get; }

        public Fixture() => Registry = new(_lifecycle, NullLogger<PanelRegistry>.Instance);

        public async Task<string> NextEventAsync()
            => (await _lifecycle.Events.ReadAsync().AsTask().WaitAsync(PanelSimulator.Guard)) switch
            {
                SessionConnectedNotification c => $"connected {c.SessionId}",
                SessionDisconnectedNotification d => $"disconnected {d.SessionId}",
                var other => other.GetType().Name,
            };

        public ValueTask DisposeAsync() => Registry.DisposeAsync();
    }

    [Fact]
    public async Task Register_MakesTheConnectionFindable_AndAnnouncesIt()
    {
        await using var fx = new Fixture();
        await using var panel = new PanelSimulator();
        await panel.HandshakeAsync();

        fx.Registry.Register(panel.Connection);

        Assert.Equal("connected 200000000001", await fx.NextEventAsync());
        Assert.Same(panel.Connection, fx.Registry.Find("200000000001"));
    }

    /// <summary>The outage: the old connection is still registered when the panel reconnects.</summary>
    [Fact]
    public async Task Reconnect_ReplacesTheStaleConnection_AndClosesIt()
    {
        await using var fx = new Fixture();
        var stale = new PanelSimulator();
        await stale.ReadyAsync();
        fx.Registry.Register(stale.Connection);
        Assert.Equal("connected 200000000001", await fx.NextEventAsync());

        await using var fresh = new PanelSimulator();
        await fresh.HandshakeAsync();
        fx.Registry.Register(fresh.Connection);

        // State for the panel is reset, then rebuilt for the new connection.
        Assert.Equal("disconnected 200000000001", await fx.NextEventAsync());
        Assert.Equal("connected 200000000001", await fx.NextEventAsync());
        Assert.Same(fresh.Connection, fx.Registry.Find("200000000001"));

        // The stale connection is told to close: it says goodbye and ends.
        Assert.IsType<ConnectionEndSession>((await stale.ReceiveAsync()).Message);
        await stale.Connection.Completion.WaitAsync(PanelSimulator.Guard);
    }

    /// <summary>The outage's second half: the stale connection's cleanup used to remove the live one.</summary>
    [Fact]
    public async Task StaleConnectionEnding_DoesNotRemoveTheLiveOne()
    {
        await using var fx = new Fixture();
        var stale = new PanelSimulator();
        await stale.HandshakeAsync();
        fx.Registry.Register(stale.Connection);
        await using var fresh = new PanelSimulator();
        await fresh.HandshakeAsync();
        fx.Registry.Register(fresh.Connection);
        for (int i = 0; i < 3; i++)
            await fx.NextEventAsync();

        fx.Registry.Unregister(stale.Connection);
        fx.Registry.Unregister(stale.Connection);

        // A later, real removal is the next event; nothing was announced for the stale one.
        fx.Registry.Unregister(fresh.Connection);
        Assert.Equal("disconnected 200000000001", await fx.NextEventAsync());
        Assert.Null(fx.Registry.Find("200000000001"));
        await stale.DisposeAsync();
    }

    [Fact]
    public async Task RegisteringTheSameConnectionTwice_AnnouncesItOnce()
    {
        await using var fx = new Fixture();
        await using var panel = new PanelSimulator();
        await panel.HandshakeAsync();

        fx.Registry.Register(panel.Connection);
        fx.Registry.Register(panel.Connection);
        fx.Registry.Unregister(panel.Connection);

        Assert.Equal("connected 200000000001", await fx.NextEventAsync());
        Assert.Equal("disconnected 200000000001", await fx.NextEventAsync());
    }

    [Fact]
    public async Task DifferentPanels_AreIndependent()
    {
        await using var fx = new Fixture();
        await using var a = new PanelSimulator(panelId: "200000000001");
        await using var b = new PanelSimulator(panelId: "200000000002");
        await a.HandshakeAsync();
        await b.HandshakeAsync();

        fx.Registry.Register(a.Connection);
        fx.Registry.Register(b.Connection);
        fx.Registry.Unregister(a.Connection);
        for (int i = 0; i < 3; i++)
            await fx.NextEventAsync();

        Assert.Null(fx.Registry.Find("200000000001"));
        Assert.Same(b.Connection, fx.Registry.Find("200000000002"));
        Assert.Equal(["200000000002"], fx.Registry.GetActiveSessions());
    }
}
