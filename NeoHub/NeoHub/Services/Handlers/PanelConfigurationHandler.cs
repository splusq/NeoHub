using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Sessions;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Options;
using NeoHub.Domain;
using NeoHub.Services.Models;
using NeoHub.Services.PanelConfiguration;
using NeoHub.Services.Settings;

namespace NeoHub.Services.Handlers
{
    /// <summary>
    /// Triggered when a panel session connects. Pulls system capabilities,
    /// then either reads full installer config (if installer code is available)
    /// or falls back to explicit label pulls. Always reads partition/zone status.
    /// </summary>
    public class PanelConfigurationHandler : IPanelLifecycleObserver
    {
        private const int MaxLabelsPerRequest = 4;

        private readonly IPanelSender _mediator;
        private readonly IPanelStateService _panelState;
        private readonly IPanelConfigurationService _configService;
        private readonly IOptionsMonitor<PanelConnectionsSettings> _connectionSettings;
        private readonly ILogger<PanelConfigurationHandler> _logger;
        private readonly TimeProvider _time;
        private readonly IPanelOperations _operations;

        public PanelConfigurationHandler(
            IPanelSender mediator,
            IPanelStateService panelState,
            IPanelConfigurationService configService,
            IOptionsMonitor<PanelConnectionsSettings> connectionSettings,
            ILogger<PanelConfigurationHandler> logger,
            TimeProvider time,
            IPanelOperations operations)
        {
            _time = time;
            _operations = operations;
            _mediator = mediator;
            _panelState = panelState;
            _configService = configService;
            _connectionSettings = connectionSettings;
            _logger = logger;
        }

        /// <summary>
        /// Starts the pull and returns at once, so a long pull for one panel never holds up
        /// lifecycle events for others. The pull is one queued operation for its panel, stops when
        /// its connection ends, and its state updates are scoped to that connection.
        /// </summary>
        public Task OnLifecycleAsync(PanelLifecycleEvent e, CancellationToken cancellationToken)
        {
            if (e is SessionConnectedNotification connected)
                _ = PullInBackgroundAsync(connected, cancellationToken);
            return Task.CompletedTask;
        }

        private async Task PullInBackgroundAsync(SessionConnectedNotification connected, CancellationToken cancellationToken)
        {
            try
            {
                await Handle(connected, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Panel configuration pull failed");
            }
        }

        public async Task Handle(SessionConnectedNotification notification, CancellationToken cancellationToken)
        {
            var sessionId = notification.SessionId;
            // Every state change from this pull is scoped to this connection: if the panel
            // reconnects meanwhile, the store ignores the rest of this pull.
            Guid? connection = notification.ConnectionId == Guid.Empty ? null : notification.ConnectionId;
            _logger.LogInformation("Starting panel configuration pull");

            // The connection posted "started" before it was registered, so once this is applied
            // the session exists (unless the connection has already gone).
            await _panelState.DispatchAsync(new ConnectionPhaseChanged(sessionId, connection, ConnectionPhase.Handshake));
            var session = _panelState.GetSession(sessionId);
            if (session is null || (connection is { } c && session.ConnectionId != c))
            {
                _logger.LogInformation("Connection already replaced or gone; skipping configuration pull");
                return;
            }

            // The whole pull is one panel operation: nothing else enters programming mode until it
            // is done. It stops if this connection ends (including being replaced).
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, notification.ConnectionLifetime);
            var completed = false;
            try
            {
                completed = await _operations.RunAsync(sessionId, "configuration pull",
                    ct => PullAsync(sessionId, connection, ct), stop.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Panel configuration pull stopped: its connection ended or the host is stopping");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during panel configuration pull");
            }
            finally
            {
                await _panelState.DispatchAsync(new InitialPullFinished(sessionId, connection, completed));
            }
        }

