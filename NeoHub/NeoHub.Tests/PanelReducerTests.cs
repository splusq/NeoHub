using System.Collections.Immutable;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using NeoHub.Domain;
using NeoHub.Services.Models;

namespace NeoHub.Tests;

/// <summary>Panel state as a pure function: synchronous, no clock, no threads.</summary>
public class PanelReducerTests
{
    private const string Panel = "200000000001";
    private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PanelStoreState Run(params PanelEvent[] events) => Run(PanelStoreState.Empty, events);

    private static PanelStoreState Run(PanelStoreState state, params PanelEvent[] events)
    {
        foreach (var e in events)
            state = PanelReducer.Apply(state, e).State;
        return state;
    }

    private static MessageReceived Msg(IMessageData m, Guid? connection = null, int seconds = 0)
        => new(Panel, connection ?? A, m, T.AddSeconds(seconds));

    private static NotificationLifestyleZoneStatus Zone(byte zone, bool open) => new()
    {
        ZoneNumber = zone,
        Status = open ? NotificationLifestyleZoneStatus.LifeStyleZoneStatusCode.Open : NotificationLifestyleZoneStatus.LifeStyleZoneStatusCode.Restored,
    };

    // ── Against the real capture ──

    /// <summary>
    /// The real evening: arm stay from HomeKit, disarm, doors, bypass. Notifications come from
    /// the protocol core replaying the capture; state is checked at each human marker.
    /// </summary>
    [Fact]
    public void CapturedSession_ProducesTheStatesThePanelReported()
    {
        var state = Run(new SessionStarted(Panel, A, CapturedSession.T0));
        var atMarker = new Dictionary<string, SessionState>();
        var bypassSeen = false;

        foreach (var item in CapturedSession.Run())
        {
            if (item.Marker is { } marker)
            {
                atMarker[marker] = state.Sessions[Panel];
                continue;
            }
            var (next, changes) = PanelReducer.Apply(state, new MessageReceived(Panel, A, item.Message!, item.At));
            state = next;
            bypassSeen |= changes.OfType<ZoneChanged>().Any(z => z.Zone.ZoneNumber == 16 && z.Zone.IsBypassed);
        }
        var final = state.Sessions[Panel];

        // Just before the disarm: armed stay, in its 120 s exit delay.
        var armed = atMarker["HomeKit: disarm"].Partitions[1];
        Assert.Equal(PartitionStatus.ArmedHome, armed.Status);
        Assert.True(armed.ExitDelayActive);
        Assert.Equal(120, armed.ExitDelayDurationSeconds);

        // Disarmed, ready, exit delay cleared, by the end.
        var partition = final.Partitions[1];
        Assert.Equal(PartitionStatus.Disarmed, partition.Status);
        Assert.False(partition.ExitDelayActive);

        // Doors: zone 1 was open at its marker; zones 1 and 9 are closed at the end.
        Assert.True(atMarker["doors: zone 1 close"].Zones[1].IsOpen);
        Assert.False(final.Zones[1].IsOpen);
        Assert.False(final.Zones[9].IsOpen);

        // Zone 16 was bypassed and then unbypassed.
        Assert.True(bypassSeen);
        Assert.False(final.Zones[16].IsBypassed);

        // The panel's clock broadcast arrived and decoded (the panel's own, wrong, date).
        Assert.Equal(new DateTime(2026, 11, 16), final.PanelDateTime!.Value.Date);
    }

    // ── Connections ──

    [Fact]
    public void EventsBeforeAnyConnection_AreIgnored()
        => Assert.Empty(Run(Msg(Zone(1, true))).Sessions);

