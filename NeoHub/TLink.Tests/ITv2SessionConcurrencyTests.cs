using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace DSC.TLink.Tests;

/// <summary>
/// Drives a real ITv2Session over <see cref="ScriptedTransport"/> to check the session's
/// concurrency guarantees: one writer at a time, one command transaction at a time, bounded
/// waits, and clean shutdown while work is in flight.
/// </summary>
public class ITv2SessionConcurrencyTests : IAsyncLifetime
{
    private static readonly ITv2SessionTimings FastTimings = new()
    {
        ReadyQuietPeriod = TimeSpan.FromMilliseconds(30),
        MaxReadyWait = TimeSpan.FromMilliseconds(300),
        CommandTimeout = TimeSpan.FromMilliseconds(500),
        HeartbeatInterval = TimeSpan.FromHours(1),
        DisposeTimeout = TimeSpan.FromMilliseconds(500),
    };

    private readonly ScriptedTransport _transport = new();
    private ITv2Session _session = null!;

    private ITv2Session Start(ITv2SessionTimings? timings = null)
        => _session = ITv2Session.CreateForTesting(_transport, "200000000001", NullLoggerFactory.Instance, timings ?? FastTimings);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_session is not null)
            await _session.DisposeAsync();
        Assert.Empty(_transport.CallbackErrors);
    }

    private static CommandRequestMessage PartitionStatusRequest(int partition)
        => new() { Request = new ModulePartitionStatus { Partition = partition } };

    private static ModulePartitionStatus PartitionStatusReply(int partition)
        => new() { Partition = partition, PartitionStatus = [0x02, 0x00] };

    /// <summary>Acks every notification the session sends (e.g. heartbeat polls).</summary>
    private static Task AckNotifications(SentPacket packet, ScriptedTransport transport)
    {
        if (packet.Message is ConnectionPoll)
            transport.Ack(packet);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentSendsAndInboundTraffic_NeverOverlapWrites()
    {
        _transport.SendDelay = TimeSpan.FromMilliseconds(2);
        Start();
        await WaitUntilReady();
        _transport.OnSend = AckNotifications;

        // Senders (heartbeat-style polls skip the command gate, so they really run concurrently)
        // racing the receive pump, which acks every inbound packet on its own thread.
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var inbound = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _transport.Inject(new ConnectionPoll());
                await Task.Delay(1);
            }
        });
        var senders = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var results = new List<Result<IMessageData>>();
            while (!stop.IsCancellationRequested)
                results.Add(await _session.SendAsync(new ConnectionPoll()));
            return results;
        })).ToList();

        await inbound;
        var all = (await Task.WhenAll(senders)).SelectMany(r => r).ToList();

        Assert.NotEmpty(all);
        Assert.All(all, r => Assert.True(r.IsSuccess, r.Error?.Message));
        Assert.Equal(1, _transport.MaxConcurrentSends);
    }

    /// <summary>
    /// Replies are matched by message type. Two requests of the same type in flight at once
    /// could swap replies; the transaction gate keeps them one at a time.
    /// </summary>
    [Fact]
    public async Task SameTypeRequests_EachGetsItsOwnReply()
    {
        int requestsSeen = 0;
        _transport.OnSend = async (packet, transport) =>
        {
            if (packet.Message is not (CommandRequestMessage or DefaultMessage) || packet.Message is SimpleAck)
                return;

            // First request is answered slowly, second quickly: without the gate the second
            // reply would arrive first and be taken by the first request.
            if (Interlocked.Increment(ref requestsSeen) == 1)
            {
                await Task.Delay(100);
                transport.Inject(PartitionStatusReply(1));
            }
            else
            {
                transport.Inject(PartitionStatusReply(2));
            }
        };
        Start();

        var first = _session.SendAsync(PartitionStatusRequest(1));
        await WaitUntil(() => requestsSeen >= 1);
        var second = _session.SendAsync(PartitionStatusRequest(2));

        var firstReply = await first;
        var secondReply = await second;

        Assert.True(firstReply.IsSuccess, firstReply.Error?.Message);
        Assert.True(secondReply.IsSuccess, secondReply.Error?.Message);
        Assert.Equal(1, Assert.IsType<ModulePartitionStatus>(firstReply.Value).Partition);
        Assert.Equal(2, Assert.IsType<ModulePartitionStatus>(secondReply.Value).Partition);
    }

    [Fact]
    public async Task UnansweredCommand_FailsWithTimeout_InsteadOfHanging()
    {
        Start();

        var started = DateTime.UtcNow;
        var result = await _session.SendAsync(PartitionStatusRequest(1)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsFailure);
        Assert.Equal(TLinkErrorCode.Timeout, result.Error!.Value.Code);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// A request that timed out used to stay registered; the next reply of the same type was
    /// handed to it (and lost), so the following request timed out too.
    /// </summary>
    [Fact]
    public async Task TimedOutRequest_DoesNotSwallowTheNextReply()
    {
        bool answer = false;
        _transport.OnSend = (packet, transport) =>
        {
            if (Volatile.Read(ref answer) && packet.Message is not SimpleAck)
                transport.Inject(PartitionStatusReply(7));
            return Task.CompletedTask;
        };
        Start();

        var timedOut = await _session.SendAsync(PartitionStatusRequest(7));
        Assert.Equal(TLinkErrorCode.Timeout, timedOut.Error!.Value.Code);

        Volatile.Write(ref answer, true);
        var answered = await _session.SendAsync(PartitionStatusRequest(7));

        Assert.True(answered.IsSuccess, answered.Error?.Message);
        Assert.Equal(7, Assert.IsType<ModulePartitionStatus>(answered.Value).Partition);
    }

    [Fact]
    public async Task PanelHangsUp_PendingCommandFailsWithDisconnected()
    {
        Start();
        await WaitUntilReady();

        var pending = _session.SendAsync(PartitionStatusRequest(1));
        await WaitUntil(() => _transport.Sent.Count >= 1);
        _transport.Close();

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TLinkErrorCode.Disconnected, result.Error!.Value.Code);
        Assert.True(_session.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task DisposeWhileSending_EverySendCompletes_WithoutThrowing()
    {
        Start();
        await WaitUntilReady();

        var sends = Enumerable.Range(0, 10)
            .Select(i => _session.SendAsync(PartitionStatusRequest(i + 1)))
            .ToList();
        await Task.Delay(20);

        await _session.DisposeAsync();

        var results = await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, r => Assert.Equal(TLinkErrorCode.Disconnected, r.Error!.Value.Code));
        Assert.True(_session.Lifetime.IsCancellationRequested);
        Assert.Equal(1, _transport.DisposeCount);
    }

    [Fact]
    public async Task SendAfterDispose_ReturnsDisconnected()
    {
        Start();
        await _session.DisposeAsync();

        var result = await _session.SendAsync(new ConnectionPoll());

        Assert.Equal(TLinkErrorCode.Disconnected, result.Error!.Value.Code);
    }

    /// <summary>
    /// Readiness waited for a quiet period that a chatty panel might never give; every send
    /// then waited forever. MaxReadyWait bounds it.
    /// </summary>
    [Fact]
    public async Task PanelThatNeverGoesQuiet_StillBecomesReady()
    {
        _transport.OnSend = AckNotifications;
        Start();

        using var chatter = new CancellationTokenSource();
        var noise = Task.Run(async () =>
        {
            while (!chatter.IsCancellationRequested)
            {
                _transport.Inject(new ConnectionPoll());
                await Task.Delay(10);
            }
        });

        var result = await _session.SendAsync(new ConnectionPoll()).WaitAsync(TimeSpan.FromSeconds(5));
        chatter.Cancel();
        await noise;

        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task UnresponsivePanel_HeartbeatClosesSession()
    {
        Start(FastTimings with
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatAckTimeout = TimeSpan.FromMilliseconds(50),
            MaxMissedHeartbeats = 2,
        });

        await WaitUntil(() => _session.Lifetime.IsCancellationRequested, TimeSpan.FromSeconds(5));

        var count = 0;
        await foreach (var _ in _session.GetNotificationsAsync())
            count++;
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ResponsivePanel_HeartbeatKeepsSessionOpen()
    {
        _transport.OnSend = AckNotifications;
        Start(FastTimings with
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(30),
            HeartbeatAckTimeout = TimeSpan.FromMilliseconds(200),
            MaxMissedHeartbeats = 2,
        });

        await WaitUntil(() => _transport.Sent.Count(p => p.Message is ConnectionPoll) >= 5, TimeSpan.FromSeconds(5));

        Assert.False(_session.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task RecoverableTransportError_DoesNotEndSession()
    {
        _transport.OnSend = AckNotifications;
        Start();
        await WaitUntilReady();

        _transport.InjectFailure(TLinkErrorCode.FramingError);
        _transport.Inject(new ConnectionPoll());
        await Task.Delay(50);

        Assert.False(_session.Lifetime.IsCancellationRequested);
        var result = await _session.SendAsync(new ConnectionPoll());
        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    [Fact]
    public async Task NotificationsQueuedBeforeHangUp_AreStillDelivered()
    {
        Start();
        await WaitUntilReady();

        _transport.Inject(new ConnectionPoll());
        _transport.Inject(new ConnectionPoll());
        _transport.Close();
        await WaitUntil(() => _session.Lifetime.IsCancellationRequested);

        var received = 0;
        await foreach (var _ in _session.GetNotificationsAsync().WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token))
            received++;

        Assert.Equal(2, received);
    }

    private async Task WaitUntilReady()
    {
        // A successful acked poll proves readiness.
        var previous = _transport.OnSend;
        _transport.OnSend = async (p, t) =>
        {
            await AckNotifications(p, t);
            if (previous is not null) await previous(p, t);
        };
        var result = await _session.SendAsync(new ConnectionPoll());
        Assert.True(result.IsSuccess, result.Error?.Message);
        _transport.OnSend = previous;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time");
            await Task.Delay(5);
        }
    }
}
