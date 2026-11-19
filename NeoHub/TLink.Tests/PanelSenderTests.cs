using System.Runtime.CompilerServices;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using DSC.TLink.ITv2.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DSC.TLink.Tests;

/// <summary>What callers (arm, disarm, bypass, the Dev Console) are told about the panel's answer.</summary>
public class PanelSenderTests
{
    private const string Panel = "200000000001";

    private sealed class OneSession(Result<IMessageData> reply) : IITv2SessionManager, IITv2Session
    {
        public string SessionId => Panel;
        public CancellationToken Lifetime => CancellationToken.None;
        public Task<Result<IMessageData>> SendAsync(IMessageData message, CancellationToken ct = default) => Task.FromResult(reply);
        public async IAsyncEnumerable<PanelMessage> GetNotificationsAsync([EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        void IITv2SessionManager.RegisterSession(string sessionId, IITv2Session session) { }
        void IITv2SessionManager.UnregisterSession(string sessionId, IITv2Session session) { }
        IITv2Session? IITv2SessionManager.GetSession(string sessionId) => sessionId == Panel ? this : null;
        public IEnumerable<string> GetActiveSessions() => [Panel];
        public Task DisconnectSessionAsync(string sessionId) => Task.CompletedTask;
        public Task DisconnectAllAsync() => Task.CompletedTask;
    }

    private static Task<SessionResponse> Send(Result<IMessageData> reply)
        => new PanelSender(new OneSession(reply), NullLogger<PanelSender>.Instance)
            .Send(new SessionCommand { SessionID = Panel, MessageData = new PartitionBuzzerControl { Payload = [0x01, 0x01] } });

    /// <summary>A Nack used to come back as Success, so an arm the panel refused looked like it worked.</summary>
    [Fact]
    public async Task PanelNack_IsAFailure_ThatSaysWhatWasWrong()
    {
        var nack = new CommandError { NackCommand = ITv2Command.ModuleControl_Partition_Buzzer_Control, NackCode = ITv2NackCode.InvalidCommandLength };

        var response = await Send(nack);

        Assert.False(response.Success);
        Assert.Contains("InvalidCommandLength", response.ErrorMessage);
        Assert.Same(nack, response.MessageData);
    }

    [Fact]
    public async Task PanelRejection_InACommandResponse_IsStillAFailure()
    {
        var response = await Send(new CommandResponse { ResponseCode = CommandResponseCode.FunctionNotAvailable });
        Assert.False(response.Success);
    }

    [Fact]
    public async Task Success_IsSuccess()
    {
        var response = await Send(new CommandResponse { ResponseCode = CommandResponseCode.Success });
        Assert.True(response.Success);
    }

    // ── The experimental buzzer message ──

    [Fact]
    public void BuzzerControl_SendsItsPayloadVerbatim_AfterTheCommandSequence()
    {
        var message = new PartitionBuzzerControl { CommandSequence = 0x42, Payload = [0x01, 0x01, 0x01, 0x01, 0x02] };

        var bytes = MessageFactory.SerializeMessage(message);

        Assert.Equal(new byte[] { 0x09, 0x14, 0x42, 0x01, 0x01, 0x01, 0x01, 0x02 }, bytes.ToArray());
    }

    [Fact]
    public void BuzzerControl_RoundTrips()
    {
        var bytes = MessageFactory.SerializeMessage(new PartitionBuzzerControl { CommandSequence = 7, Payload = [0x01, 0x01, 0x05] }).ToArray();

        var decoded = Assert.IsType<PartitionBuzzerControl>(MessageFactory.DeserializeMessage(bytes));
        Assert.Equal(7, decoded.CommandSequence);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x05 }, decoded.Payload);
    }

    [Fact]
    public void BuzzerControl_IsOfferedByTheDevConsole()
        => Assert.Contains(MessageCatalog.GetSendable(), m => m.MessageType == typeof(PartitionBuzzerControl));
}