    [Fact]
    public void StaleConnection_CannotChangeTheNewConnectionsState()
    {
        var state = Run(
            new SessionStarted(Panel, A, T),
            new SessionStarted(Panel, B, T.AddSeconds(1)),
            Msg(Zone(1, true), A),
            new ConnectionPhaseChanged(Panel, A, ConnectionPhase.Connected));

        var session = state.Sessions[Panel];
        Assert.Equal(B, session.ConnectionId);
        Assert.Empty(session.Zones);
        Assert.Equal(ConnectionPhase.Handshake, session.ConnectionPhase);
    }

    /// <summary>The 2026-11-17 shape: the old connection ends after the new one started.</summary>
    [Fact]
    public void StaleConnectionEnding_DoesNotRemoveTheNewConnectionsState()
    {
        var state = Run(
            new SessionStarted(Panel, A, T),
            new SessionStarted(Panel, B, T.AddSeconds(1)),
            Msg(Zone(1, true), B),
            new SessionEnded(Panel, A));

        Assert.True(state.Sessions[Panel].Zones[1].IsOpen);
        Assert.Equal(B, state.Sessions[Panel].ConnectionId);
    }

    [Fact]
    public void ConnectionEnding_RemovesTheState_AndAReconnectStartsFresh()
    {
        var ended = Run(new SessionStarted(Panel, A, T), Msg(Zone(1, true)), new SessionEnded(Panel, A));
        var reconnected = Run(ended, new SessionStarted(Panel, B, T.AddSeconds(5)));

        Assert.False(ended.Sessions.ContainsKey(Panel));
        Assert.Empty(reconnected.Sessions[Panel].Zones);
        Assert.Equal(B, reconnected.Sessions[Panel].ConnectionId);
    }

    // ── Pulled status vs live events ──

    [Fact]
    public void PulledZoneStatus_IsIgnored_IfTheZoneChangedLiveAfterTheRequest()
    {
        var state = Run(new SessionStarted(Panel, A, T));
        var requestedAt = state.Version;
        state = Run(state,
            Msg(Zone(1, false), seconds: 1),   // live: closed, after the request went out
            new ZoneStatusRead(Panel, A, [new ZoneReading(1, Open: true, false, false, false)], requestedAt, T.AddSeconds(2)));

        Assert.False(state.Sessions[Panel].Zones[1].IsOpen);
    }

    [Fact]
    public void PulledZoneStatus_IsApplied_WhenNothingNewerHappened()
    {
        var state = Run(new SessionStarted(Panel, A, T), Msg(Zone(1, false)));
        state = Run(state, new ZoneStatusRead(Panel, A, [new ZoneReading(1, true, false, false, true)], state.Version, T.AddSeconds(2)));

        var zone = state.Sessions[Panel].Zones[1];
        Assert.True(zone.IsOpen);
        Assert.True(zone.IsBypassed);
    }

    [Fact]
    public void PulledPartitionStatus_IsIgnored_IfItChangedLiveAfterTheRequest()
    {
        var state = Run(new SessionStarted(Panel, A, T));
        var requestedAt = state.Version;
        state = Run(state,
            Msg(new NotificationArmDisarm { Partition = 1, ArmMode = ArmingMode.AwayArm }, seconds: 1),
            new PartitionStatusRead(Panel, A, new PartitionReading(1, PartitionStatus.Disarmed, true, false), requestedAt, T.AddSeconds(2)));

        Assert.Equal(PartitionStatus.ArmedAway, state.Sessions[Panel].Partitions[1].Status);
    }

    // ── Live notifications ──

    [Fact]
    public void ExitDelay_StartsAtTheEventTime_AndDisarmClearsIt()
    {
        var state = Run(new SessionStarted(Panel, A, T),
            Msg(new NotificationExitDelay { Partition = 1, DurationInSeconds = 60, DelayFlags = NotificationExitDelay.ExitDelayFlags.ExitDelayActive }, seconds: 3));
        var p = state.Sessions[Panel].Partitions[1];
        Assert.True(p.ExitDelayActive);
        Assert.Equal(T.AddSeconds(3).UtcDateTime, p.ExitDelayStartedAt);

        state = Run(state, Msg(new NotificationArmDisarm { Partition = 1, ArmMode = ArmingMode.Disarm }, seconds: 10));
        p = state.Sessions[Panel].Partitions[1];
        Assert.Equal(PartitionStatus.Disarmed, p.Status);
        Assert.False(p.ExitDelayActive);
        Assert.Null(p.ExitDelayStartedAt);
    }

