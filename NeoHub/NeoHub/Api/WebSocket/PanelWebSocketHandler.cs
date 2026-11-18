using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DSC.TLink.ITv2.MediatR;
using NeoHub.Api.WebSocket.Models;
using NeoHub.Services;
using NeoHub.Services.PanelConfiguration.Sections;

namespace NeoHub.Api.WebSocket
{
    /// <summary>
    /// Manages WebSocket connections for Home Assistant / Homebridge integration.
    /// Pushes partition and zone updates in real-time.
    ///
    /// Concurrency: state-change events arrive on several threads. Each event is serialized
    /// once, synchronously, and queued to every client's <see cref="WebSocketClient"/>, whose
    /// single send loop writes them in order. Nothing here calls WebSocket.SendAsync directly.
    /// </summary>
    public class PanelWebSocketHandler
    {
        /// <summary>Largest client message accepted; requests are a few hundred bytes.</summary>
        internal const int MaxInboundMessageBytes = 64 * 1024;

        private readonly IPanelStateService _panelState;
        private readonly IPanelCommandService _commandService;
        private readonly IITv2SessionManager _sessionManager;
        private readonly ISessionMonitor _sessionMonitor;
        private readonly ILogger<PanelWebSocketHandler> _logger;
        private readonly ConcurrentDictionary<string, WebSocketClient> _clients = new();

        public PanelWebSocketHandler(
            IPanelStateService panelState,
            IPanelCommandService commandService,
            IITv2SessionManager sessionManager,
            ISessionMonitor sessionMonitor,
            ILogger<PanelWebSocketHandler> logger)
        {
            _panelState = panelState;
            _commandService = commandService;
            _sessionManager = sessionManager;
            _sessionMonitor = sessionMonitor;
            _logger = logger;

            _logger.LogInformation("PanelWebSocketHandler initialized. Subscribed to state change events.");

            _sessionMonitor.SessionsChanged += OnSessionsChanged;
            _panelState.PartitionStateChanged += OnPartitionChanged;
            _panelState.ZoneStateChanged += OnZoneChanged;
            _panelState.ConfigurationComplete += OnConfigurationComplete;
        }

        internal int ClientCount => _clients.Count;

        public async Task HandleConnectionAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                _logger.LogWarning("Rejected non-WebSocket request to /api/ws from {RemoteIp}", 
                    context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await RunClientAsync(webSocket, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);
        }

        /// <summary>
        /// Serves one accepted socket until it closes. Separate from the HTTP plumbing so it can
        /// be driven directly in tests.
        /// </summary>
        internal async Task RunClientAsync(System.Net.WebSockets.WebSocket webSocket, string? remoteIp, CancellationToken ct)
        {
            var clientId = Guid.NewGuid().ToString("N")[..8];
            var client = new WebSocketClient(clientId, webSocket, _logger);
            _clients[clientId] = client;

            _logger.LogInformation("WebSocket client {ClientId} connected from {RemoteIp}. Total clients: {Count}",
                clientId, remoteIp, _clients.Count);

            try
            {
                await ReceiveMessagesAsync(client, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebSocket client {ClientId} error", clientId);
            }
            finally
            {
                // Remove this client (not an arbitrary one), then drain and close it.
                _clients.TryRemove(new KeyValuePair<string, WebSocketClient>(clientId, client));
                await client.DisposeAsync();
                _logger.LogInformation("WebSocket client {ClientId} disconnected. Total clients: {Count}",
                    clientId, _clients.Count);
            }
        }

        private async Task ReceiveMessagesAsync(WebSocketClient client, CancellationToken ct)
        {
            var webSocket = client.Socket;
            var buffer = new byte[4096];
            using var message = new MemoryStream();

            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // The close reply is sent by WebSocketClient.DisposeAsync after its send
                        // loop has finished, so it never overlaps a queued send.
                        _logger.LogDebug("Client {ClientId} sent close frame", client.Id);
                        break;
                    }

                    // Messages may arrive in several frames; assemble before parsing.
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxInboundMessageBytes)
                    {
                        _logger.LogWarning("Client {ClientId} sent a message over {Max} bytes; disconnecting it",
                            client.Id, MaxInboundMessageBytes);
                        webSocket.Abort();
                        break;
                    }

