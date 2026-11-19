using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using DSC.TLink.ITv2.Protocol;

namespace DSC.TLink.Tests;

/// <summary>
/// Behaviors the capture doesn't exercise, including each finding from the concurrency audit
/// (docs/Concurrency.md on the #4 branch) that applies to the protocol core. Synchronous and
/// deterministic: the test is the clock and the panel.
/// </summary>
public class ConnectionScenarioTests
{
    private static CommandRequestMessage PartitionStatus(int partition)
        => new() { Request = new ModulePartitionStatus { Partition = partition } };

    private static ModulePartitionStatus PartitionStatusReply(int partition)
        => new() { Partition = partition, PartitionStatus = [0x02, 0x00] };

    private static PartitionArm Arm() => new() { Partition = 1, ArmMode = ArmingMode.StayArm, AccessCode = "0000" };

    private static CommandResponse ResponseTo(ITv2Packet sent, CommandResponseCode code = CommandResponseCode.Success)
        => new() { CommandSequence = ((ICommandMessage)sent.Message).CommandSequence, ResponseCode = code };

    // ── Ordering ──

    /// <summary>
    /// A reply that frees the next queued command is acked before that command is sent, as the
    /// panel saw from the previous session (it acked in its receive loop before anything else).
    /// </summary>
    [Fact]
    public void QueuedCommand_IsSentAfterTheAckOfTheReplyThatFreedIt()
    {
        var core = new CoreHarness().Ready();
        core.Request(Arm());
        var first = core.LastSent;
        core.Request(Arm());
        Assert.Single(core.SentOfType<PartitionArm>());

        core.PanelSends(ResponseTo(first), first.SenderSequence);

        var tail = core.Sent.TakeLast(2).Select(p => p.Message.GetType().Name).ToList();
        Assert.Equal(["SimpleAck", "PartitionArm"], tail);
    }

    // ── Audit T4: replies matched by type ──

    [Fact]
    public void SameTypeRequests_GoOneAtATime_AndEachGetsItsOwnReply()
    {
        var core = new CoreHarness().Ready();
        var a = core.Request(PartitionStatus(1));
        var b = core.Request(PartitionStatus(2));
        Assert.Single(core.SentOfType<CommandRequestMessage>());

        core.PanelSends(PartitionStatusReply(1));
        Assert.Equal(2, core.SentOfType<CommandRequestMessage>().Count());
        core.PanelSends(PartitionStatusReply(2));

        Assert.Equal(1, Assert.IsType<ModulePartitionStatus>(core.Results[a].Value).Partition);
        Assert.Equal(2, Assert.IsType<ModulePartitionStatus>(core.Results[b].Value).Partition);
    }

    [Fact]
    public void Notifications_DoNotWaitBehindACommand()
    {
        var core = new CoreHarness().Ready();
        core.Request(Arm());
        core.Request(new ConnectionPoll());

        Assert.IsType<ConnectionPoll>(core.LastSent.Message);
    }

    // ── Audit T5/T6: timeouts ──

    [Fact]
    public void UnansweredCommand_FailsWithTimeout_AfterCommandTimeout()
    {
        var core = new CoreHarness().Ready();
        var id = core.Request(Arm());

        core.Advance(core.Timings.CommandTimeout - TimeSpan.FromMilliseconds(1));
        Assert.False(core.Results.ContainsKey(id));
        core.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(TLinkErrorCode.Timeout, core.Results[id].Error!.Value.Code);
    }

    [Fact]
    public void TimedOutRequest_IsGone_SoTheNextRequestGetsTheNextReply()
    {
        var core = new CoreHarness().Ready();
        var timedOut = core.Request(PartitionStatus(1));
        core.Advance(core.Timings.CommandTimeout);
        Assert.Equal(TLinkErrorCode.Timeout, core.Results[timedOut].Error!.Value.Code);

        var next = core.Request(PartitionStatus(1));
        core.PanelSends(PartitionStatusReply(1));

        Assert.True(core.Results[next].IsSuccess);
        Assert.Empty(core.State.Pending);
    }

    [Fact]
    public void TimeoutOfACommand_LetsTheQueuedOneGo()
    {
        var core = new CoreHarness().Ready();
        core.Request(Arm());
        core.Advance(TimeSpan.FromSeconds(5));
        var queued = core.Request(Arm());
        core.Advance(core.Timings.CommandTimeout - TimeSpan.FromSeconds(5));

        Assert.Equal(2, core.SentOfType<PartitionArm>().Count());
        Assert.False(core.Results.ContainsKey(queued));
    }

