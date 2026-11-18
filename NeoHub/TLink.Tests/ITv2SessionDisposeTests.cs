using DSC.TLink.ITv2;
using Microsoft.Extensions.Logging.Abstractions;

namespace DSC.TLink.Tests;

public class ITv2SessionDisposeTests
{
    private static ITv2Session CreateSession(SilentTransport transport)
        => ITv2Session.CreateForTesting(transport, "200000000001", NullLoggerFactory.Instance);

    /// <summary>
    /// The session manager disposes a session it replaces, and the connection handler's
    /// <c>await using</c> disposes it again. The second call used to throw
    /// ChannelClosedException ("ITv2 connection error" in the 2026-11-17 logs).
    /// </summary>
    [Fact]
    public async Task DisposeTwice_DoesNotThrow_AndClosesTransportOnce()
    {
        var transport = new SilentTransport();
        var session = CreateSession(transport);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentDispose_ClosesTransportOnce()
    {
        var transport = new SilentTransport();
        var session = CreateSession(transport);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => session.DisposeAsync().AsTask()));

        Assert.Equal(1, transport.DisposeCount);
    }

    /// <summary>
    /// When the session manager replaces a stale session, that session's connection handler
    /// is still reading notifications. Its stream must end cleanly so the handler unregisters
    /// without logging a spurious connection error.
    /// </summary>
    [Fact]
    public async Task DisposeWhileReading_EndsNotificationStreamCleanly()
    {
        var session = CreateSession(new SilentTransport());

        var reading = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var _ in session.GetNotificationsAsync())
                count++;
            return count;
        });

        await Task.Delay(50);
        Assert.False(reading.IsCompleted);

        await session.DisposeAsync();

        var finished = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(reading, finished);
        Assert.Equal(0, await reading);
    }

    [Fact]
    public async Task ReadingAfterDispose_YieldsNothing()
    {
        var session = CreateSession(new SilentTransport());
        await session.DisposeAsync();

        var count = 0;
        await foreach (var _ in session.GetNotificationsAsync())
            count++;

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CallerCancellation_StillThrows()
    {
        var session = CreateSession(new SilentTransport());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in session.GetNotificationsAsync(cts.Token)) { }
        });

        await session.DisposeAsync();
    }
}
