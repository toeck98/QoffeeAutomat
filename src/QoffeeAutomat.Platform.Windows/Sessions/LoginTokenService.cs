using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>
/// The agent's half of the one-click UI login from the tray (PLAN.md S09 item 12, S11, T711). The tray entry Open UI of the
/// SessionHost sends <c>login_token_request</c>; this service answers with a single-use token (valid 30 seconds, redeemable
/// from loopback only, see <see cref="ILoginTokenIssuer"/>) when, and only when, the user of the active console session is a
/// member of the local Administrators group. A standard user gets a reason and never a token, and the SessionHost opens the
/// normal login page.
/// </summary>
/// <remarks>
/// The decision does not rely on anything the SessionHost says about itself: <c>is_administrator</c> in its <c>hello</c> is
/// ignored. The agent asks the operating system for the token of the user that is logged on in the session of the connection
/// (<see cref="IElevatedNativeApi.OpenElevatedToken"/>): an administrator under User Account Control has an elevated linked token,
/// a standard user has none. With User Account Control switched off no linked token exists either, so such an administrator gets
/// the login page too; refusing is the safe side of that uncertainty. Only the connection of the active console session is
/// answered, like the pause request of the tray icon.
/// </remarks>
public sealed class LoginTokenService : IHostedService
{
    private readonly WindowsSessionManager _manager;
    private readonly Func<SessionConnection?> _activeConnection;
    private readonly IElevatedNativeApi _native;
    private readonly ILoginTokenIssuer _issuer;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly HashSet<SessionConnection> _attached = [];

    /// <summary>Creates the service.</summary>
    /// <param name="manager">Holds the connections of the SessionHosts.</param>
    /// <param name="activeConnection">Gives the connection of the active console session; null when there is none.</param>
    /// <param name="native">Reads the token of the user of a session.</param>
    /// <param name="issuer">Creates the tokens.</param>
    /// <param name="logger">The logger.</param>
    public LoginTokenService(
        WindowsSessionManager manager,
        Func<SessionConnection?> activeConnection,
        IElevatedNativeApi native,
        ILoginTokenIssuer issuer,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(activeConnection);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(logger);

        _manager = manager;
        _activeConnection = activeConnection;
        _native = native;
        _issuer = issuer;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _manager.ConnectionsChanged += OnConnectionsChanged;
        Attach();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _manager.ConnectionsChanged -= OnConnectionsChanged;
        lock (_gate)
        {
            foreach (SessionConnection connection in _attached)
            {
                connection.LoginTokenRequested -= OnRequested;
            }

            _attached.Clear();
        }

        return Task.CompletedTask;
    }

    /// <summary>Decides one request. A token is issued only for an administrator in the active console session.</summary>
    /// <param name="connection">The connection that asked.</param>
    /// <param name="requestId">The id of the request, copied into the answer.</param>
    /// <returns>The answer: a token, or the reason why there is none.</returns>
    public SessionLoginTokenMessage Decide(SessionConnection connection, string requestId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(requestId);

        if (!ReferenceEquals(_activeConnection(), connection))
        {
            return Deny(connection, requestId, SessionLoginDenials.NotConsole);
        }

        ElevatedTokenResult result;
        try
        {
            result = _native.OpenElevatedToken(connection.SessionId);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return Deny(connection, requestId, SessionLoginDenials.Unavailable);
        }

        switch (result.Status)
        {
            case ElevatedTokenStatus.Available:
                // Only the fact that the user has an elevated token matters; the token itself is not used.
                result.Token?.Dispose();
                string token = _issuer.Issue();
                SessionLog.LoginTokenIssued(_logger, connection.SessionId, connection.UserName);
                return new SessionLoginTokenMessage(requestId, Token: token);
            case ElevatedTokenStatus.NoLinkedToken:
                return Deny(connection, requestId, SessionLoginDenials.NotAdministrator);
            default:
                return Deny(connection, requestId, SessionLoginDenials.Unavailable);
        }
    }

    private SessionLoginTokenMessage Deny(SessionConnection connection, string requestId, string reason)
    {
        SessionLog.LoginTokenDenied(_logger, connection.SessionId, connection.UserName, reason);
        return new SessionLoginTokenMessage(requestId, Reason: reason);
    }

    private void OnConnectionsChanged(object? sender, EventArgs e) => Attach();

    private void Attach()
    {
        IReadOnlyList<SessionConnection> open = _manager.GetConnections();
        lock (_gate)
        {
            foreach (SessionConnection connection in open)
            {
                if (_attached.Add(connection))
                {
                    connection.LoginTokenRequested += OnRequested;
                }
            }

            foreach (SessionConnection connection in _attached.Where(connection => !open.Contains(connection)).ToArray())
            {
                connection.LoginTokenRequested -= OnRequested;
                _attached.Remove(connection);
            }
        }
    }

    private void OnRequested(object? sender, SessionLoginTokenRequestedEventArgs e)
    {
        if (sender is SessionConnection connection)
        {
            // The handler of the connection must return quickly: the token query and the answer run on their own.
            _ = AnswerAsync(connection, e.RequestId);
        }
    }

    private async Task AnswerAsync(SessionConnection connection, string requestId)
    {
        try
        {
            SessionLoginTokenMessage answer = await Task.Run(() => Decide(connection, requestId)).ConfigureAwait(false);
            await connection.SendLoginTokenAsync(answer, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // The connection is ending; the SessionHost times out and opens the login page.
        }
    }
}