    /// <summary>The deadline covers waiting for a turn: a request queued too long times out unsent.</summary>
    [Fact]
    public void RequestStuckInTheQueue_TimesOutWithoutBeingSent()
    {
        var core = new CoreHarness().Ready();
        core.Request(Arm());
        var queued = core.Request(Arm());
        core.Advance(core.Timings.CommandTimeout);

        Assert.Single(core.SentOfType<PartitionArm>());
        Assert.Equal(TLinkErrorCode.Timeout, core.Results[queued].Error!.Value.Code);
    }

    [Fact]
    public void RequestBeforeReady_WaitsForReadiness_ThenGetsTheFullCommandTimeout()
    {
        var core = new CoreHarness().Handshake();
        var id = core.Request(Arm());
        Assert.Empty(core.SentOfType<PartitionArm>());

        core.Advance(core.Timings.ReadyQuietPeriod);
        Assert.Single(core.SentOfType<PartitionArm>());

        core.Advance(core.Timings.CommandTimeout - TimeSpan.FromMilliseconds(1));
        Assert.False(core.Results.ContainsKey(id));
    }

    [Fact]
    public void Cancel_RemovesTheRequest_AndFreesTheCommandSlot()
    {
        var core = new CoreHarness().Ready();
        var first = core.Request(Arm());
        core.Request(Arm());
        core.Cancel(first);

        Assert.Equal(TLinkErrorCode.Cancelled, core.Results[first].Error!.Value.Code);
        Assert.Equal(2, core.SentOfType<PartitionArm>().Count());
    }

    // ── Audit T7: readiness ──

    [Fact]
    public void Burst_PostponesReadiness_UntilQuiet()
    {
        var core = new CoreHarness().Handshake();
        core.Advance(TimeSpan.FromSeconds(1.5));
        core.PanelSends(new ConnectionPoll());
        core.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(Phase.Bursting, core.State.Phase);

        core.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(Phase.Ready, core.State.Phase);
    }

    [Fact]
    public void PanelThatNeverGoesQuiet_IsReadyAfterMaxReadyWait()
    {
        var core = new CoreHarness().Handshake();
        for (int i = 0; i < 100 && core.State.Phase != Phase.Ready; i++)
        {
            core.Advance(TimeSpan.FromMilliseconds(500));
            core.PanelSends(new ConnectionPoll());
        }

        Assert.Equal(Phase.Ready, core.State.Phase);
        Assert.Equal(core.State.BurstStartedAt + core.Timings.MaxReadyWait, core.State.ReadyDue);
    }

    [Fact]
    public void BurstNotifications_AreAckedButNotPublished()
    {
        var core = new CoreHarness().Handshake();
        core.PanelSends(new NotificationPartitionReadyStatus());

        Assert.IsType<SimpleAck>(core.LastSent.Message);
        Assert.Empty(core.Published);
    }

    // ── Audit T9: heartbeat ──

