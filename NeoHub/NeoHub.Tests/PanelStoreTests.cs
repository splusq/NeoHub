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

    private static List<PanelChange> Drain(PanelSubscription subscription)
    {
        var items = new List<PanelChange>();
        while (subscription.Changes.TryRead(out var change))
            items.Add(change);
        return items;
    }

    [Fact]
    public async Task Subscription_StartsWithResynchronize_ThenEveryChangeInOrder()
    {
        using var subscription = _store.Subscribe();
        Sink.Started(Panel, A, T);
        for (int i = 0; i < 50; i++)
            Sink.Accept(new PanelMessage(Panel, A, Zone(1, i % 2 == 0), T.AddSeconds(i)));
        await Barrier();

        var changes = Drain(subscription);
        Assert.IsType<Resynchronize>(changes[0]);
        Assert.Equal(
            Enumerable.Range(0, 50).Select(i => $"1:{i % 2 == 0}"),
            changes.OfType<ZoneChanged>().Select(z => $"{z.Zone.ZoneNumber}:{z.Zone.IsOpen}"));
    }

    [Fact]
    public async Task Subscription_ResynchronizeMarksTheSnapshot_NothingFallsBetween()
    {
        // Changes before subscribing are in the snapshot; changes after it arrive on the stream.
        Sink.Started(Panel, A, T);
        Sink.Accept(new PanelMessage(Panel, A, Zone(1, true), T));
        using var subscription = _store.Subscribe();
        Sink.Accept(new PanelMessage(Panel, A, Zone(2, true), T));
        await Barrier();

        var changes = Drain(subscription);
        Assert.IsType<Resynchronize>(changes[0]);
        Assert.Equal([2], changes.OfType<ZoneChanged>().Select(z => (int)z.Zone.ZoneNumber));
    }

    [Fact]
    public async Task SlowSubscriber_IsToldToResynchronize_OthersAreUnaffected()
    {
        using var slow = _store.Subscribe();
        using var fast = _store.Subscribe();
        Sink.Started(Panel, A, T);
        await Barrier();
        var first = PanelStore.SubscriberCapacity / 2 + 10;
        for (int i = 0; i < first; i++)
            Sink.Accept(new PanelMessage(Panel, A, Zone(1, i % 2 == 0), T));
        await Barrier();
        var fastSeen = Drain(fast).Count;
        for (int i = 0; i < first; i++)
            Sink.Accept(new PanelMessage(Panel, A, Zone(2, i % 2 == 0), T));
        await Barrier();
        fastSeen += Drain(fast).Count;

        // The slow one gets what fit, then the stream ends with the overflow.
        Assert.Equal(PanelStore.SubscriberCapacity, Drain(slow).Count);
        await Assert.ThrowsAsync<SubscriberOverflowException>(() => slow.Changes.Completion.WaitAsync(Guard));
        // The fast one saw everything and is still subscribed.
        Assert.False(fast.Changes.Completion.IsCompleted);
        Assert.True(fastSeen > PanelStore.SubscriberCapacity);
        // And the store kept going.
        Assert.Equal(2, _store.GetZones(Panel).Count);
    }

    [Fact]
    public async Task DisposedSubscription_StopsReceiving()
    {
        var subscription = _store.Subscribe();
        subscription.Dispose();
        Sink.Started(Panel, A, T);
        await Barrier();

        // Only the opening marker was delivered; then the stream ended (a reader's Completion
        // finishes once the remaining items are read).
        Assert.All(Drain(subscription), c => Assert.IsType<Resynchronize>(c));
        await subscription.Changes.Completion.WaitAsync(Guard);
    }

    [Fact]
    public async Task ChangeFeed_ThrowingCallback_DoesNotStopTheFeedOrTheStore()
    {
        var seen = new List<byte>();
        var sawTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var feed = PanelChangeFeed.Start(_store, c =>
        {
            if (c is Resynchronize) subscribed.TrySetResult();
            if (c is not ZoneChanged z) return;
            if (z.Zone.ZoneNumber == 1) throw new InvalidOperationException("subscriber bug");
            seen.Add(z.Zone.ZoneNumber);
            if (z.Zone.ZoneNumber == 2) sawTwo.TrySetResult();
        });
        // The feed subscribes in the background; changes before that are only in the snapshot.
        await subscribed.Task.WaitAsync(Guard);

        Sink.Started(Panel, A, T);
        Sink.Accept(new PanelMessage(Panel, A, Zone(1, true), T));
        Sink.Accept(new PanelMessage(Panel, A, Zone(2, true), T));
        await sawTwo.Task.WaitAsync(Guard);

        Assert.Equal([2], seen);
        Assert.Equal(2, _store.GetZones(Panel).Count);
    }

    [Fact]
    public async Task ChangeFeed_FallingBehind_ResubscribesAndResynchronizes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resyncs = 0;
        var resynced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var feed = PanelChangeFeed.Start(_store, c =>
        {
            if (c is not Resynchronize) return;
            if (++resyncs == 1)
            {
                subscribed.TrySetResult();
                gate.Task.Wait();          // the page is busy: changes pile up behind it
            }
            else resynced.TrySetResult();
        });
        await subscribed.Task.WaitAsync(Guard);

        Sink.Started(Panel, A, T);
        for (int i = 0; i < PanelStore.SubscriberCapacity + 10; i++)
            Sink.Accept(new PanelMessage(Panel, A, Zone(1, i % 2 == 0), T));
        await Barrier();
        gate.SetResult();

        await resynced.Task.WaitAsync(Guard);
        Assert.Equal(2, resyncs);
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
