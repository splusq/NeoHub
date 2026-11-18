using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using NeoHub.Domain;

namespace NeoHub.Tests;

/// <summary>
/// The store actor. Every wait is a barrier: DispatchAsync completes once its event is applied,
/// and the inbox is first-in first-out, so everything posted before it is applied too.
/// </summary>
public class PanelStoreTests : IAsyncLifetime
{
    private const string Panel = "200000000001";
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);   // hang guard only

    private readonly PanelStore _store = new(NullLogger<PanelStore>.Instance);
    private IPanelMessageSink Sink => _store;

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _store.DisposeAsync();

    private Task Barrier() => _store.DispatchAsync(new UserListUpdated("barrier", l => l)).WaitAsync(Guard);

    private static NotificationLifestyleZoneStatus Zone(byte zone, bool open) => new()
    {
        ZoneNumber = zone,
        Status = open ? NotificationLifestyleZoneStatus.LifeStyleZoneStatusCode.Open : NotificationLifestyleZoneStatus.LifeStyleZoneStatusCode.Restored,
    };

    [Fact]
    public async Task ConnectionStream_IsAppliedInOrder()
    {
        Sink.Started(Panel, A, T);
        Sink.Accept(new PanelMessage(Panel, A, Zone(1, true), T.AddSeconds(1)));
        Sink.Accept(new PanelMessage(Panel, A, Zone(1, false), T.AddSeconds(2)));
        await Barrier();

        Assert.False(_store.GetZone(Panel, 1)!.IsOpen);
    }

    [Fact]
    public async Task EndedConnection_ReadsAsAbsent()
    {
        Sink.Started(Panel, A, T);
        Sink.Accept(new PanelMessage(Panel, A, Zone(1, true), T));
        Sink.Ended(Panel, A);
        await Barrier();

        Assert.Null(_store.GetSession(Panel));
        Assert.Empty(_store.GetAllSessions());
        Assert.Empty(_store.GetZones(Panel));
    }

    [Fact]
    public async Task Reconnect_ThenStaleEnd_KeepsTheNewConnection()
    {
        Sink.Started(Panel, A, T);
        Sink.Started(Panel, B, T.AddSeconds(1));
        Sink.Accept(new PanelMessage(Panel, B, Zone(9, true), T.AddSeconds(2)));
        Sink.Ended(Panel, A);
        await Barrier();

        Assert.Equal(B, _store.GetSession(Panel)!.ConnectionId);
        Assert.True(_store.GetZone(Panel, 9)!.IsOpen);
    }

    [Fact]
    public async Task ZoneChanges_AreAnnouncedInOrder()
    {
        var seen = new List<string>();
        _store.ZoneStateChanged += (_, e) => seen.Add($"{e.Zone.ZoneNumber}:{e.Zone.IsOpen}");

        Sink.Started(Panel, A, T);
        for (int i = 0; i < 50; i++)
            Sink.Accept(new PanelMessage(Panel, A, Zone(1, i % 2 == 0), T.AddSeconds(i)));
        await Barrier();

        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"1:{i % 2 == 0}"), seen);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotStopOthersOrTheStore()
    {
        var calls = 0;
        _store.ZoneStateChanged += (_, _) => throw new InvalidOperationException("subscriber bug");
        _store.ZoneStateChanged += (_, _) => calls++;

        Sink.Started(Panel, A, T);
        Sink.Accept(new PanelMessage(Panel, A, Zone(1, true), T));
        Sink.Accept(new PanelMessage(Panel, A, Zone(2, true), T));
        await Barrier();

        Assert.Equal(2, calls);
        Assert.Equal(2, _store.GetZones(Panel).Count);
    }

    [Fact]
    public async Task ManyWritersAtOnce_EveryEventIsApplied()
    {
        Sink.Started(Panel, A, T);
        await Barrier();

        await Task.WhenAll(Enumerable.Range(1, 8).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 25; i++)
                Sink.Accept(new PanelMessage(Panel, A, Zone((byte)((w - 1) * 25 + i + 1), true), T));
        })));
        await Barrier();

        Assert.Equal(200, _store.GetZones(Panel).Count);
        Assert.All(_store.GetZones(Panel).Values, z => Assert.True(z.IsOpen));
    }

    [Fact]
    public async Task Readers_AlwaysSeeACompleteSnapshot()
    {
        Sink.Started(Panel, A, T);
        await Barrier();
        var before = _store.GetZones(Panel);

        Sink.Accept(new PanelMessage(Panel, A, Zone(1, true), T));
        await Barrier();

        Assert.Empty(before);                       // an old snapshot never changes under a reader
        Assert.Single(_store.GetZones(Panel));
    }
}
