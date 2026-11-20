using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Security;

/// <summary>Registers the security services of the hub: accounts, sessions, API tokens, authorization policies and the audit log (T903).</summary>
public static class SecurityServices
{
    /// <summary>Adds the services. Existing registrations of the same services are kept, so tests can replace them first.</summary>
    /// <param name="services">The service collection; <see cref="Storage.HubDatabase"/>, <see cref="Hosting.HubDataDirectory"/> and <see cref="TimeProvider"/> must be registered.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddHubSecurity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(new HubAuthOptions());
        services.TryAddSingleton<UserStore>();
        services.TryAddSingleton<HubSessionStore>();
        services.TryAddSingleton<LoginRateLimiter>();
        services.TryAddSingleton<UserService>();
        services.TryAddSingleton<ApiTokenService>();
        services.TryAddSingleton<SetupTokenService>();
        services.TryAddSingleton<SetupService>();
        services.TryAddSingleton<LoginService>();
        services.TryAddSingleton<AuditLog>();

        services.AddAuthentication(HubAuthNames.Scheme)
            .AddScheme<AuthenticationSchemeOptions, HubAuthenticationHandler>(HubAuthNames.Scheme, _ => { });
        services.AddAuthorization(options =>
        {
            // One policy per role; each lets in its role and every more powerful one.
            options.AddPolicy(HubPolicies.Viewer, policy => policy
                .AddAuthenticationSchemes(HubAuthNames.Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(HubRoles.Viewer, HubRoles.Operator, HubRoles.Admin));
            options.AddPolicy(HubPolicies.Operator, policy => policy
                .AddAuthenticationSchemes(HubAuthNames.Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(HubRoles.Operator, HubRoles.Admin));
            options.AddPolicy(HubPolicies.Admin, policy => policy
                .AddAuthenticationSchemes(HubAuthNames.Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(HubRoles.Admin));
        });

        services.AddHostedService<SecurityStartupService>();
        return services;
    }
}

/// <summary>
/// At start-up: while the hub has no account, makes sure that the one-time setup token exists and prints it to the log, so the operator
/// can open the UI and create the first administrator; once an account exists, removes a setup token file that is left over.
/// </summary>
public sealed class SecurityStartupService : IHostedService
{
    private readonly UserService _users;
    private readonly SetupTokenService _setupToken;
    private readonly ILogger<SecurityStartupService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="users">The accounts.</param>
    /// <param name="setupToken">The setup token file.</param>
    /// <param name="logger">The logger.</param>
    public SecurityStartupService(UserService users, SetupTokenService setupToken, ILogger<SecurityStartupService> logger)
    {
        _users = users;
        _setupToken = setupToken;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (await _users.CountAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            string token = await _setupToken.EnsureAsync(cancellationToken).ConfigureAwait(false);
            SecurityLog.SetupTokenReady(_logger, token, _setupToken.FilePath);
            return;
        }

        if (_setupToken.Exists())
        {
            _setupToken.Delete();
            SecurityLog.StaleSetupTokenRemoved(_logger, _setupToken.FilePath);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
