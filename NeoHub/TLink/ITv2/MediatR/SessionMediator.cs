using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DSC.TLink.ITv2.MediatR
{
    /// <summary>
    /// Unified mediator that handles both outbound commands (from Blazor UI) 
    /// and publishes inbound notifications (from panel).
    /// Registered as singleton - uses SessionManager for routing.
    /// </summary>
    internal class SessionMediator : IRequestHandler<SessionCommand, SessionResponse>
    {
        private readonly IMediator _mediator;
        private readonly IITv2SessionManager _sessionManager;
        private readonly ILogger<SessionMediator> _logger;

        public SessionMediator(
            IMediator mediator,
            IITv2SessionManager sessionManager,
            ILogger<SessionMediator> logger)
        {
            _mediator = mediator;
            _sessionManager = sessionManager;
            _logger = logger;
        }

        #region Command Handling (Outbound from Blazor UI)

        public async Task<SessionResponse> Handle(
            SessionCommand request,
            CancellationToken cancellationToken)
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

                return new SessionResponse
                {
                    Success = true,
                    MessageData = result.Value
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling command");
                return new SessionResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        #endregion


    }
}
