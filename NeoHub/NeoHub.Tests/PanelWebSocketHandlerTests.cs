using Microsoft.Extensions.Logging.Abstractions;
using NeoHub.Api.WebSocket;
using NeoHub.Services;
using NeoHub.Services.Models;

namespace NeoHub.Tests;

public class PanelWebSocketHandlerTests
{
    private const string Panel = "200000000001";

    private readonly PanelStateService _state = new(NullLogger<PanelStateService>.Instance);
    private readonly PanelWebSocketHandler _handler;

    public PanelWebSocketHandlerTests()
    {
        _handler = new PanelWebSocketHandler(
            _state, new FakeCommandService(), new FakeSessionManager(Panel), new SessionMonitor(),
            NullLogger<PanelWebSocketHandler>.Instance);
    }

    private (FakeWebSocket Socket, Task Run) Connect()
    {
        var socket = new FakeWebSocket();
        var run = _handler.RunClientAsync(socket, "test", CancellationToken.None);
        return (socket, run);
    }

    private async Task WaitForClients(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_handler.ClientCount != count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"expected {count} clients, have {_handler.ClientCount}");
            await Task.Delay(5);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Disconnect used to call ConcurrentBag.TryTake, which removes an arbitrary client:
    /// often a different, still-connected one, which then stopped receiving updates.
    /// </summary>
    [Fact]
    public async Task OneClientDisconnecting_DoesNotStopUpdatesToAnother()
    {
        var (first, firstRun) = Connect();
        var (second, secondRun) = Connect();
        await WaitForClients(2);

        first.ReceiveClose();
        await firstRun;
        await WaitForClients(1);

        _state.UpdateZone(Panel, new ZoneState { ZoneNumber = 3, IsOpen = true });

        await WaitFor(() => second.SentOfType("zone_update").Any());
        Assert.Empty(first.SentOfType("zone_update"));

        second.ReceiveClose();
        await secondRun;
        await WaitForClients(0);
    }

    /// <summary>
    /// Each event used to start its own fire-and-forget send: two quick changes overlapped on
    /// the socket (the second was dropped) or arrived out of order (a closed door showed open).
    /// </summary>
    [Fact]
    public async Task BurstOfZoneChanges_ArrivesComplete_AndInOrder()
    {
        var (socket, run) = Connect();
        await WaitForClients(1);

        for (int i = 0; i < 50; i++)
            _state.UpdateZone(Panel, new ZoneState { ZoneNumber = 3, IsOpen = i % 2 == 0 });

        await WaitFor(() => socket.SentOfType("zone_update").Count() == 50);
        var updates = socket.SentOfType("zone_update").ToList();
        for (int i = 0; i < 50; i++)
            Assert.Contains($"\"open\":{(i % 2 == 0 ? "true" : "false")}", updates[i]);
        Assert.Equal(1, socket.MaxConcurrentSends);

        socket.ReceiveClose();
        await run;
    }

    [Fact]
    public async Task FullStateRequest_IsAnswered_AlongsideBroadcasts()
    {
        _state.UpdatePartition(Panel, new PartitionState { PartitionNumber = 1 });
        var (socket, run) = Connect();
        await WaitForClients(1);

        var broadcasts = Task.Run(() =>
        {
            for (int i = 0; i < 20; i++)
                _state.UpdateZone(Panel, new ZoneState { ZoneNumber = (byte)(i + 1) });
        });
        socket.ReceiveText("{\"type\":\"get_full_state\"}");
        await broadcasts;

        await WaitFor(() => socket.SentOfType("full_state").Any() && socket.SentOfType("zone_update").Count() == 20);
        Assert.Equal(1, socket.MaxConcurrentSends);

        socket.ReceiveClose();
        await run;
    }

    [Fact]
    public async Task OversizedMessage_DisconnectsTheClient()
    {
        var (socket, run) = Connect();
        await WaitForClients(1);

        socket.ReceiveText(new string('x', PanelWebSocketHandler.MaxInboundMessageBytes + 1));
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(socket.Aborted);
        Assert.Equal(0, _handler.ClientCount);
    }
}
