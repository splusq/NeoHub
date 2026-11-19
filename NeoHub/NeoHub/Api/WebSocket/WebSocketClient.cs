using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using NeoHub.Api.WebSocket.Models;

namespace NeoHub.Api.WebSocket
{
    /// <summary>
    /// One connected client's outgoing side: a bounded outbox and a single send loop, so sends
    /// never overlap and arrive in the order they were posted. A client that can't keep up (outbox
    /// full) or whose send stalls past <see cref="SendTimeout"/> is aborted; it never slows anyone else.
    /// </summary>
    public sealed class WebSocketClient
    {
        public const int OutboxCapacity = 256;
        public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        };

        private readonly System.Net.WebSockets.WebSocket _socket;
        private readonly TimeProvider _time;
        private readonly ILogger _logger;
        private readonly Channel<WebSocketMessage> _outbox = Channel.CreateBounded<WebSocketMessage>(
            new BoundedChannelOptions(OutboxCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        public WebSocketClient(string id, System.Net.WebSockets.WebSocket socket, TimeProvider time, ILogger logger)
        {
            Id = id;
            _socket = socket;
            _time = time;
            _logger = logger;
            Completion = SendLoopAsync();
        }

        public string Id { get; }

        /// <summary>Completes when the send loop has stopped (closed, aborted, or failed).</summary>
        public Task Completion { get; }

        /// <summary>Queues a message. Returns false (and aborts the client) if it has fallen too far behind.</summary>
        public bool Post(WebSocketMessage message)
        {
            if (_outbox.Writer.TryWrite(message))
                return true;
            if (_outbox.Writer.TryComplete())
            {
                _logger.LogWarning("WebSocket client {ClientId} fell {Capacity} messages behind; disconnecting it", Id, OutboxCapacity);
                _socket.Abort();
            }
            return false;
        }

        /// <summary>Stops accepting messages; the loop sends what is queued and stops.</summary>
        public void Complete() => _outbox.Writer.TryComplete();

        private async Task SendLoopAsync()
        {
            await Task.Yield();
            try
            {
                await foreach (var message in _outbox.Reader.ReadAllAsync())
                {
                    if (_socket.State != WebSocketState.Open)
                        break;
                    // Serialize as the base type so the [JsonPolymorphic] discriminator is emitted
                    var bytes = JsonSerializer.SerializeToUtf8Bytes<WebSocketMessage>(message, JsonOptions);
                    _logger.LogTrace("Client {ClientId} → {MessageType}", Id, message.GetType().Name);
                    using var stall = new CancellationTokenSource(SendTimeout, _time);
                    await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, stall.Token);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("WebSocket client {ClientId} stalled for {Timeout}; disconnecting it", Id, SendTimeout);
                _socket.Abort();
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException)
            {
                _logger.LogDebug("WebSocket client {ClientId} send failed: {Error}", Id, ex.Message);
                _socket.Abort();
            }
            finally
            {
                _outbox.Writer.TryComplete();
            }
        }
    }
}
