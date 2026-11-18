using Microsoft.Extensions.Logging.Abstractions;
using NeoHub.Domain;

namespace NeoHub.Tests;

/// <summary>
/// The per-panel operation queue. Ordering is controlled with gates the test opens itself;
/// nothing waits on time except a hang guard.
/// </summary>
public class PanelOperationsTests : IAsyncLifetime
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private readonly PanelOperations _ops = new(NullLogger<PanelOperations>.Instance);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _ops.DisposeAsync();

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task SamePanel_OperationsRunOneAtATime_InOrder()
    {
        var log = new List<string>();
        var firstStarted = Gate();
        var releaseFirst = Gate();

        var first = _ops.RunAsync("p1", "first", async _ =>
        {
            log.Add("first start");
            firstStarted.SetResult();
            await releaseFirst.Task;
            log.Add("first end");
            return 1;
        });
        await firstStarted.Task.WaitAsync(Guard);
        var second = _ops.RunAsync("p1", "second", _ => { log.Add("second"); return Task.FromResult(2); });

        // The second can't start while the first holds the panel.
        Assert.False(second.IsCompleted);
        releaseFirst.SetResult();

        Assert.Equal(1, await first.WaitAsync(Guard));
        Assert.Equal(2, await second.WaitAsync(Guard));
        Assert.Equal(["first start", "first end", "second"], log);
    }

    [Fact]
    public async Task DifferentPanels_DoNotWaitForEachOther()
    {
        var release = Gate();
        var blocked = _ops.RunAsync("p1", "slow", async _ => { await release.Task; return 1; });

        var other = await _ops.RunAsync("p2", "fast", _ => Task.FromResult(2)).WaitAsync(Guard);

        Assert.Equal(2, other);
        Assert.False(blocked.IsCompleted);
        release.SetResult();
        await blocked.WaitAsync(Guard);
    }

    [Fact]
    public async Task CancelledWhileQueued_NeverRuns_AndTheNextOneDoes()
    {
        var release = Gate();
        var started = Gate();
        var holding = _ops.RunAsync("p1", "holding", async _ => { started.SetResult(); await release.Task; return 0; });
        await started.Task.WaitAsync(Guard);

        using var cancel = new CancellationTokenSource();
        var ran = false;
        var cancelled = _ops.RunAsync("p1", "cancelled", _ => { ran = true; return Task.FromResult(0); }, cancel.Token);
        var next = _ops.RunAsync("p1", "next", _ => Task.FromResult(3));
        cancel.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Guard));
        Assert.Equal(3, await next.WaitAsync(Guard));
        Assert.False(ran);
        await holding;
    }

    [Fact]
    public async Task CancelledWhileRunning_TheOperationSeesItsToken()
    {
        using var cancel = new CancellationTokenSource();
        var started = Gate();
        var op = _ops.RunAsync("p1", "long", async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }, cancel.Token);
        await started.Task.WaitAsync(Guard);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => op.WaitAsync(Guard));
        Assert.Equal(4, await _ops.RunAsync("p1", "after", _ => Task.FromResult(4)).WaitAsync(Guard));
    }

    [Fact]
    public async Task FailingOperation_ReportsItsError_AndTheLaneKeepsGoing()
    {
        var failing = _ops.RunAsync<int>("p1", "fails", _ => throw new InvalidOperationException("panel said no"));
        var next = _ops.RunAsync("p1", "next", _ => Task.FromResult(5));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(Guard));
        Assert.Equal("panel said no", error.Message);
        Assert.Equal(5, await next.WaitAsync(Guard));
    }

    /// <summary>Queueing for the same panel from inside its own operation would wait forever.</summary>
    [Fact]
    public async Task NestedOperationForTheSamePanel_FailsFast_InsteadOfDeadlocking()
    {
        var outer = _ops.RunAsync("p1", "outer", async ct =>
        {
            var ex = Assert.Throws<InvalidOperationException>(() => { _ = _ops.RunAsync("p1", "inner", _ => Task.FromResult(0)); });
            Assert.Contains("would wait on itself", ex.Message);
            // A different panel is fine.
            return await _ops.RunAsync("p2", "elsewhere", _ => Task.FromResult(7));
        });

        Assert.Equal(7, await outer.WaitAsync(Guard));
    }

    [Fact]
    public async Task ManyCallersAtOnce_NeverOverlap()
    {
        var running = 0;
        var maxRunning = 0;
        var calls = Enumerable.Range(0, 40).Select(i => Task.Run(() => _ops.RunAsync("p1", $"op {i}", async _ =>
        {
            maxRunning = Math.Max(maxRunning, ++running);
            await Task.Yield();
            running--;
            return i;
        }))).ToList();

        var results = await Task.WhenAll(calls).WaitAsync(Guard);

        Assert.Equal(Enumerable.Range(0, 40), results.Order());
        Assert.Equal(1, maxRunning);
    }
}