    /// <summary>
    /// Kept from the previous handler and documented here: any ready-status notification marks
    /// the partition disarmed. If the panel ever sends one while armed, this would be wrong.
    /// </summary>
    [Fact]
    public void ReadyStatus_MarksThePartitionDisarmed_AsBefore()
    {
        var state = Run(new SessionStarted(Panel, A, T),
            Msg(new NotificationArmDisarm { Partition = 1, ArmMode = ArmingMode.StayArm }),
            Msg(new NotificationPartitionReadyStatus { PartitionNumber = 1, Status = NotificationPartitionReadyStatus.PartitionReadyStatusEnum.NotReadyToArm }));

        var p = state.Sessions[Panel].Partitions[1];
        Assert.Equal(PartitionStatus.Disarmed, p.Status);
        Assert.False(p.IsReady);
    }

    [Fact]
    public void ZoneLabels_SplitIntoTwoLines_AndShortLabelsDoNotCrash()
    {
        var state = Run(new SessionStarted(Panel, A, T), Msg(new NotificationLabelText
        {
            Collection = NotificationLabelText.LabelCollection.Zone,
            Start = 3,
            End = 4,
            Labels = ["ZONE ONE      ENTRY         ", "SHORT"],
        }));

        var zones = state.Sessions[Panel].Zones;
        Assert.Equal("ZONE ONE ENTRY", zones[3].DisplayName);
        Assert.Equal("SHORT", zones[4].DisplayName);
    }

    [Fact]
    public void InstallerSetup_SetsDefinitionsLabelsAndAssignments()
    {
        var state = Run(new SessionStarted(Panel, A, T), new InstallerSetupApplied(Panel, A,
            [new PartitionSetup(1, "HOUSE")],
            [new ZoneSetup(2, null, "KITCHEN", null, HasLabel: true, Partitions: ImmutableArray.Create<byte>(1, 2))]));

        Assert.Equal("HOUSE", state.Sessions[Panel].Partitions[1].Name);
        var zone = state.Sessions[Panel].Zones[2];
        Assert.Equal("KITCHEN", zone.DisplayName);
        Assert.Equal(new byte[] { 1, 2 }, zone.Partitions.ToArray());
    }

    [Fact]
    public void InitialPull_MarksInitialized_AndAnnouncesCompletion()
    {
        var (state, changes) = PanelReducer.Apply(Run(new SessionStarted(Panel, A, T)), new InitialPullFinished(Panel, A, Completed: true));

        Assert.True(state.Sessions[Panel].IsInitialized);
        Assert.Equal(ConnectionPhase.Connected, state.Sessions[Panel].ConnectionPhase);
        Assert.Contains(changes, c => c is ConfigurationCompleted);
    }

    [Fact]
    public void SameEvents_SameState()
    {
        PanelStoreState Once() => Run(new SessionStarted(Panel, A, T), Msg(Zone(1, true)), Msg(Zone(9, true), seconds: 2), Msg(Zone(1, false), seconds: 3));
        static IEnumerable<string> Summary(SessionState s) => s.Zones.OrderBy(z => z.Key).Select(z =>
            $"{z.Key} open={z.Value.IsOpen} parts={string.Join(',', z.Value.Partitions)} at={z.Value.LastUpdated:O} v={z.Value.Version}");

        var a = Once().Sessions[Panel];
        var b = Once().Sessions[Panel];

        Assert.Equal(Summary(a), Summary(b));
        Assert.Equal(a.LastUpdated, b.LastUpdated);
    }
}
