using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;

namespace DSC.TLink.Tests;

/// <summary>
/// The live shell: a real <see cref="ITv2.Runtime.PanelConnection"/> loop over a channel transport,
/// on a fake clock. Every wait is on something the connection actually did (a write, a result),
/// never on elapsed time.
/// </summary>
public class PanelConnectionTests
{
    private static PartitionArm Arm() => new() { Partition = 1, ArmMode = ArmingMode.StayArm, AccessCode = "0000" };

    [Fact]
    public async Task Handshake_ThenARequest_RoundTrips()
    {
        await using var panel = new PanelSimulator();
        await panel.ReadyAsync();

        var result = panel.Connection.SendAsync(Arm());
        var sent = await panel.ReceiveAsync<PartitionArm>();
        panel.Respond(sent);

        var reply = Assert.IsType<CommandResponse>((await result.WaitAsync(PanelSimulator.Guard)).Value);
        Assert.Equal(CommandResponseCode.Success, reply.ResponseCode);
    }

    [Fact]
    public async Task ManyCallersAtOnce_AllComplete_ThroughOneWriter()
    {
        await using var panel = new PanelSimulator();
        await panel.ReadyAsync();

        var calls = Enumerable.Range(0, 25).Select(_ => Task.Run(() => panel.Connection.SendAsync(new ConnectionPoll()))).ToList();
        for (int i = 0; i < 25; i++)
            panel.Ack(await panel.ReceiveAsync<ConnectionPoll>());

        var results = await Task.WhenAll(calls).WaitAsync(PanelSimulator.Guard);
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.False(panel.Transport.OverlappingWriteSeen);
    }

    [Fact]
    public async Task CallerCancels_TheCallThrows_AndTheNextCommandGoesOut()
    {
        await using var panel = new PanelSimulator();
        await panel.ReadyAsync();

        using var cancel = new CancellationTokenSource();
        var first = panel.Connection.SendAsync(Arm(), cancel.Token);
        await panel.ReceiveAsync<PartitionArm>();
        var second = panel.Connection.SendAsync(Arm());
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(PanelSimulator.Guard));
        panel.Respond(await panel.ReceiveAsync<PartitionArm>());
        Assert.True((await second.WaitAsync(PanelSimulator.Guard)).IsSuccess);
    }

    [Fact]
    public async Task UnansweredCommand_TimesOut_OnTheFakeClock()
    {
        await using var panel = new PanelSimulator();
        await panel.ReadyAsync();

        var result = panel.Connection.SendAsync(Arm());
        await panel.ReceiveAsync<PartitionArm>();
        await panel.AdvanceAsync(panel.Timings.CommandTimeout);

        Assert.Equal(TLinkErrorCode.Timeout, (await result.WaitAsync(PanelSimulator.Guard)).Error!.Value.Code);
    }

    [Fact]
    public async Task Heartbeat_IsSent_WhenTheFakeClockReachesIt()
    {
        await using var panel = new PanelSimulator();
        await panel.ReadyAsync();

        await panel.AdvanceAsync(panel.Timings.HeartbeatInterval);

        Assert.IsType<ConnectionPoll>((await panel.ReceiveAsync()).Message);
    }

    [Fact]
    public async Task Notifications_AreDeliveredInOrder_EvenIfThePanelHangsUpRightAfter()
    {
        var panel = new PanelSimulator();
        await panel.ReadyAsync();

        panel.Send(new NotificationLifestyleZoneStatus { ZoneNumber = 1, Status = NotificationLifestyleZoneStatus.LifeStyleZoneStatusCode.Open }, 0);
        panel.Send(new NotificationLifestyleZoneStatus { ZoneNumber = 1, Status = NotificationLifestyleZoneStatus.LifeStyleZoneStatusCode.Restored }, 0);
        panel.Transport.Hangup();

        var received = new List<string>();
        await foreach (var message in panel.Connection.GetNotificationsAsync().WithCancellation(new CancellationTokenSource(PanelSimulator.Guard).Token))
            if (message.Message is NotificationLifestyleZoneStatus z)
                received.Add($"{z.ZoneNumber}:{z.Status}");

        Assert.Equal(["1:Open", "1:Restored"], received);
        await panel.Connection.Completion.WaitAsync(PanelSimulator.Guard);
    }

    [Fact]
    public async Task PanelHangsUp_WaitingCallersGetDisconnected_AndEverythingIsCleanedUp()
    {
        var panel = new PanelSimulator();
        await panel.ReadyAsync();

        var waiting = panel.Connection.SendAsync(Arm());
        await panel.ReceiveAsync<PartitionArm>();
        panel.Transport.Hangup();

        Assert.Equal(TLinkErrorCode.Disconnected, (await waiting.WaitAsync(PanelSimulator.Guard)).Error!.Value.Code);
        await panel.Connection.Completion.WaitAsync(PanelSimulator.Guard);
        Assert.True(panel.Connection.Lifetime.IsCancellationRequested);
        Assert.True(panel.Transport.Disposed);
        Assert.Equal(TLinkErrorCode.Disconnected, (await panel.Connection.SendAsync(Arm())).Error!.Value.Code);
    }

    [Fact]
    public async Task Dispose_WhenReady_SaysGoodbye_ThenCloses()
    {
        var panel = new PanelSimulator();
        await panel.ReadyAsync();

        var disposing = panel.Connection.DisposeAsync().AsTask();

        Assert.IsType<ConnectionEndSession>((await panel.ReceiveAsync()).Message);
        await disposing.WaitAsync(PanelSimulator.Guard);
        Assert.True(panel.Transport.Disposed);
    }

    [Fact]
    public async Task BadFrame_IsSkipped_AndTheSessionKeepsWorking()
    {
        await using var panel = new PanelSimulator();
        await panel.ReadyAsync();

        panel.Transport.DeliverFailure(TLinkErrorCode.FramingError);
        panel.Transport.Deliver([0x01, 0x02, 0x03]);
        var result = panel.Connection.SendAsync(Arm());
        panel.Respond(await panel.ReceiveAsync<PartitionArm>());

        Assert.True((await result.WaitAsync(PanelSimulator.Guard)).IsSuccess);
    }

    [Fact]
    public async Task UnknownPanel_NeverEstablishes_AndTheConnectionEnds()
    {
        var panel = new PanelSimulator(knownPanel: false);
        var records = CapturedTraffic.Load(Path.Combine(CapturedTraffic.Directory, "connection-01.jsonl"));
        panel.Transport.Deliver(records.First(r => r.IsPacket && r.Dir == "rx").FrameBytes);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => panel.Connection.Established.WaitAsync(PanelSimulator.Guard));
        await panel.Connection.Completion.WaitAsync(PanelSimulator.Guard);
    }
}
