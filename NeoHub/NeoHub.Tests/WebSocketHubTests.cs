using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NeoHub.Api.WebSocket;
using NeoHub.Api.WebSocket.Models;
using NeoHub.Domain;
using NeoHub.Services;

namespace NeoHub.Tests;

/// <summary>
/// The WebSocket side: the hub, each client's outbox, and the request loop. Every wait reads a
/// message the code under test sends, or a gate the test opens itself; none depends on timing.
/// </summary>
public class WebSocketHubTests : IAsyncLifetime
{
    private const string Panel = "200000000001";
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);   // hang guard only

    private readonly PanelStore _store = new(NullLogger<PanelStore>.Instance);
    private ClientHub _hub = null!;
    private IPanelMessageSink Sink => _store;

    public async Task InitializeAsync()
    {
        _hub = new ClientHub(_store, () => _store.GetAllSessions().Keys, NullLogger<ClientHub>.Instance);
        await _hub.Ready.WaitAsync(Guard);
    }

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        await _store.DisposeAsync();
    }

    private static NotificationLifestyleZoneStatus Zone(byte zone, bool open) => new()
    {
        ZoneNumber = zone,
        Status = open ? NotificationLifestyleZoneStatus.LifeStyleZoneStatusCode.Open : NotificationLifestyleZoneStatus.LifeStyleZoneStatusCode.Restored,
    };

    private (WebSocketClient Client, ScriptedSocket Socket) Join(string id, TimeProvider? time = null)
    {
        var socket = new ScriptedSocket();
        var client = new WebSocketClient(id, socket, time ?? TimeProvider.System, NullLogger.Instance);
        _hub.Add(client);
        return (client, socket);
    }

    [Fact]
    public async Task ZoneChanges_ReachEveryClient_InStoreOrder()
    {
        var (_, one) = Join("one");
        var (_, two) = Join("two");
        await _hub.QuiesceAsync().WaitAsync(Guard);

        Sink.Started(Panel, A, T);
        for (int i = 0; i < 50; i++)
            Sink.Accept(new PanelMessage(Panel, A, Zone(1, i % 2 == 0), T.AddSeconds(i)));

        var expected = Enumerable.Range(0, 50).Select(i => i % 2 == 0).ToList();
        foreach (var socket in new[] { one, two })
        {
            var opens = new List<bool>();
            for (int i = 0; i < 50; i++)
            {
                var m = await socket.NextAsync().WaitAsync(Guard);
                Assert.Equal("zone_update", m.Type);
                opens.Add(m.Json.GetProperty("open").GetBoolean());
            }
            Assert.Equal(expected, opens);
        }
    }

    [Fact]
    public async Task FullStateRequests_AmongChanges_ClientEndsWithTheStoresState()
    {
        var (client, socket) = Join("one");
        Sink.Started(Panel, A, T);
        await _store.DispatchAsync(new UserListUpdated("barrier", l => l)).WaitAsync(Guard);
        await _hub.QuiesceAsync().WaitAsync(Guard);

        var requests = 0;
        for (int i = 0; i < 30; i++)
        {
            Sink.Accept(new PanelMessage(Panel, A, Zone((byte)(i % 3 + 1), i % 4 < 2), T.AddSeconds(i)));
            if (i % 5 == 0) { _hub.RequestFullState(client.Id); requests++; }
        }

        // Apply what the client receives, in order, the way an integration does.
        var model = new Dictionary<int, bool>();
        for (int i = 0; i < 30 + requests; i++)
        {
            var m = await socket.NextAsync().WaitAsync(Guard);
            if (m.Type == "full_state")
            {
                model.Clear();
                foreach (var z in m.Json.GetProperty("sessions")[0].GetProperty("zones").EnumerateArray())
                    model[z.GetProperty("zone_number").GetInt32()] = z.GetProperty("open").GetBoolean();
            }
            else
            {
                Assert.Equal("zone_update", m.Type);
                model[m.Json.GetProperty("zone_number").GetInt32()] = m.Json.GetProperty("open").GetBoolean();
            }
        }

        Assert.Equal(_store.GetZones(Panel).ToDictionary(kv => (int)kv.Key, kv => kv.Value.IsOpen), model);
    }

    [Fact]
    public async Task RemovedClient_GetsNothingMore_OthersContinue()
    {
        var (gone, goneSocket) = Join("gone");
        var (_, stays) = Join("stays");
        Sink.Started(Panel, A, T);
        Sink.Accept(new PanelMessage(Panel, A, Zone(1, true), T));
        Assert.Equal("zone_update", (await goneSocket.NextAsync().WaitAsync(Guard)).Type);
        Assert.Equal("zone_update", (await stays.NextAsync().WaitAsync(Guard)).Type);

        _hub.Remove(gone.Id);
        await gone.Completion.WaitAsync(Guard);
        Sink.Accept(new PanelMessage(Panel, A, Zone(2, true), T));

        var m = await stays.NextAsync().WaitAsync(Guard);
        Assert.Equal(2, m.Json.GetProperty("zone_number").GetInt32());
        await _hub.QuiesceAsync().WaitAsync(Guard);
        Assert.False(goneSocket.Sent.Reader.TryRead(out _));
    }

    [Fact]
    public async Task StalledClient_IsAborted_OthersUnaffected()
    {
        var time = new FakeTimeProvider(T);
        var (stalled, stalledSocket) = Join("stalled", time);
        stalledSocket.StallSends = true;
        var (_, healthy) = Join("healthy");

        Sink.Started(Panel, A, T);
        Sink.Accept(new PanelMessage(Panel, A, Zone(1, true), T));
        await stalledSocket.SendStarted.Task.WaitAsync(Guard);
        Assert.Equal("zone_update", (await healthy.NextAsync().WaitAsync(Guard)).Type);

        time.Advance(WebSocketClient.SendTimeout);
        await stalledSocket.Aborted.Task.WaitAsync(Guard);
        await stalled.Completion.WaitAsync(Guard);

        Sink.Accept(new PanelMessage(Panel, A, Zone(2, true), T));
        Assert.Equal(2, (await healthy.NextAsync().WaitAsync(Guard)).Json.GetProperty("zone_number").GetInt32());
    }

    [Fact]
    public async Task ClientThatFallsTooFarBehind_IsAborted()
    {
        var socket = new ScriptedSocket { StallSends = true };
        var client = new WebSocketClient("slow", socket, new FakeTimeProvider(T), NullLogger.Instance);

        Assert.True(client.Post(new ErrorMessage { Message = "first" }));
        await socket.SendStarted.Task.WaitAsync(Guard);          // the loop holds the first one
        for (int i = 0; i < WebSocketClient.OutboxCapacity; i++)
            Assert.True(client.Post(new ErrorMessage { Message = $"m{i}" }));

        Assert.False(client.Post(new ErrorMessage { Message = "one too many" }));
        await socket.Aborted.Task.WaitAsync(Guard);
    }

    [Fact]
    public async Task Sends_NeverOverlap_AndKeepPostOrder()
    {
        var socket = new ScriptedSocket();
        var client = new WebSocketClient("one", socket, TimeProvider.System, NullLogger.Instance);
        for (int i = 0; i < 200; i++)
            Assert.True(client.Post(new ErrorMessage { Message = $"m{i}" }));

        var got = new List<string>();
        for (int i = 0; i < 200; i++)
            got.Add((await socket.NextAsync().WaitAsync(Guard)).Json.GetProperty("message").GetString()!);

        Assert.Equal(Enumerable.Range(0, 200).Select(i => $"m{i}"), got);
        Assert.Equal(1, socket.MaxConcurrentSends);
    }

    // ── The request loop ──

    private PanelWebSocketHandler Handler(FakeCommands? commands = null)
        => new(_hub, commands ?? new FakeCommands(), TimeProvider.System, NullLogger<PanelWebSocketHandler>.Instance);

    [Fact]
    public async Task FragmentedRequest_IsReassembled()
    {
        var socket = new ScriptedSocket();
        var run = Handler().RunClientAsync(socket, CancellationToken.None);

        socket.Receive("{\"type\":\"get_fu", endOfMessage: false);
        socket.Receive("ll_state\"}", endOfMessage: true);

        Assert.Equal("full_state", (await socket.NextAsync().WaitAsync(Guard)).Type);
        socket.ReceiveClose();
        await run.WaitAsync(Guard);
    }

    [Fact]
    public async Task OversizeRequest_ClosesWithMessageTooBig()
    {
        var socket = new ScriptedSocket();
        var run = Handler().RunClientAsync(socket, CancellationToken.None);

        var chunk = new string(' ', 4000);
        for (int i = 0; i * chunk.Length <= PanelWebSocketHandler.MaxMessageBytes; i++)
            socket.Receive(chunk, endOfMessage: false);

        await run.WaitAsync(Guard);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.ClosedWith);
    }

    [Fact]
    public async Task InvalidJson_GetsAnError_WithoutEchoingThePayload()
    {
        var socket = new ScriptedSocket();
        var run = Handler().RunClientAsync(socket, CancellationToken.None);

        socket.Receive("{\"type\":\"disarm\",\"code\":\"9876\"", endOfMessage: true);

        var m = await socket.NextAsync().WaitAsync(Guard);
        Assert.Equal("error", m.Type);
        Assert.DoesNotContain("9876", m.Text);
        socket.ReceiveClose();
        await run.WaitAsync(Guard);
    }

    [Fact]
    public async Task FailedCommand_ErrorGoesToTheClientThatSentIt()
    {
        var commands = new FakeCommands { Result = PanelCommandResult.Error(DSC.TLink.TLinkErrorCode.Timeout, "panel did not answer") };
        var (_, bystander) = Join("bystander");
        var socket = new ScriptedSocket();
        var run = Handler(commands).RunClientAsync(socket, CancellationToken.None);

        socket.Receive("{\"type\":\"arm_away\",\"session_id\":\"" + Panel + "\",\"partition_number\":1}", endOfMessage: true);

        var m = await socket.NextAsync().WaitAsync(Guard);
        Assert.Equal("error", m.Type);
        Assert.Equal("panel did not answer", m.Json.GetProperty("message").GetString());
        Assert.Equal((Panel, (byte)1, ArmingMode.AwayArm), commands.LastArm);

        socket.ReceiveClose();
        await run.WaitAsync(Guard);
        await _hub.QuiesceAsync().WaitAsync(Guard);
        Assert.False(bystander.Sent.Reader.TryRead(out _));
    }

    // ── Fakes ──

    private sealed class FakeCommands : IPanelCommandService
    {
        public PanelCommandResult Result { get; init; } = PanelCommandResult.Ok();
        public (string, byte, ArmingMode)? LastArm { get; private set; }

        public Task<PanelCommandResult> ArmAsync(string sessionId, byte partition, ArmingMode mode, string? accessCode = null)
        {
            LastArm = (sessionId, partition, mode);
            return Task.FromResult(Result);
        }
        public Task<PanelCommandResult> DisarmAsync(string sessionId, byte partition, string? accessCode = null) => Task.FromResult(Result);
        public Task<PanelCommandResult> BypassZoneAsync(string sessionId, byte partition, byte zoneNumber, bool bypass, string? accessCode = null) => Task.FromResult(Result);
    }

    public sealed record Sent(string Text)
    {
        public JsonElement Json { get; } = JsonDocument.Parse(Text).RootElement;
        public string Type => Json.GetProperty("type").GetString()!;
    }

    /// <summary>An in-memory WebSocket: the test scripts what arrives and reads what was sent.</summary>
    public sealed class ScriptedSocket : System.Net.WebSockets.WebSocket
    {
        private readonly Channel<(byte[] Data, WebSocketMessageType Type, bool End)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType, bool)>();
        private WebSocketState _state = WebSocketState.Open;
        private int _sending;

        public Channel<Sent> Sent { get; } = Channel.CreateUnbounded<Sent>();
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Sends never finish on their own (only cancellation ends them).</summary>
        public bool StallSends { get; set; }
        public int MaxConcurrentSends { get; private set; }
        public WebSocketCloseStatus? ClosedWith { get; private set; }

        public async Task<Sent> NextAsync() => await Sent.Reader.ReadAsync();

        public void Receive(string text, bool endOfMessage)
            => _incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage));

        public void ReceiveClose() => _incoming.Writer.TryWrite(([], WebSocketMessageType.Close, true));

        public override WebSocketCloseStatus? CloseStatus => ClosedWith;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
            _incoming.Writer.TryComplete();
            Aborted.TrySetResult();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ClosedWith = closeStatus;
            _state = _state == WebSocketState.CloseReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose() { }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            (byte[] Data, WebSocketMessageType Type, bool End) frame;
            try { frame = await _incoming.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely); }

            if (frame.Type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, null);
            }
            Assert.True(frame.Data.Length <= buffer.Count, "test frames must fit the receive buffer");
            frame.Data.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(frame.Data.Length, frame.Type, frame.End);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _sending);
            MaxConcurrentSends = Math.Max(MaxConcurrentSends, now);
            try
            {
                SendStarted.TrySetResult();
                if (StallSends)
                {
                    var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var _ = cancellationToken.Register(() => never.TrySetCanceled(cancellationToken));
                    await never.Task;
                }
                await Task.Yield();   // give any overlapping send a chance to show itself
                Sent.Writer.TryWrite(new Sent(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count)));
            }
            finally
            {
                Interlocked.Decrement(ref _sending);
            }
        }
    }
}
