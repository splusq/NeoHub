using Microsoft.Extensions.Logging.Abstractions;
using NeoHub.Services;
using NeoHub.Services.Handlers;
using NeoHub.Services.Models;

namespace NeoHub.Tests;

public class PanelStateServiceTests
{
    private const string Panel = "200000000001";
    private readonly PanelStateService _state = new(NullLogger<PanelStateService>.Instance);

    /// <summary>
    /// The programming lock used to live in SessionState, which is replaced on reconnect; an
    /// operation for the old connection and one for the new held different locks.
    /// </summary>
    [Fact]
    public void ConfigLock_IsTheSameAcrossDisconnectAndReconnect()
    {
        _state.UpdateSession(Panel, _ => { });
        var before = _state.GetSession(Panel)!.ConfigLock;

        _state.RemoveSession(Panel);
        _state.UpdateSession(Panel, _ => { });

        Assert.Same(before, _state.GetSession(Panel)!.ConfigLock);
        Assert.Same(before, _state.GetConfigLock(Panel));
    }

    [Fact]
    public void ConfigLock_IsPerPanel()
        => Assert.NotSame(_state.GetConfigLock("panel-a"), _state.GetConfigLock("panel-b"));

    /// <summary>
    /// Partitions and Zones were plain Dictionaries written by the notification loop and the
    /// configuration pull while the UI and WebSocket enumerated them.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritersAndReaders_DoNotCorruptState()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                _state.UpdateZone(Panel, new ZoneState { ZoneNumber = (byte)(i++ % 64 + 1), Partitions = [1] });
                _state.UpdatePartition(Panel, new PartitionState { PartitionNumber = (byte)(w + 1) });
            }
        }));
        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = _state.GetZones(Panel).Select(z => z.Value.Partitions.ToList()).ToList();
                _ = _state.GetPartitions(Panel).Values.Count();
            }
        }));

        await Task.WhenAll(writers.Concat(readers));

        Assert.Equal(64, _state.GetZones(Panel).Count);
        Assert.Equal(4, _state.GetPartitions(Panel).Count);
    }

    [Fact]
    public void GetZones_ForUnknownPanel_IsEmpty()
        => Assert.Empty(_state.GetZones("nobody"));
}

public class PulledStatusGuardTests
{
    private static readonly DateTime RequestedAt = new(2026, 11, 17, 8, 10, 0, DateTimeKind.Utc);

    [Fact]
    public void NewEntity_IsApplied()
        => Assert.True(PanelConfigurationHandler.ShouldApplyPulledStatus(null, RequestedAt));

    [Fact]
    public void EntityLastChangedBeforeTheRequest_IsApplied()
        => Assert.True(PanelConfigurationHandler.ShouldApplyPulledStatus(RequestedAt.AddSeconds(-1), RequestedAt));

    /// <summary>A live notification after the request went out is newer than the reply.</summary>
    [Fact]
    public void EntityChangedLiveAfterTheRequest_IsKept()
        => Assert.False(PanelConfigurationHandler.ShouldApplyPulledStatus(RequestedAt.AddMilliseconds(5), RequestedAt));
}
