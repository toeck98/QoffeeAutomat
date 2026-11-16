using System.Net.WebSockets;

namespace QoffeeAutomat.Agent.Link.Pairing;

/// <summary>
/// An open QoffeeLink connection as the registry sees it. The QoffeeLink server (T210) implements this
/// for every accepted WebSocket so that revoking a token can close the connection.
/// </summary>
public interface ILinkConnection
{
    /// <summary>Closes the connection with a WebSocket close code (see <c>CloseCodes</c> in the contracts).</summary>
    /// <param name="closeCode">The close code, for example 4401 after a revocation.</param>
    /// <param name="reason">A short reason for the close frame; never contains secrets.</param>
    /// <param name="cancellationToken">Cancels the close handshake.</param>
    /// <returns>A task that completes when the connection is closed.</returns>
    ValueTask CloseAsync(int closeCode, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// Remembers which connections belong to which paired client (PLAN.md T211, T210). The QoffeeLink
/// server calls <see cref="Register"/> once the bearer token is validated and disposes the returned
/// registration when the connection ends; <see cref="PairingService.RevokeAsync"/> uses
/// <see cref="CloseAllAsync"/> to drop the connections of a revoked client.
/// </summary>
public sealed class LinkConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<ILinkConnection>> _byClient = new(StringComparer.Ordinal);

    /// <summary>The number of open connections of all clients.</summary>
    public int TotalCount
    {
        get
        {
            lock (_gate)
            {
                return _byClient.Values.Sum(list => list.Count);
            }
        }
    }

    /// <summary>Registers an open connection.</summary>
    /// <param name="clientId">The id of the client that owns the token (<see cref="LinkClient.Id"/>).</param>
    /// <param name="connection">The connection.</param>
    /// <returns>A registration; dispose it when the connection ends.</returns>
    public IDisposable Register(string clientId, ILinkConnection connection)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(connection);

        lock (_gate)
        {
            if (!_byClient.TryGetValue(clientId, out List<ILinkConnection>? list))
            {
                list = [];
                _byClient[clientId] = list;
            }

            list.Add(connection);
        }

        return new Registration(this, clientId, connection);
    }

    /// <summary>Counts the open connections of a client.</summary>
    /// <param name="clientId">The client id.</param>
    /// <returns>The number of registered connections.</returns>
    public int Count(string clientId)
    {
        ArgumentNullException.ThrowIfNull(clientId);

        lock (_gate)
        {
            return _byClient.TryGetValue(clientId, out List<ILinkConnection>? list) ? list.Count : 0;
        }
    }

    /// <summary>Closes every open connection of a client.</summary>
    /// <param name="clientId">The client id.</param>
    /// <param name="closeCode">The WebSocket close code.</param>
    /// <param name="reason">The close reason.</param>
    /// <param name="cancellationToken">Cancels the closing.</param>
    /// <returns>The number of connections that were asked to close.</returns>
    public async Task<int> CloseAllAsync(string clientId, int closeCode, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientId);

        ILinkConnection[] connections;
        lock (_gate)
        {
            if (!_byClient.Remove(clientId, out List<ILinkConnection>? list))
            {
                return 0;
            }

            connections = [.. list];
        }

        foreach (ILinkConnection connection in connections)
        {
            try
            {
                await connection.CloseAsync(closeCode, reason, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or InvalidOperationException or IOException)
            {
                // The connection was already gone; the goal, no open connection, is reached.
            }
        }

        return connections.Length;
    }

    private void Unregister(string clientId, ILinkConnection connection)
    {
        lock (_gate)
        {
            if (_byClient.TryGetValue(clientId, out List<ILinkConnection>? list))
            {
                list.Remove(connection);
                if (list.Count == 0)
                {
                    _byClient.Remove(clientId);
                }
            }
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly LinkConnectionRegistry _owner;
        private readonly string _clientId;
        private readonly ILinkConnection _connection;
        private int _disposed;

        public Registration(LinkConnectionRegistry owner, string clientId, ILinkConnection connection)
        {
            _owner = owner;
            _clientId = clientId;
            _connection = connection;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unregister(_clientId, _connection);
            }
        }
    }
}