        private async Task<bool> PullAsync(string sessionId, Guid? connection, CancellationToken cancellationToken)
        {
            {
                // ── 1. Capabilities ──
                var capabilities = await RequestAsync<ConnectionSystemCapabilities>(
                    sessionId,
                    new CommandRequestMessage { Request = new ConnectionSystemCapabilities() },
                    cancellationToken);

                if (capabilities == null)
                {
                    _logger.LogWarning("Failed to get system capabilities, aborting config pull");
                    return false;
                }

                _logger.LogInformation(
                    "Panel capabilities: {MaxZones} zones, {MaxPartitions} partitions, {MaxUsers} users",
                    capabilities.MaxZones, capabilities.MaxPartitions, capabilities.MaxUsers);

                await _panelState.DispatchAsync(new CapabilitiesRead(sessionId, connection,
                    capabilities.MaxZones, capabilities.MaxPartitions, capabilities.MaxUsers));

                var connectionSettings = _connectionSettings.CurrentValue.FindBySessionId(sessionId);
                var maxZonesSetting = connectionSettings?.MaxZones ?? 0;
                var effectiveZones = maxZonesSetting > 0
                    ? Math.Min(capabilities.MaxZones, maxZonesSetting)
                    : capabilities.MaxZones;

                _logger.LogInformation(
                    "Using {EffectiveZones} zones (panel max: {PanelMax}, setting: {Setting})",
                    effectiveZones, capabilities.MaxZones, maxZonesSetting > 0 ? maxZonesSetting : "unlimited");

                // ── 2. Labels: installer config vs. explicit pull ──
                await _panelState.DispatchAsync(new ConnectionPhaseChanged(sessionId, connection, ConnectionPhase.ReadingConfig));
                var installerCode = connectionSettings?.InstallerCode;
                if (!string.IsNullOrEmpty(installerCode))
                {
                    _logger.LogInformation("Installer code configured, reading panel configuration sections");
                    var result = await _configService.RunInConfigModeAsync(
                        sessionId, installerCode, readWrite: false,
                        async () =>
                        {
                            await _configService.ReadSectionsAsync(
                                sessionId, CreateSendDelegate(sessionId), cancellationToken);
                            return new SectionResult(true);
                        }, cancellationToken);

                    if (result.Success && _panelState.GetSession(sessionId)?.Configuration is { } config)
                    {
                        var (partitions, zones) = PanelReadings.Setup(config, effectiveZones, capabilities.MaxPartitions);
                        await _panelState.DispatchAsync(new InstallerSetupApplied(sessionId, connection, partitions, zones));
                        _logger.LogInformation("Applied installer config to session state: {Zones} zones, {Partitions} partitions",
                            effectiveZones, capabilities.MaxPartitions);
                    }
                    else if (!result.Success)
                    {
                        _logger.LogWarning("Installer config read failed: {Error}", result.ErrorMessage);
                    }
                }

                // Fall back to explicit label pull if no config was read
                if (_panelState.GetSession(sessionId)?.Configuration is null)
                {
                    await PullZoneLabelsAsync(sessionId, effectiveZones, cancellationToken);
                    await PullPartitionLabelsAsync(sessionId, capabilities.MaxPartitions, cancellationToken);
                }

                // ── 3. Status reads (always) ──
                await _panelState.DispatchAsync(new ConnectionPhaseChanged(sessionId, connection, ConnectionPhase.ReadingStatus));
                await PullPartitionStatusAsync(sessionId, connection, capabilities.MaxPartitions, cancellationToken);
                await PullZoneStatusAsync(sessionId, connection, effectiveZones, cancellationToken);

                _logger.LogInformation("Panel configuration pull complete");
                return true;
            }
        }

        private SendSectionRead CreateSendDelegate(string sessionId) =>
            async (request, token) =>
            {
                var response = await _mediator.Send(new SessionCommand
                {
                    SessionID = sessionId,
                    MessageData = request
                }, token);
                return response.Success ? response.MessageData as SectionReadResponse : null;
            };

