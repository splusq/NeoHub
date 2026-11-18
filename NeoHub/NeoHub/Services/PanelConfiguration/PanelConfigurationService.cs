using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Sessions;
using DSC.TLink.ITv2.Messages;

namespace NeoHub.Services.PanelConfiguration;

public class PanelConfigurationService : IPanelConfigurationService
{
    private readonly IPanelSender _mediator;
    private readonly TimeProvider _time;
    private readonly IPanelStateService _panelState;
    private readonly ILogger<PanelConfigurationService> _logger;
    private readonly NeoHub.Domain.IPanelOperations _operations;

    public PanelConfigurationService(
        IPanelSender mediator,
        IPanelStateService panelState,
        ILogger<PanelConfigurationService> logger,
        NeoHub.Domain.IPanelOperations operations,
        TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _operations = operations;
        _mediator = mediator;
        _panelState = panelState;
        _logger = logger;
    }

    public Task<SectionResult> ReadAllAsync(string sessionId, string installerCode, CancellationToken ct)
    {
        if (_panelState.GetSession(sessionId) == null)
            return Task.FromResult(new SectionResult(false, "Session not found"));

        return _operations.RunAsync(sessionId, "read installer configuration", opCt =>
            RunInConfigModeAsync(sessionId, installerCode, readWrite: false, async () =>
            {
                await ReadSectionsAsync(sessionId, CreateSendDelegate(sessionId), opCt);
                return new SectionResult(true);
            }, opCt), ct);
    }

    public async Task ReadSectionsAsync(string sessionId, SendSectionRead send, CancellationToken ct)
    {
        var session = _panelState.GetSession(sessionId);
        if (session == null) return;

        var capabilities = new PanelCapabilities
        {
            MaxZones = session.MaxZones,
            MaxPartitions = session.MaxPartitions,
            MaxUsers = 0,
            MaxFOBs = 0,
            MaxProxTags = 0,
            MaxOutputs = 0,
        };
        var config = new PanelConfigurationState(capabilities);

        _logger.LogInformation("Reading panel configuration sections");

        foreach (var section in config.AllSections)
        {
            if (!section.IsSupported) continue;

            try
            {
                await section.ReadAllAsync(send, ct);
                _logger.LogDebug("Read {Section}", section.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read {Section}", section.DisplayName);
            }
        }

        config.LastReadAt = _time.GetUtcNow().UtcDateTime;

        await _panelState.DispatchAsync(new NeoHub.Domain.ConfigurationReplaced(sessionId, config));
        _logger.LogInformation("Panel configuration read complete");
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

    /// <summary>
    /// Enters config mode with the specified privilege, executes the operation,
    /// then always exits. Does NOT acquire the config lock.
    /// If the panel is already in programming mode, exits first to avoid "partition busy".
    /// </summary>
    public Task<SectionResult> ExecuteInConfigModeAsync(
        string sessionId, string installerCode, bool readWrite,
        Func<Task<SectionResult>> operation, CancellationToken ct)
        => _operations.RunAsync(sessionId, readWrite ? "installer configuration write" : "installer configuration read",
            opCt => RunInConfigModeAsync(sessionId, installerCode, readWrite, operation, opCt), ct);

    public async Task<SectionResult> RunInConfigModeAsync(
        string sessionId,
        string installerCode,
        bool readWrite,
        Func<Task<SectionResult>> operation,
        CancellationToken ct)
    {
        var session = _panelState.GetSession(sessionId);
        if (session == null)
            return new(false, "Session not found");

        // If panel is already in programming mode, exit first to avoid "partition busy"
        if (session.IsInProgrammingMode)
        {
            _logger.LogDebug("Panel already in programming mode, exiting first");
            await ExitConfigModeAsync(sessionId, ct);
        }

        var enterResult = await EnterConfigModeAsync(sessionId, installerCode, readWrite, ct);
        if (!enterResult.Success)
            return enterResult;

        try
        {
            return await operation();
        }
        finally
        {
            await ExitConfigModeAsync(sessionId, ct);
        }
    }

    private async Task<SectionResult> EnterConfigModeAsync(
        string sessionId, string installerCode, bool readWrite, CancellationToken ct)
    {
        try
        {
            var response = await _mediator.Send(new SessionCommand
            {
                SessionID = sessionId,
                MessageData = new ConfigurationEnter
                {
                    Partition = 1,
                    ProgrammingMode = ProgrammingMode.InstallersProgramming,
                    AccessCode = installerCode,
                    ReadWrite = readWrite
                        ? ConfigurationEnter.ReadWriteAccessEnum.ReadWriteMode
                        : ConfigurationEnter.ReadWriteAccessEnum.ReadOnlyMode
                }
            }, ct);

            if (!response.Success)
            {
                _logger.LogWarning("Failed to enter config mode: {Error}", response.ErrorMessage);
                return new(false, response.ErrorMessage ?? "Failed to enter config mode");
            }

            _logger.LogDebug("Entered installer config mode ({Mode})", readWrite ? "read-write" : "read-only");
            return new(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error entering config mode");
            return new(false, ex.Message);
        }
    }

    private async Task ExitConfigModeAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await _mediator.Send(new SessionCommand
            {
                SessionID = sessionId,
                MessageData = new ConfigurationExit()
            }, ct);

            _logger.LogDebug("Exited config mode");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to exit config mode");
        }
    }

    public SendSectionWrite CreateWriteDelegate(string sessionId) =>
        async (request, token) =>
        {
            var response = await _mediator.Send(new SessionCommand
            {
                SessionID = sessionId,
                MessageData = request
            }, token);

            return response.Success
                ? new SectionResult(true)
                : new SectionResult(false, response.ErrorMessage ?? "Write failed");
        };
}
