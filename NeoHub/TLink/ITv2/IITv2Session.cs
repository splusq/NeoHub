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

using DSC.TLink.ITv2.Messages;

namespace DSC.TLink.ITv2;

/// <summary>
/// Represents a connected ITv2 session that can send commands and stream notifications.
/// </summary>
internal interface IITv2Session : IAsyncDisposable
{
    string SessionId { get; }

    /// <summary>
    /// Cancelled when this connection ends for any reason (transport closed, END_SESSION,
    /// heartbeat gave up, replaced by a reconnect, disposed). Work tied to this connection,
    /// such as the initial configuration pull, should stop when it fires.
    /// </summary>
    CancellationToken Lifetime { get; }

    /// <summary>
    /// Send a message and wait for the protocol+command transaction to complete.
    /// Returns the command response for command messages, or the original message for notifications.
    /// Commands run one at a time per session. Fails with <see cref="TLinkErrorCode.Timeout"/> if
    /// the panel doesn't answer in time and <see cref="TLinkErrorCode.Disconnected"/> if the
    /// session ends first; only cancellation of <paramref name="ct"/> throws.
    /// </summary>
    Task<Result<IMessageData>> SendAsync(IMessageData message, CancellationToken ct = default);

    /// <summary>
    /// Yields inbound notifications after the session is connected.
    /// Command responses are routed internally and never appear here.
    /// MultipleMessagePackets are expanded — individual sub-messages are yielded.
    /// </summary>
    IAsyncEnumerable<IMessageData> GetNotificationsAsync(CancellationToken ct = default);
}
