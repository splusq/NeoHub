using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using NeoHub.Api.WebSocket.Models;
using NeoHub.Services;

namespace NeoHub.Api.WebSocket
{
    /// <summary>
    /// The /api/ws endpoint (Home Assistant / Homebridge integration). Each connection gets a
    /// <see cref="WebSocketClient"/> registered with the <see cref="ClientHub"/>, which pushes
    /// state changes in order. This class only reads the client's requests and runs commands.
    /// </summary>
    public class PanelWebSocketHandler
    {
        /// <summary>Largest request accepted from a client (requests are tiny JSON objects).</summary>
        public const int MaxMessageBytes = 64 * 1024;

        private readonly ClientHub _hub;
        private readonly IPanelCommandService _commandService;
        private readonly TimeProvider _time;
        private readonly ILogger<PanelWebSocketHandler> _logger;

        public PanelWebSocketHandler(
            ClientHub hub,
            IPanelCommandService commandService,
            TimeProvider time,
            ILogger<PanelWebSocketHandler> logger)
        {
            _hub = hub;
            _commandService = commandService;
            _time = time;
            _logger = logger;
        }

        public async Task HandleConnectionAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                _logger.LogWarning("Rejected non-WebSocket request to /api/ws from {RemoteIp}",
                    context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            _logger.LogInformation("WebSocket connection from {RemoteIp}", context.Connection.RemoteIpAddress);
            await RunClientAsync(socket, context.RequestAborted);
        }

        /// <summary>Serves one connected socket until it closes.</summary>
        public async Task RunClientAsync(System.Net.WebSockets.WebSocket socket, CancellationToken ct)
        {
            var client = new WebSocketClient(Guid.NewGuid().ToString("N")[..8], socket, _time, _logger);
            _hub.Add(client);
            try
            {
                await ReceiveLoopAsync(socket, client, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is WebSocketException or IOException)
            {
                _logger.LogDebug("Client {ClientId} disconnected without close handshake: {Error}", client.Id, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebSocket client {ClientId} error", client.Id);
            }
            finally
            {
                _hub.Remove(client.Id);
                client.Complete();
                await client.Completion;
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(WebSocketClient.SendTimeout, _time);
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", timeout.Token);
                    }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
                    {
                        socket.Abort();
                    }
                }
            }
        }

        private async Task ReceiveLoopAsync(System.Net.WebSockets.WebSocket socket, WebSocketClient client, CancellationToken ct)
        {
            var buffer = new ArrayBufferWriter<byte>(4096);
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer.GetMemory(4096), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogDebug("Client {ClientId} sent close frame", client.Id);
                    return;
                }

                buffer.Advance(result.Count);
                if (buffer.WrittenCount > MaxMessageBytes)
                {
                    _logger.LogWarning("Client {ClientId} sent a message over {Max} bytes; closing", client.Id, MaxMessageBytes);
                    using var timeout = new CancellationTokenSource(WebSocketClient.SendTimeout, _time);
                    client.Complete();
                    await client.Completion;
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", timeout.Token);
                    return;
                }
                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType == WebSocketMessageType.Text)
                    await ProcessMessageAsync(client, buffer.WrittenSpan.ToArray());
                buffer.ResetWrittenCount();
            }
        }

        private async Task ProcessMessageAsync(WebSocketClient client, byte[] json)
        {
            WebSocketMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<WebSocketMessage>(json, WebSocketClient.JsonOptions);
            }
            catch (JsonException ex)
            {
                // Don't log the payload: it may carry an access code.
                SendError(client, $"Invalid JSON: {ex.Message}");
                return;
            }

            if (message is null)
            {
                SendError(client, "Invalid message");
                return;
            }

            _logger.LogDebug("Client {ClientId} request: {MessageType}", client.Id, message.GetType().Name);
            try
            {
                switch (message)
                {
                    case GetFullStateMessage:
                        // Through the hub, so it lands in order with the broadcasts.
                        _hub.RequestFullState(client.Id);
                        break;

                    case ArmAwayMessage armAway:
                        await ArmAsync(client, armAway, DSC.TLink.ITv2.Enumerations.ArmingMode.AwayArm);
                        break;

                    case ArmHomeMessage armHome:
                        await ArmAsync(client, armHome, DSC.TLink.ITv2.Enumerations.ArmingMode.StayArm);
                        break;

                    case ArmNightMessage armNight:
                        await ArmAsync(client, armNight, DSC.TLink.ITv2.Enumerations.ArmingMode.NightArm);
                        break;

                    case DisarmMessage disarm:
                        _logger.LogInformation("Client {ClientId} → disarm: Session={SessionId}, Partition={Partition}",
                            client.Id, disarm.SessionId, disarm.PartitionNumber);
                        Report(client, await _commandService.DisarmAsync(disarm.SessionId, disarm.PartitionNumber, disarm.Code), "Command failed");
                        break;

                    case BypassZoneMessage bypass:
                        _logger.LogInformation(
                            "Client {ClientId} → bypass_zone: Session={SessionId}, Partition={Partition}, Zone={Zone}, Bypass={Bypass}",
                            client.Id, bypass.SessionId, bypass.PartitionNumber, bypass.ZoneNumber, bypass.Bypass);
                        Report(client, await _commandService.BypassZoneAsync(
                            bypass.SessionId, bypass.PartitionNumber, bypass.ZoneNumber, bypass.Bypass, bypass.Code), "Bypass command failed");
                        break;

                    default:
                        SendError(client, $"Unknown message type: {message.GetType().Name}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing {MessageType} from client {ClientId}", message.GetType().Name, client.Id);
                SendError(client, $"Processing error: {ex.Message}");
            }
        }

        private async Task ArmAsync(WebSocketClient client, ArmCommandMessage command, DSC.TLink.ITv2.Enumerations.ArmingMode mode)
        {
            _logger.LogInformation("Client {ClientId} → {Command}: Session={SessionId}, Partition={Partition}",
                client.Id, command.GetType().Name, command.SessionId, command.PartitionNumber);
            Report(client, await _commandService.ArmAsync(command.SessionId, command.PartitionNumber, mode, command.Code), "Command failed");
        }

        private void Report(WebSocketClient client, PanelCommandResult result, string fallback)
        {
            if (!result.Success)
                SendError(client, result.ErrorMessage ?? fallback);
        }

        private void SendError(WebSocketClient client, string error)
        {
            _logger.LogWarning("Client {ClientId} → error: {Message}", client.Id, error);
            client.Post(new ErrorMessage { Message = error });
        }
    }
}
