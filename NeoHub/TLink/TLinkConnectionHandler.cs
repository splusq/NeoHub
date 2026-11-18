// DSC TLink - a communications library for DSC Powerseries NEO alarm panels
// Copyright (C) 2024 Brian Humlicek
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Security.Cryptography;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Protocol;
using DSC.TLink.ITv2.Runtime;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DSC.TLink
{
    /// <summary>
    /// Kestrel entry point for a panel's TCP connection. Starts a <see cref="PanelConnection"/>,
    /// registers it once its handshake succeeds, hands its notifications to the application,
    /// and unregisters it when it ends. Owns no protocol state itself.
    /// </summary>
    internal class ITv2ConnectionHandler : ConnectionHandler
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ITv2ConnectionHandler> _log;

        internal static Dictionary<string, object> CreateLogScope(string sessionId) =>
            new() { [ConnectionSettings.LogScopeKey] = sessionId };

        public ITv2ConnectionHandler(
            IServiceProvider serviceProvider,
            ILogger<ITv2ConnectionHandler> log)
        {
            _serviceProvider = serviceProvider;
            _log = log;
        }

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            _log.LogInformation("Connection request from {RemoteEndPoint}", connection.RemoteEndPoint);

            var settingsProvider = _serviceProvider.GetRequiredService<IConnectionSettingsProvider>();
            var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
            var sessionMediator = _serviceProvider.GetRequiredService<SessionMediator>();
            var registry = _serviceProvider.GetRequiredService<PanelRegistry>();
            var time = _serviceProvider.GetRequiredService<TimeProvider>();

            var transport = new TLinkTransport(connection.Transport, loggerFactory.CreateLogger<TLinkTransport>());
            var panel = PanelConnection.Start(
                transport,
                (sessionId, encryption) => Credentials(settingsProvider.ResolveConnection(sessionId, encryption)),
                time,
                new ConnectionConfig(ConnectionTimings.Default, RandomNumberGenerator.GetBytes(ConnectionConfig.LocalRandomLength)),
                loggerFactory.CreateLogger<PanelConnection>());

            // Notifications go to the application in arrival order, on their own loop.
            var notifications = DeliverNotificationsAsync(panel, sessionMediator);
            try
            {
                string sessionId;
                try
                {
                    sessionId = await panel.Established;
                }
                catch (OperationCanceledException)
                {
                    _log.LogWarning("Panel connection ended before its handshake completed");
                    return;
                }

                using var scope = _log.BeginScope(CreateLogScope(sessionId));
                registry.Register(panel);
                try
                {
                    await panel.Completion;
                }
                finally
                {
                    registry.Unregister(panel);
                }
            }
            finally
            {
                await panel.Completion;
                await notifications;
                _log.LogInformation("TLink disconnected from {RemoteEndPoint}", connection.RemoteEndPoint);
            }
        }

        private async Task DeliverNotificationsAsync(PanelConnection panel, SessionMediator mediator)
        {
            await Task.Yield();
            await foreach (var message in panel.GetNotificationsAsync())
                await mediator.PublishNotificationAsync(panel.SessionId, message);
        }

        private static ConnectionCredentials? Credentials(ConnectionSettings? settings)
            => settings is null
                ? null
                : new ConnectionCredentials(settings.EncryptionType, settings.IntegrationAccessCodeType1, settings.IntegrationAccessCodeType2);
    }
}