        private async Task PullZoneLabelsAsync(string sessionId, int maxZones, CancellationToken ct)
        {
            for (int start = 1; start <= maxZones; start += MaxLabelsPerRequest)
            {
                int end = Math.Min(start + MaxLabelsPerRequest - 1, maxZones);

                var response = await RequestAsync<NotificationLabelText>(
                    sessionId,
                    new CommandRequestMessage
                    {
                        Request = new NotificationLabelText { Collection = NotificationLabelText.LabelCollection.Zone, Start = start, End = end }
                    },
                    ct);

                if (response == null)
                {
                    _logger.LogWarning("Failed to get zone labels {Start}-{End}",
                        start, end);
                }
            }

            _logger.LogInformation("Pulled zone labels");
        }

        private async Task PullPartitionLabelsAsync(string sessionId, int maxPartitions, CancellationToken ct)
        {
            for (int start = 1; start <= maxPartitions; start += MaxLabelsPerRequest)
            {
                int end = Math.Min(start + MaxLabelsPerRequest - 1, maxPartitions);

                var response = await RequestAsync<NotificationLabelText>(
                    sessionId,
                    new CommandRequestMessage
                    {
                        Request = new NotificationLabelText { Collection = NotificationLabelText.LabelCollection.Partition, Start = start, End = end }
                    },
                    ct);

                if (response == null)
                {
                    _logger.LogWarning("Failed to get partition labels {Start}-{End}",
                        start, end);
                }
            }

            _logger.LogInformation("Pulled partition labels");
        }

        private async Task PullPartitionStatusAsync(string sessionId, Guid? connection, int maxPartitions, CancellationToken ct)
        {
            var partEnables = _panelState.GetSession(sessionId)?.Configuration?.PartitionEnables.Values;

            for (int partition = 1; partition <= maxPartitions; partition++)
            {
                // Skip disabled partitions when config data is available
                if (partEnables is not null
                    && partition - 1 < partEnables.Count
                    && partEnables[partition - 1] != PanelConfiguration.Sections.PartitionEnable.Enabled)
                    continue;

                // Taken before the request: a live change after this point is newer than the reply.
                var requestedAt = _panelState.Version;
                var response = await RequestAsync<ModulePartitionStatus>(
                    sessionId,
                    new CommandRequestMessage
                    {
                        Request = new ModulePartitionStatus { Partition = partition }
                    },
                    ct);

                if (response == null)
                {
                    _logger.LogWarning("Failed to get status for partition {Partition}", partition);
                    continue;
                }

                var reading = PanelReadings.Partition(response);
                await _panelState.DispatchAsync(new PartitionStatusRead(sessionId, connection, reading, requestedAt, _time.GetUtcNow()));
                _logger.LogDebug("Partition {Partition} Status1=0x{S1:X} Status2=0x{S2:X} → {Status}, IsReady={IsReady}",
                    reading.Partition, (byte)response.Status1, (byte)response.Status2, reading.Status, reading.IsReady);
            }

            _logger.LogInformation("Pulled initial partition status");
        }

        private async Task PullZoneStatusAsync(string sessionId, Guid? connection, int maxZones, CancellationToken ct)
        {
            var zoneDefs = _panelState.GetSession(sessionId)?.Configuration?.ZoneDefinitions.Values;

            var requestedAt = _panelState.Version;
            var response = await RequestAsync<ModuleZoneStatus>(
                sessionId,
                new CommandRequestMessage
                {
                    Request = new ModuleZoneStatus { ZoneStart = 1, ZoneCount = maxZones }
                },
                ct);

            if (response == null)
            {
                _logger.LogWarning("Failed to get zone status");
                return;
            }

            await _panelState.DispatchAsync(new ZoneStatusRead(sessionId, connection,
                PanelReadings.Zones(response, zoneDefs), requestedAt, _time.GetUtcNow()));
            _logger.LogInformation("Pulled initial status for {Count} zones", response.ZoneStatusBytes.Length);
        }

        private async Task<T?> RequestAsync<T>(string sessionId, IMessageData request, CancellationToken ct)
            where T : class, IMessageData
        {
            var response = await _mediator.Send(new SessionCommand
            {
                SessionID = sessionId,
                MessageData = request
            }, ct);

            if (!response.Success)
            {
                _logger.LogWarning("Command {Command} failed: {Error}",
                    typeof(T).Name, response.ErrorMessage);
                return null;
            }

            return response.MessageData as T;
        }
    }
}
