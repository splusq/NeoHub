using System.Net.WebSockets;
using System.Threading.Channels;

namespace NeoHub.Api.WebSocket
{
    /// <summary>
    /// One connected WebSocket client with its own outbound queue.
    ///
    /// A WebSocket allows only one SendAsync at a time, and broadcasts are raised from several
    /// threads (panel notifications, the configuration pull, session changes). Every outbound
    /// message goes through <see cref="TryEnqueue"/> and a single send loop writes them in
    /// order, so sends never overlap and a zone's open/close updates can't be reordered.
    ///
    /// A client that falls <see cref="Capacity"/> messages behind, or whose send stalls for
    /// <see cref="SendTimeout"/>, is aborted rather than allowed to block or drop updates
    /// silently; it reconnects and gets a fresh full_state.
    /// </summary>
    internal sealed class WebSocketClient : IAsyncDisposable
    {
        public const int Capacity = 512;
        public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

        private readonly Channel<ReadOnlyMemory<byte>> _outbox;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _abort = new();
        private readonly Task _sendLoop;
        private int _disposed;

        public WebSocketClient(string id, System.Net.WebSockets.WebSocket socket, ILogger logger)
        {
            Id = id;
            Socket = socket;
            _logger = logger;
            _outbox = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(Capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _sendLoop = Task.Run(SendLoopAsync);
        }

        public string Id { get; }
        public System.Net.WebSockets.WebSocket Socket { get; }

        /// <summary>Set once the client has been aborted for falling behind or a failed send.</summary>
        public bool IsAborted => _abort.IsCancellationRequested;

        /// <summary>
        /// Queues one UTF-8 text message. Never blocks. Returns false (and aborts the client)
        /// if the queue is full; returns false if the client is already closing.
        /// </summary>
        public bool TryEnqueue(ReadOnlyMemory<byte> utf8Message)
        {
            if (_outbox.Writer.TryWrite(utf8Message))
                return true;

            if (!IsAborted && Volatile.Read(ref _disposed) == 0)
            {
                _logger.LogWarning("WebSocket client {ClientId} is {Capacity} messages behind; disconnecting it", Id, Capacity);
                Abort();
            }
            return false;
        }

        private async Task SendLoopAsync()
        {
            try
            {
                await foreach (var message in _outbox.Reader.ReadAllAsync(_abort.Token))
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
                    timeout.CancelAfter(SendTimeout);
                    await Socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (_abort.IsCancellationRequested)
            {
                // Aborted: nothing more to send.
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("WebSocket client {ClientId} send stalled for {Timeout}; disconnecting it", Id, SendTimeout);
                Abort();
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "WebSocket client {ClientId} send failed; disconnecting it", Id);
                Abort();
            }
        }

        private void Abort()
        {
            _abort.Cancel();
            _outbox.Writer.TryComplete();
            // Abort (not Close) is safe to call concurrently with a pending send or receive,
            // and it unblocks the receive loop so the handler cleans up this client.
            Socket.Abort();
        }

        /// <summary>
        /// Stops accepting messages, lets queued ones go out (bounded), then closes the socket.
        /// The close happens only after the send loop has finished, since a close is itself a
        /// send and must not overlap one.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _outbox.Writer.TryComplete();
            if (await Task.WhenAny(_sendLoop, Task.Delay(DrainTimeout)) != _sendLoop)
            {
                Abort();
                await _sendLoop;
            }

            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var cts = new CancellationTokenSource(DrainTimeout);
                    await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", cts.Token);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "WebSocket client {ClientId} close failed", Id);
            }
        }
    }
}
