using System.Collections.Immutable;
using System.Threading.Channels;
using DSC.TLink.ITv2.Sessions;
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
/// Lifecycle events go to the application through <see cref="PanelLifecycle"/>, in order (a
/// reconnect is always "disconnected" then "connected"); <see cref="PanelLifecycleDispatcher"/>
/// delivers them, so a slow observer (the configuration pull) never holds up registrations.
/// </summary>
internal sealed class PanelRegistry : IITv2SessionManager, IAsyncDisposable
{
    private abstract record Command;
    private sealed record Add(PanelConnection Connection) : Command;
    private sealed record Remove(PanelConnection Connection) : Command;

    private readonly Channel<Command> _inbox = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly PanelLifecycle _lifecycle;
    private readonly ILogger<PanelRegistry> _logger;
    private readonly Task _run;

    // Written only by the registry loop; read by anyone. Immutable, so a reader always sees a
    // complete map, just possibly a moment old.
    private readonly Concurrency.Snapshot<ImmutableDictionary<string, PanelConnection>> _snapshot = new(ImmutableDictionary<string, PanelConnection>.Empty);

    public PanelRegistry(PanelLifecycle lifecycle, ILogger<PanelRegistry> logger)
    {
        _lifecycle = lifecycle;
        _logger = logger;
        _run = RunAsync();
    }

    /// <summary>Records a connection whose handshake finished.</summary>
    public void Register(PanelConnection connection) => _inbox.Writer.TryWrite(new Add(connection));

    /// <summary>Forgets a connection that ended (no-op if it was already replaced).</summary>
    public void Unregister(PanelConnection connection) => _inbox.Writer.TryWrite(new Remove(connection));

    /// <summary>The live connection for a panel, if any.</summary>
    public PanelConnection? Find(string sessionId) => _snapshot.Value.GetValueOrDefault(sessionId);

    // ── IITv2SessionManager (the application's existing view) ──

    void IITv2SessionManager.RegisterSession(string sessionId, IITv2Session session)
        => Register((PanelConnection)session);

    void IITv2SessionManager.UnregisterSession(string sessionId, IITv2Session session)
        => Unregister((PanelConnection)session);

    IITv2Session? IITv2SessionManager.GetSession(string sessionId) => Find(sessionId);

    public IEnumerable<string> GetActiveSessions() => _snapshot.Value.Keys;

    public async Task DisconnectSessionAsync(string sessionId)
    {
        if (Find(sessionId) is { } connection)
            await connection.DisposeAsync();
    }

    public async Task DisconnectAllAsync()
        => await Task.WhenAll(_snapshot.Value.Values.Select(c => c.DisposeAsync().AsTask()));

    public async ValueTask DisposeAsync()
    {
        _inbox.Writer.TryComplete();
        await _run;
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
                        _snapshot.Publish(current);

                        if (existing is not null)
                        {
                            _logger.LogWarning("Panel {SessionId} reconnected while its previous connection was still open; replacing it", id);
                            existing.RequestClose();
                            _lifecycle.Post(new SessionDisconnectedNotification(id));
                        }
                        else
                        {
                            _logger.LogInformation("Session registered. Active sessions: {Count}", current.Count);
                        }
                        _lifecycle.Post(new SessionConnectedNotification(id, connection.Lifetime, connection.ConnectionId));
                        break;
                    }

                    case Remove(var connection):
                    {
                        var id = connection.SessionId;
                        if (id.Length == 0 || !current.TryGetValue(id, out var existing) || !ReferenceEquals(existing, connection))
                            break;   // never registered, or already replaced by a newer connection
                        current = current.Remove(id);
                        _snapshot.Publish(current);
                        _logger.LogInformation("Session unregistered. Active sessions: {Count}", current.Count);
                        _lifecycle.Post(new SessionDisconnectedNotification(id));
                        break;
                    }
                }
            }
        }
        finally
        {
            _lifecycle.Complete();
        }
    }
}
