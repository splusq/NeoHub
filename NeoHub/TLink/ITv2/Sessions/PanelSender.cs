using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.Logging;

namespace DSC.TLink.ITv2.Sessions
{
    /// <summary>Sends a message to a panel and waits for its reply.</summary>
    public interface IPanelSender
    {
        /// <summary>
        /// Sends <see cref="SessionCommand.MessageData"/> to the panel's live connection. Never throws
        /// for panel or transport failures (they come back as an unsuccessful response); it throws
        /// <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        Task<SessionResponse> Send(SessionCommand command, CancellationToken cancellationToken = default);
    }

    /// <summary>Routes a command to the panel's current connection.</summary>
    internal sealed class PanelSender : IPanelSender
    {
        private readonly IITv2SessionManager _sessionManager;
        private readonly ILogger<PanelSender> _logger;

        public PanelSender(IITv2SessionManager sessionManager, ILogger<PanelSender> logger)
        {
            _sessionManager = sessionManager;
            _logger = logger;
        }

        public async Task<SessionResponse> Send(SessionCommand request, CancellationToken cancellationToken = default)
        {
            var session = _sessionManager.GetSession(request.SessionID);
            if (session == null)
            {
                _logger.LogWarning("Command failed - session not found");
                return new SessionResponse
                {
                    Success = false,
                    ErrorCode = TLinkErrorCode.SessionNotFound,
                    ErrorMessage = $"Session {request.SessionID} not found"
                };
            }

            try
            {
                var result = await session.SendAsync(request.MessageData, cancellationToken);

                if (result.IsFailure)
                {
                    return new SessionResponse
                    {
                        Success = false,
                        ErrorCode = result.Error!.Value.Code,
                        ErrorMessage = result.Error.Value.Message
                    };
                }

                // A Nack: the panel refused the command itself (bad length, partition, code...).
                if (result.Value is CommandError nack)
                {
                    _logger.LogWarning("Panel refused {Command}: {Nack}", nack.NackCommand, nack.NackCode);
                    return new SessionResponse
                    {
                        Success = false,
                        ErrorMessage = $"Panel refused {nack.NackCommand}: {nack.NackCode}",
                        MessageData = nack,
                    };
                }

                if (result.Value is CommandResponse { ResponseCode: not CommandResponseCode.Success } errorResponse)
                {
                    _logger.LogWarning("Panel rejected command: [{Code}] {Description}",
                        errorResponse.ResponseCode, errorResponse.ResponseCode.Description());

                    return new SessionResponse
                    {
                        Success = false,
                        ErrorMessage = errorResponse.ResponseCode.Description(),
                    };
                }

                return new SessionResponse { Success = true, MessageData = result.Value };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling command");
                return new SessionResponse { Success = false, ErrorMessage = ex.Message };
            }
        }
    }
}
