using System.Collections.Immutable;
using System.Threading.Channels;
using DSC.TLink.ITv2.MediatR;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DSC.TLink.ITv2.Runtime;

/// <summary>
/// Which connection currently speaks for each panel. One loop owns the map; registrations
/// and removals are messages in a <see cref="Channel{T}"/>, applied in order. Readers get the
/// latest immutable snapshot, published by that loop alone.
///
/// The newest connection for a panel always wins: a reconnect replaces the stale one, which is
/// asked to close, and a stale connection closing later never removes the live one.
///
/// Lifecycle events go to the application through a second channel and are delivered one at a
/// time, in order (a reconnect is always "disconnected" then "connected"), by their own loop, so
/// a slow handler (the configuration pull) never holds up registrations.
/// </summary>
internal sealed class PanelRegistry : IITv2SessionManager, IAsyncDisposable
{
    private abstract record Command;
    private sealed record Add(PanelConnection Connection) : Command;
    private sealed record Remove(PanelConnection Connection) : Command;

    private readonly Channel<Command> _inbox = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<INotification> _lifecycle = Channel.CreateUnbounded<INotification>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly IPublisher _publisher;
    private readonly ILogger<PanelRegistry> _logger;
    private readonly Task _run;
    private readonly Task _dispatch;

    // Written only by the registry loop; read by anyone. Immutable, so a reader always sees a
    // complete map, just possibly a moment old.
    private ImmutableDictionary<string, PanelConnection> _snapshot = ImmutableDictionary<string, PanelConnection>.Empty;

    public PanelRegistry(IPublisher publisher, ILogger<PanelRegistry> logger)
    {
        _publisher = publisher;
        _logger = logger;
        _run = RunAsync();
        _dispatch = DispatchAsync();
    }

    /// <summary>Records a connection whose handshake finished.</summary>
    public void Register(PanelConnection connection) => _inbox.Writer.TryWrite(new Add(connection));

    /// <summary>Forgets a connection that ended (no-op if it was already replaced).</summary>
    public void Unregister(PanelConnection connection) => _inbox.Writer.TryWrite(new Remove(connection));

    /// <summary>The live connection for a panel, if any.</summary>
    public PanelConnection? Find(string sessionId) => Volatile.Read(ref _snapshot).GetValueOrDefault(sessionId);

    /// <summary>Lifecycle events as they are delivered (for tests and diagnostics).</summary>
    internal event Action<INotification>? Delivered;

    // ── IITv2SessionManager (the application's existing view) ──

    void IITv2SessionManager.RegisterSession(string sessionId, IITv2Session session)
        => Register((PanelConnection)session);

    void IITv2SessionManager.UnregisterSession(string sessionId, IITv2Session session)
        => Unregister((PanelConnection)session);

    IITv2Session? IITv2SessionManager.GetSession(string sessionId) => Find(sessionId);

    public IEnumerable<string> GetActiveSessions() => Volatile.Read(ref _snapshot).Keys;

    public async Task DisconnectSessionAsync(string sessionId)
    {
        if (Find(sessionId) is { } connection)
            await connection.DisposeAsync();
    }

    public async Task DisconnectAllAsync()
        => await Task.WhenAll(Volatile.Read(ref _snapshot).Values.Select(c => c.DisposeAsync().AsTask()));

    public async ValueTask DisposeAsync()
    {
        _inbox.Writer.TryComplete();
        await _run;
        await _dispatch;
    }

    // ── Loops ──

    private async Task RunAsync()
    {
        await Task.Yield();
        var current = ImmutableDictionary<string, PanelConnection>.Empty;
        try
        {
            await foreach (var command in _inbox.Reader.ReadAllAsync())
            {
                switch (command)
                {
                    case Add(var connection):
                    {
                        var id = connection.SessionId;
                        if (current.TryGetValue(id, out var existing) && ReferenceEquals(existing, connection))
                            break;
                        current = current.SetItem(id, connection);
                        Volatile.Write(ref _snapshot, current);

                        if (existing is not null)
                        {
                            _logger.LogWarning("Panel {SessionId} reconnected while its previous connection was still open; replacing it", id);
                            existing.RequestClose();
                            _lifecycle.Writer.TryWrite(new SessionDisconnectedNotification(id));
                        }
                        else
                        {
                            _logger.LogInformation("Session registered. Active sessions: {Count}", current.Count);
                        }
                        _lifecycle.Writer.TryWrite(new SessionConnectedNotification(id, connection.Lifetime));
                        break;
                    }

                    case Remove(var connection):
                    {
                        var id = connection.SessionId;
                        if (id.Length == 0 || !current.TryGetValue(id, out var existing) || !ReferenceEquals(existing, connection))
                            break;   // never registered, or already replaced by a newer connection
                        current = current.Remove(id);
                        Volatile.Write(ref _snapshot, current);
                        _logger.LogInformation("Session unregistered. Active sessions: {Count}", current.Count);
                        _lifecycle.Writer.TryWrite(new SessionDisconnectedNotification(id));
                        break;
                    }
                }
            }
        }
        finally
        {
            _lifecycle.Writer.TryComplete();
        }
    }

    private async Task DispatchAsync()
    {
        await Task.Yield();
        await foreach (var notification in _lifecycle.Reader.ReadAllAsync())
        {
            try
            {
                await _publisher.Publish(notification);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling {Notification}", notification.GetType().Name);
            }
            Delivered?.Invoke(notification);
        }
    }
}
