using System.Runtime.CompilerServices;
using DSC.TLink;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using MediatR;

namespace DSC.TLink.Tests;

internal sealed class FakeSession(string sessionId, string name) : IITv2Session
{
    public string SessionId => sessionId;
    public CancellationTokenSource LifetimeSource { get; } = new();
    public CancellationToken Lifetime => LifetimeSource.Token;
    public int DisposeCount;

    public Task<Result<IMessageData>> SendAsync(IMessageData message, CancellationToken ct = default)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<IMessageData> GetNotificationsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref DisposeCount);
        return ValueTask.CompletedTask;
    }

    public override string ToString() => name;
}

/// <summary>Records published lifecycle notifications in order.</summary>
internal sealed class RecordingMediator : IMediator
{
    private readonly List<string> _events = new();

    public IReadOnlyList<string> Events { get { lock (_events) return _events.ToList(); } }

    public Task Publish(object notification, CancellationToken ct = default)
    {
        lock (_events) _events.Add(notification.GetType().Name.Replace("Notification", ""));
        return Task.CompletedTask;
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default)
        where TNotification : INotification => Publish((object)notification!, ct);

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest => throw new NotSupportedException();
    public Task<object?> Send(object request, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>A transport that never delivers anything, like a half-open TCP connection.</summary>
internal sealed class SilentTransport : ITLinkTransport
{
    public int DisposeCount;
    public ReadOnlyMemory<byte> DefaultHeader => ReadOnlyMemory<byte>.Empty;

    public Task<Result<TLinkMessage>> ReadMessageAsync(CancellationToken ct = default)
        => Task.FromResult(Result<TLinkMessage>.Fail(TLinkErrorCode.Cancelled, "silent"));

    public async IAsyncEnumerable<Result<TLinkMessage>> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    public Task<Result> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => Task.FromResult(Result.Ok());

    public Task<Result> SendAsync(ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => Task.FromResult(Result.Ok());

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref DisposeCount);
        return ValueTask.CompletedTask;
    }
}

internal sealed class NullSettingsProvider : IConnectionSettingsProvider
{
    public ConnectionSettings? ResolveConnection(string sessionId, EncryptionType encryptionType) => null;
    public void ConfirmDefaults(string sessionId) { }
}
