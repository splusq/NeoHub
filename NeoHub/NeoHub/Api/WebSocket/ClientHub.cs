using System.Threading.Channels;
using DSC.TLink.ITv2.MediatR;
using NeoHub.Domain;
using NeoHub.Services;

namespace NeoHub.Api.WebSocket
{
    /// <summary>
    /// Owns the set of WebSocket clients. One loop takes, in order: clients joining and leaving,
    /// full-state requests, and every store change; it turns each into messages on the clients'
    /// outboxes. So every client sees changes in the order the store applied them, and a full
    /// state it asked for is placed correctly among them (anything after it arrives after it).
    /// </summary>
    public sealed class ClientHub : IAsyncDisposable
    {
        private abstract record Input;
        private sealed record Joined(WebSocketClient Client) : Input;
        private sealed record Left(string ClientId) : Input;
        private sealed record FullStateRequested(string ClientId) : Input;
        private sealed record Changed(PanelChange Change) : Input;
        private sealed record Quiesce(TaskCompletionSource Done) : Input;

        private readonly Channel<Input> _inbox = Channel.CreateUnbounded<Input>(new UnboundedChannelOptions { SingleReader = true });
        private readonly IPanelStateService _state;
        private readonly Func<IEnumerable<string>> _activeSessions;
        private readonly ILogger<ClientHub> _logger;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;
        private readonly Task _pump;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ClientHub(IPanelStateService state, IITv2SessionManager sessions, ILogger<ClientHub> logger)
            : this(state, sessions.GetActiveSessions, logger) { }

        internal ClientHub(IPanelStateService state, Func<IEnumerable<string>> activeSessions, ILogger<ClientHub> logger)
        {
            _state = state;
            _activeSessions = activeSessions;
            _logger = logger;
            _run = RunAsync();
            _pump = PumpAsync(_stop.Token);
        }

        /// <summary>Completes once the hub is subscribed to the store: every change after this reaches clients.</summary>
        internal Task Ready => _ready.Task;

        public void Add(WebSocketClient client) => _inbox.Writer.TryWrite(new Joined(client));
        public void Remove(string clientId) => _inbox.Writer.TryWrite(new Left(clientId));
        public void RequestFullState(string clientId) => _inbox.Writer.TryWrite(new FullStateRequested(clientId));

        /// <summary>Completes once everything posted before this call has been handled.</summary>
        internal Task QuiesceAsync()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inbox.Writer.TryWrite(new Quiesce(done)))
                done.TrySetResult();
            return done.Task;
        }

        // Copies store changes into the inbox (resubscribing if it falls behind).
        private async Task PumpAsync(CancellationToken stop)
        {
            await Task.Yield();
            while (!stop.IsCancellationRequested)
            {
                using var subscription = _state.Subscribe();
                try
                {
                    await foreach (var change in subscription.Changes.ReadAllAsync(stop))
                    {
                        // The first subscription's opening Resynchronize needs no broadcast: a client
                        // asks for full state when it connects. Later ones mean changes were missed.
                        if (change is Resynchronize && _ready.TrySetResult())
                            continue;
                        _inbox.Writer.TryWrite(new Changed(change));
                    }
                    return;
                }
                catch (SubscriberOverflowException) { /* the new subscription starts with Resynchronize */ }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            }
        }

        private async Task RunAsync()
        {
            await Task.Yield();
            var clients = new Dictionary<string, WebSocketClient>();
            await foreach (var input in _inbox.Reader.ReadAllAsync())
            {
                try
                {
                    Handle(input, clients);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "WebSocket hub failed handling {Input}", input.GetType().Name);
                }
            }
            foreach (var client in clients.Values)
                client.Complete();
        }

        private void Handle(Input input, Dictionary<string, WebSocketClient> clients)
        {
            switch (input)
            {
                case Joined joined:
                    clients[joined.Client.Id] = joined.Client;
                    _logger.LogInformation("WebSocket client {ClientId} joined. Total clients: {Count}", joined.Client.Id, clients.Count);
                    break;

                case Left left:
                    if (clients.Remove(left.ClientId, out var gone))
                    {
                        gone.Complete();
                        _logger.LogInformation("WebSocket client {ClientId} left. Total clients: {Count}", left.ClientId, clients.Count);
                    }
                    break;

                case FullStateRequested request:
                    if (clients.TryGetValue(request.ClientId, out var asker))
                        PostOrDrop(asker, WebSocketMessages.FullState(_state, _activeSessions()), clients);
                    break;

                case Quiesce quiesce:
                    quiesce.Done.TrySetResult();
                    break;

                case Changed changed:
                    switch (WebSocketMessages.For(changed.Change))
                    {
                        case WebSocketMessages.Broadcast.Send send:
                            Broadcast(send.Message, clients);
                            break;
                        case WebSocketMessages.Broadcast.FullState when clients.Count > 0:
                            Broadcast(WebSocketMessages.FullState(_state, _activeSessions()), clients);
                            break;
                    }
                    break;
            }
        }

        private void Broadcast(Models.WebSocketMessage message, Dictionary<string, WebSocketClient> clients)
        {
            foreach (var client in clients.Values.ToList())
                PostOrDrop(client, message, clients);
        }

        private static void PostOrDrop(WebSocketClient client, Models.WebSocketMessage message, Dictionary<string, WebSocketClient> clients)
        {
            if (!client.Post(message))
                clients.Remove(client.Id);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _pump;
            _inbox.Writer.TryComplete();
            await _run;
        }
    }
}
