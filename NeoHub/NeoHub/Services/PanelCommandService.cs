using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Messages;
using MediatR;
using Microsoft.Extensions.Options;
using NeoHub.Services.Settings;

namespace NeoHub.Services
{
    public class PanelCommandService : IPanelCommandService
    {
        private readonly IMediator _mediator;
        private readonly ILogger<PanelCommandService> _logger;
        private readonly IOptionsMonitor<PanelConnectionsSettings> _connectionSettings;
        private readonly IPanelStateService _panelState;

        /// <summary>
        /// How long a bypass waits for another programming-mode operation (the connect-time
        /// configuration pull, a user-code change) to finish before giving up.
        /// </summary>
        internal static readonly TimeSpan ProgrammingLockTimeout = TimeSpan.FromSeconds(30);

        public PanelCommandService(
            IMediator mediator, 
            ILogger<PanelCommandService> logger,
            IOptionsMonitor<PanelConnectionsSettings> connectionSettings,
            IPanelStateService panelState)
        {
            _mediator = mediator;
            _logger = logger;
            _connectionSettings = connectionSettings;
            _panelState = panelState;
        }

        private ConnectionSettings? GetConnection(string sessionId) =>
            _connectionSettings.CurrentValue.FindBySessionId(sessionId);

        public async Task<PanelCommandResult> ArmAsync(string sessionId, byte partition, ArmingMode mode, string? accessCode = null)
        {
            var conn = GetConnection(sessionId);
            var code = accessCode ?? conn?.DefaultAccessCode ?? string.Empty;

            _logger.LogInformation(
                "Arm command: Partition={Partition}, Mode={Mode}, UsingDefaultCode={UsingDefault}",
                partition, mode, string.IsNullOrEmpty(accessCode) && !string.IsNullOrEmpty(conn?.DefaultAccessCode));

            var message = new PartitionArm
            {
                Partition = partition,
                ArmMode = mode,
                AccessCode = code
            };

            return await SendCommandAsync(sessionId, message);
        }

        public async Task<PanelCommandResult> DisarmAsync(string sessionId, byte partition, string? accessCode = null)
        {
            var conn = GetConnection(sessionId);
            var code = accessCode ?? conn?.DefaultAccessCode;

            if (string.IsNullOrEmpty(code))
            {
                return PanelCommandResult.Error("Access code is required to disarm");
            }

            _logger.LogInformation(
                "Disarm command: Partition={Partition}, UsingDefaultCode={UsingDefault}",
                partition, string.IsNullOrEmpty(accessCode) && !string.IsNullOrEmpty(conn?.DefaultAccessCode));

            var message = new PartitionDisarm
            {
                Partition = partition,
                AccessCode = code
            };

            return await SendCommandAsync(sessionId, message);
        }

        public async Task<PanelCommandResult> BypassZoneAsync(string sessionId, byte partition, byte zoneNumber, bool bypass, string? accessCode = null)
        {
            var conn = GetConnection(sessionId);
            var code = accessCode ?? conn?.DefaultAccessCode;

            if (string.IsNullOrEmpty(code))
                return PanelCommandResult.Error("Access code is required to bypass zones");

            _logger.LogInformation(
                "Bypass command: Partition={Partition}, Zone={Zone}, Bypass={Bypass}, UsingDefaultCode={UsingDefault}",
                partition, zoneNumber, bypass,
                string.IsNullOrEmpty(accessCode) && !string.IsNullOrEmpty(conn?.DefaultAccessCode));

            // Enter → write → exit must not interleave with another programming-mode sequence
            // (the configuration pull or a user-code change) on the same panel.
            var programmingLock = _panelState.GetConfigLock(sessionId);
            if (!await programmingLock.WaitAsync(ProgrammingLockTimeout))
            {
                _logger.LogWarning("Bypass failed: the panel stayed busy with another programming operation for {Timeout}",
                    ProgrammingLockTimeout);
                return PanelCommandResult.Error("The panel is busy with another programming operation; try again shortly");
            }

            try
            {
                return await BypassInProgrammingModeAsync(sessionId, partition, zoneNumber, bypass, code);
            }
            finally
            {
                programmingLock.Release();
            }
        }

        private async Task<PanelCommandResult> BypassInProgrammingModeAsync(
            string sessionId, byte partition, byte zoneNumber, bool bypass, string code)
        {
            var enterResult = await SendCommandAsync(sessionId, new ConfigurationEnter
            {
                Partition = partition,
                ProgrammingMode = ProgrammingMode.UserBypassProgramming,
                AccessCode = code,
                ReadWrite = ConfigurationEnter.ReadWriteAccessEnum.ReadWriteMode
            });

            if (!enterResult.Success)
            {
                _logger.LogWarning("Bypass failed: could not enter config mode. {Error}", enterResult.ErrorMessage);
                return enterResult;
            }

            PanelCommandResult bypassResult;
            try
            {
                bypassResult = await SendCommandAsync(sessionId, new SingleZoneBypassWrite
                {
                    Partition = partition,
                    ZoneNumber = zoneNumber,
                    BypassState = bypass ? BypassStatusEnum.Bypassed : BypassStatusEnum.NotBypassed,
                });
            }
            finally
            {
                var exitResult = await SendCommandAsync(sessionId, new ConfigurationExit { Partition = partition });
                if (!exitResult.Success)
                    _logger.LogWarning("Failed to exit config mode after bypass. {Error}", exitResult.ErrorMessage);
            }

            return bypassResult;
        }
        private async Task<PanelCommandResult> SendCommandAsync(string sessionId, IMessageData message)
        {
            try
            {
                SessionResponse response = await _mediator.Send(new SessionCommand
                {
                    SessionID = sessionId,
                    MessageData = message
                });

                if (response.Success)
                    return PanelCommandResult.Ok();

                _logger.LogWarning("Command failed: [{Code}] {Error}", response.ErrorCode, response.ErrorMessage);

                return response.ErrorCode.HasValue
                    ? PanelCommandResult.Error(response.ErrorCode.Value, response.ErrorMessage ?? response.ErrorCode.Value.ToString())
                    : PanelCommandResult.Error(response.ErrorMessage ?? "Unknown error");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending command");
                return PanelCommandResult.Error(ex.Message);
            }
        }
    }
}