                    if (!result.EndOfMessage)
                        continue;

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                        _logger.LogTrace("Client {ClientId} ← {Json}", client.Id, json);
                        await ProcessMessageAsync(client, json);
                    }
                    message.SetLength(0);
                }
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                // Client disconnected abruptly (network drop, browser close, etc.)
                // This is normal behavior, not an error
                _logger.LogDebug("Client {ClientId} disconnected without close handshake", client.Id);
            }
            catch (WebSocketException) when (client.IsAborted)
            {
                // We aborted it (too slow, or a send failed); already logged.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host shutting down or request aborted.
            }
        }

        private async Task ProcessMessageAsync(WebSocketClient client, string json)
        {
            var clientId = client.Id;
            try
            {
                var message = JsonSerializer.Deserialize<WebSocketMessage>(json, _jsonOptions);

                if (message == null)
                {
                    _logger.LogWarning("Client {ClientId} sent null message", clientId);
                    SendError(client, "Invalid message");
                    return;
                }

                _logger.LogDebug("Client {ClientId} request: {MessageType}", clientId, message.GetType().Name);

                switch (message)
                {
                    case GetFullStateMessage:
                        Send(client, BuildFullState(clientId));
                        break;

                    case ArmAwayMessage armAway:
                        await HandleArmCommandAsync(client, armAway, DSC.TLink.ITv2.Enumerations.ArmingMode.AwayArm);
                        break;

                    case ArmHomeMessage armHome:
                        await HandleArmCommandAsync(client, armHome, DSC.TLink.ITv2.Enumerations.ArmingMode.StayArm);
                        break;

                    case ArmNightMessage armNight:
                        await HandleArmCommandAsync(client, armNight, DSC.TLink.ITv2.Enumerations.ArmingMode.NightArm);
                        break;

                    case DisarmMessage disarm:
                        await HandleDisarmCommandAsync(client, disarm);
                        break;

                    case BypassZoneMessage bypassZone:
                        await HandleBypassZoneCommandAsync(client, bypassZone);
                        break;

                    default:
                        _logger.LogWarning("Client {ClientId} sent unknown message type: {MessageType}", clientId, message.GetType().Name);
                        SendError(client, $"Unknown message type: {message.GetType().Name}");
                        break;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Client {ClientId} sent invalid JSON: {Json}", clientId, json);
                SendError(client, $"Invalid JSON: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message from client {ClientId}: {Json}", clientId, json);
                SendError(client, $"Processing error: {ex.Message}");
            }
        }

        private FullStateMessage BuildFullState(string clientId)
        {
            var sessions = _sessionManager.GetActiveSessions()
                .Where(sessionId => !string.IsNullOrWhiteSpace(sessionId))
                .Select(sessionId =>
                {
                    var partitions = _panelState.GetPartitions(sessionId);
                    var zones = _panelState.GetZones(sessionId);

                    return new SessionDto
                    {
                        SessionId = sessionId,
                        Name = sessionId,
                        Partitions = partitions
                            .Select(kvp => new PartitionDto
                            {
                                PartitionNumber = kvp.Key,
                                Name = kvp.Value.DisplayName,
                                Status = kvp.Value.EffectiveStatus
                            })
                            .ToList(),
                        Zones = zones
                            .Select(kvp => new ZoneDto
                            {
                                ZoneNumber = kvp.Key,
                                Name = kvp.Value.DisplayName,
                                DeviceClass = DetermineDeviceClass(kvp.Value),
                                Open = kvp.Value.IsOpen,
                                Bypassed = kvp.Value.IsBypassed,
                                // Copy: the DTO is serialized later, the list is shared state.
                                Partitions = kvp.Value.Partitions.ToList()
                            })
                            .ToList()
                    };
                })
                .ToList();

            _logger.LogDebug(
                "Client {ClientId} → full_state: {SessionCount} sessions, {PartitionCount} partitions, {ZoneCount} zones",
                clientId,
                sessions.Count,
                sessions.Sum(s => s.Partitions.Count),
                sessions.Sum(s => s.Zones.Count));

            return new FullStateMessage { Sessions = sessions };
        }

        private async Task HandleArmCommandAsync(
            WebSocketClient client,
            ArmCommandMessage command, 
            DSC.TLink.ITv2.Enumerations.ArmingMode mode)
        {
            _logger.LogInformation("Client {ClientId} → {Command}: Session={SessionId}, Partition={Partition}",
                client.Id, command.GetType().Name, command.SessionId, command.PartitionNumber);

            var result = await _commandService.ArmAsync(command.SessionId, command.PartitionNumber, mode, command.Code);

            if (!result.Success)
                SendError(client, result.ErrorMessage ?? "Command failed");
        }

        private async Task HandleDisarmCommandAsync(WebSocketClient client, DisarmMessage command)
        {
            _logger.LogInformation("Client {ClientId} → {Command}: Session={SessionId}, Partition={Partition}",
                client.Id, command.GetType().Name, command.SessionId, command.PartitionNumber);

            var result = await _commandService.DisarmAsync(command.SessionId, command.PartitionNumber, command.Code);

            if (!result.Success)
                SendError(client, result.ErrorMessage ?? "Command failed");
        }

        private async Task HandleBypassZoneCommandAsync(WebSocketClient client, BypassZoneMessage command)
        {
            _logger.LogInformation(
                "Client {ClientId} → bypass_zone: Session={SessionId}, Partition={Partition}, Zone={Zone}, Bypass={Bypass}",
                client.Id, command.SessionId, command.PartitionNumber, command.ZoneNumber, command.Bypass);

            var result = await _commandService.BypassZoneAsync(
                command.SessionId, command.PartitionNumber, command.ZoneNumber, command.Bypass, command.Code);

            if (!result.Success)
                SendError(client, result.ErrorMessage ?? "Bypass command failed");
        }

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
        };

        /// <summary>Serializes as the base type so the [JsonPolymorphic] discriminator is emitted.</summary>
        private static byte[] Serialize(WebSocketMessage message)
            => JsonSerializer.SerializeToUtf8Bytes<WebSocketMessage>(message, _jsonOptions);

        private void Send(WebSocketClient client, WebSocketMessage message)
        {
            var bytes = Serialize(message);
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Client {ClientId} → {MessageType}: {Json}", client.Id, message.GetType().Name, Encoding.UTF8.GetString(bytes));
            client.TryEnqueue(bytes);
        }

        private void SendError(WebSocketClient client, string errorMessage)
        {
            _logger.LogWarning("Client {ClientId} → error: {Message}", client.Id, errorMessage);
            Send(client, new ErrorMessage { Message = errorMessage });
        }

        #region Event Handlers (broadcast to all clients)

        // Each handler builds and serializes its message synchronously, on the thread that raised
        // the event, so every client's queue sees events in the order they were applied.

        private void OnSessionsChanged()
        {
            _logger.LogDebug("Session list changed, broadcasting full_state to {Count} clients", _clients.Count);
            BroadcastFullState();
        }

        private void OnConfigurationComplete(object? sender, ConfigurationCompleteEventArgs e)
        {
            _logger.LogInformation("Configuration complete for session {SessionId}, broadcasting full_state to {Count} clients",
                e.SessionId, _clients.Count);
            BroadcastFullState();
        }

        private void OnPartitionChanged(object? sender, PartitionStateChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.SessionId))
            {
                _logger.LogWarning("Ignoring partition update with empty sessionId");
                return;
            }

            _logger.LogDebug("Broadcasting partition_update: Session={SessionId}, Partition={Partition}, Status={Status}",
                e.SessionId, e.Partition.PartitionNumber, e.Partition.EffectiveStatus);

            Broadcast(new PartitionUpdateMessage
            {
                SessionId = e.SessionId,
                PartitionNumber = e.Partition.PartitionNumber,
                Status = e.Partition.EffectiveStatus
            });
        }

        private void OnZoneChanged(object? sender, ZoneStateChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.SessionId))
            {
                _logger.LogWarning("Ignoring zone update with empty sessionId");
                return;
            }

            _logger.LogDebug("Broadcasting zone_update: Session={SessionId}, Zone={Zone}, Open={Open}, Bypassed={Bypassed}",
                e.SessionId, e.Zone.ZoneNumber, e.Zone.IsOpen, e.Zone.IsBypassed);

            Broadcast(new ZoneUpdateMessage
            {
                SessionId = e.SessionId,
                ZoneNumber = e.Zone.ZoneNumber,
                Open = e.Zone.IsOpen,
                Bypassed = e.Zone.IsBypassed
            });
        }

        private void BroadcastFullState()
        {
            if (_clients.IsEmpty)
                return;
            Broadcast(BuildFullState("broadcast"));
        }

        private void Broadcast(WebSocketMessage message)
        {
            try
            {
                var bytes = Serialize(message);
                _logger.LogTrace("Broadcasting {MessageType} to {Count} clients", message.GetType().Name, _clients.Count);
                foreach (var client in _clients.Values)
                    client.TryEnqueue(bytes);
            }
            catch (Exception ex)
            {
                // Event handlers run on the panel's notification path; never let one throw into it.
                _logger.LogError(ex, "Error broadcasting {MessageType}", message.GetType().Name);
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Maps the zone's panel definition to a Home Assistant binary_sensor device class.
        /// The definition says how the panel treats the zone, not what hardware is wired to it,
        /// so this is a best guess. Without the installer config (no definition) every zone is "door".
        /// </summary>
        private static string DetermineDeviceClass(Services.Models.ZoneState zone)
        {
            return zone.Definition switch
            {
                ZoneDefinition.Interior or
                ZoneDefinition.InteriorStayAway or
                ZoneDefinition.InteriorDelay or
                ZoneDefinition.NightZone => "motion",

                ZoneDefinition.Delayed24HourFire or
                ZoneDefinition.Standard24HourFire or
                ZoneDefinition.AutoVerifiedFire => "smoke",

                ZoneDefinition.Heat24Hour or
                ZoneDefinition.HighTemperature24Hour => "heat",
                ZoneDefinition.LowTemperature24Hour => "cold",
                ZoneDefinition.CO24Hour => "carbon_monoxide",
                ZoneDefinition.Gas24Hour => "gas",
                ZoneDefinition.Flood24Hour => "moisture",

                ZoneDefinition.Holdup24Hour or
                ZoneDefinition.Panic24Hour or
                ZoneDefinition.Medical24Hour or
                ZoneDefinition.Emergency24Hour or
                ZoneDefinition.Sprinkler24Hour => "safety",

                ZoneDefinition.LatchingTamper24Hour or
                ZoneDefinition.NonLatchingTamper24Hour => "tamper",

                ZoneDefinition.Supervisory24Hour or
                ZoneDefinition.SupervisoryBuzzer24Hour or
                ZoneDefinition.FireSupervisory => "problem",

                _ => "door"
            };
        }

        #endregion
    }
}