    [Fact]
    public void Heartbeat_FirstPollAtReadyPlusInterval_NextAfterTheAck()
    {
        var core = new CoreHarness().Ready();
        core.Advance(core.Timings.HeartbeatInterval - TimeSpan.FromMilliseconds(1));
        Assert.Empty(core.SentOfType<ConnectionPoll>());

        core.Advance(TimeSpan.FromMilliseconds(1));
        var poll = Assert.Single(core.SentOfType<ConnectionPoll>());

        core.Advance(TimeSpan.FromSeconds(1));
        core.PanelAcks(poll);
        core.Advance(core.Timings.HeartbeatInterval - TimeSpan.FromMilliseconds(1));
        Assert.Single(core.SentOfType<ConnectionPoll>());
        core.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, core.SentOfType<ConnectionPoll>().Count());
    }

    [Fact]
    public void SilentPanel_IsClosedAfterMaxMissedHeartbeats()
    {
        var core = new CoreHarness().Ready();
        var cycle = core.Timings.HeartbeatInterval + core.Timings.HeartbeatAckTimeout;

        core.Advance(cycle * (core.Timings.MaxMissedHeartbeats - 1) + core.Timings.HeartbeatInterval);
        Assert.Empty(core.Closed);

        core.Advance(core.Timings.HeartbeatAckTimeout);
        Assert.Equal(Phase.Closed, core.State.Phase);
        Assert.Equal(core.Timings.MaxMissedHeartbeats, core.SentOfType<ConnectionPoll>().Count());
    }

    [Fact]
    public void AnyTraffic_CountsAsAlive_EvenIfThePollItselfIsNotAcked()
    {
        var core = new CoreHarness().Ready();
        for (int i = 0; i < core.Timings.MaxMissedHeartbeats + 2; i++)
        {
            core.Advance(core.Timings.HeartbeatInterval);
            core.PanelSends(new NotificationPartitionReadyStatus());   // traffic, but no ack of the poll
            core.Advance(core.Timings.HeartbeatAckTimeout);
        }

        Assert.Empty(core.Closed);
        Assert.Equal(0, core.State.MissedPolls);
    }

    // ── Audit T10, T13: robustness ──

    [Fact]
    public void GarbageFrame_IsIgnored_AndTheSessionContinues()
    {
        var core = new CoreHarness().Ready();
        core.PanelSendsGarbage();
        var id = core.Request(Arm());
        core.PanelSends(ResponseTo(core.LastSent), core.LastSent.SenderSequence);

        Assert.True(core.Results[id].IsSuccess);
        Assert.Contains(core.Effects, e => e is Diagnostic { Level: Microsoft.Extensions.Logging.LogLevel.Warning });
    }

    [Fact]
    public void IncompleteHandshake_IsClosedAfterHandshakeTimeout()
    {
        var core = new CoreHarness();
        core.Advance(core.Timings.HandshakeTimeout);

        Assert.Equal(Phase.Closed, core.State.Phase);
        Assert.Contains("Handshake did not complete", core.Closed.Single());
    }

    [Fact]
    public void UnknownPanel_IsClosedDuringTheHandshake()
    {
        var core = new CoreHarness { Credentials = null };
        try { core.Handshake(); } catch (Xunit.Sdk.XunitException) { /* stops early by design */ }

        Assert.Equal(Phase.Closed, core.State.Phase);
        Assert.Contains("No valid connection settings", core.Closed.Single());
    }

    // ── Closing ──

    [Fact]
    public void PanelHangsUp_EveryWaitingRequestFails_WithDisconnected()
    {
        var core = new CoreHarness().Ready();
        var sent = core.Request(Arm());
        var queued = core.Request(Arm());
        core.TransportCloses();

        Assert.Equal(TLinkErrorCode.Disconnected, core.Results[sent].Error!.Value.Code);
        Assert.Equal(TLinkErrorCode.Disconnected, core.Results[queued].Error!.Value.Code);

        var late = core.Request(Arm());
        Assert.Equal(TLinkErrorCode.Disconnected, core.Results[late].Error!.Value.Code);
    }

    [Fact]
    public void PanelEndsTheSession_IsAckedThenClosed()
    {
        var core = new CoreHarness().Ready();
        core.PanelSends(new ConnectionEndSession());

        Assert.IsType<SimpleAck>(core.LastSent.Message);
        Assert.Equal(Phase.Closed, core.State.Phase);
    }

    [Fact]
    public void ClosingWhenReady_SaysGoodbye()
    {
        var core = new CoreHarness().Ready();
        core.CloseRequested();

        Assert.IsType<ConnectionEndSession>(core.LastSent.Message);
        Assert.Equal(Phase.Closed, core.State.Phase);
    }

    [Fact]
    public void ClosingDuringTheHandshake_DoesNotSendGoodbye()
    {
        var core = new CoreHarness();
        core.CloseRequested();

        Assert.Empty(core.Sent);
        Assert.Equal(Phase.Closed, core.State.Phase);
    }

    // ── Determinism ──

    [Fact]
    public void SameInputs_SameOutputs()
    {
        static List<string> Run()
        {
            var core = new CoreHarness().Ready();
            core.Request(PartitionStatus(1));
            core.PanelSends(PartitionStatusReply(1));
            core.Advance(TimeSpan.FromMinutes(5));
            return core.Sent.Select(p => Convert.ToHexString(PacketCodec.Encode(p))).ToList();
        }

        Assert.Equal(Run(), Run());
    }
}
