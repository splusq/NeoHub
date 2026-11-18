using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NeoHub.Api.WebSocket;

namespace NeoHub.Tests;

public class WebSocketClientTests
{
    private static byte[] Msg(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task ManyProducers_SendsNeverOverlap_AndEverythingIsDelivered()
    {
        var socket = new FakeWebSocket();
        var client = new WebSocketClient("c1", socket, NullLogger.Instance);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(p => Task.Run(() =>
        {
            for (int i = 0; i < 25; i++)
                Assert.True(client.TryEnqueue(Msg($"{p}:{i}")));
        })));
        await client.DisposeAsync();

        Assert.Equal(200, socket.Sent.Count);
        Assert.Equal(1, socket.MaxConcurrentSends);
    }

    [Fact]
    public async Task SingleProducer_OrderIsPreserved()
    {
        var socket = new FakeWebSocket { SendDelay = TimeSpan.Zero };
        var client = new WebSocketClient("c1", socket, NullLogger.Instance);

        for (int i = 0; i < 100; i++)
            client.TryEnqueue(Msg(i.ToString()));
        await client.DisposeAsync();

        Assert.Equal(Enumerable.Range(0, 100).Select(i => i.ToString()), socket.Sent);
    }

    [Fact]
    public async Task StalledClient_IsDisconnected_WithoutBlockingTheProducer()
    {
        var socket = new FakeWebSocket { SendGate = new TaskCompletionSource() };
        var client = new WebSocketClient("c1", socket, NullLogger.Instance);

        var started = DateTime.UtcNow;
        bool rejected = false;
        for (int i = 0; i < WebSocketClient.Capacity + 10 && !rejected; i++)
            rejected = !client.TryEnqueue(Msg(i.ToString()));

        Assert.True(rejected);
        Assert.True(client.IsAborted);
        Assert.True(socket.Aborted);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1), "TryEnqueue must never block");
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_SendsQueuedMessages_ThenCloses()
    {
        var socket = new FakeWebSocket();
        var client = new WebSocketClient("c1", socket, NullLogger.Instance);

        client.TryEnqueue(Msg("a"));
        client.TryEnqueue(Msg("b"));
        await client.DisposeAsync();

        Assert.Equal(new[] { "a", "b" }, socket.Sent);
        Assert.Equal(1, socket.CloseOutputCount);
        Assert.Equal(1, socket.MaxConcurrentSends);   // the close did not overlap a send
        Assert.False(client.TryEnqueue(Msg("late")));
    }
}
