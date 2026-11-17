using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Agent.Security.Auth;
using QoffeeAutomat.Agent.Security.Tls;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Security;

/// <summary>Registers the security services of the agent: TLS certificate, pairing, link tokens and management API authentication (T211, T212).</summary>
public static class SecurityServices
{
    /// <summary>Adds the services. Existing registrations of the same services are kept, so tests can replace them first.</summary>
    /// <param name="services">The service collection; <see cref="ISecretStore"/>, <see cref="TimeProvider"/> and <see cref="IAppPaths"/> must be registered.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddAgentSecurity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // TLS certificate (T211): the HTTPS listener opens once the certificate exists.
        services.TryAddSingleton(provider => new TlsCertificateProvider(
            provider.GetRequiredService<ISecretStore>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ITlsCertificateSource>(provider => provider.GetRequiredService<TlsCertificateProvider>());

        // Pairing and link tokens (T211).
        services.TryAddSingleton(provider => new PairingCodeManager(provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(provider => new LinkTokenStore(
            provider.GetRequiredService<ISecretStore>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<LinkConnectionRegistry>();
        services.TryAddSingleton<PairingService>();

        // Management API authentication (T212).
        services.TryAddSingleton(new AuthOptions());
        services.TryAddSingleton<PasswordService>();
        services.TryAddSingleton<SetupTokenService>();
        services.TryAddSingleton<SessionStore>();
        services.TryAddSingleton<AttemptLimiter>();
        services.TryAddSingleton<AccountService>();

        // One-click UI login from the tray (T711).
        services.TryAddSingleton<OneTimeLoginService>();
        services.TryAddSingleton<ILoginTokenIssuer>(provider => provider.GetRequiredService<OneTimeLoginService>());

        services.AddAuthentication(AuthNames.Scheme)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(AuthNames.Scheme, _ => { });
        services.AddAuthorization(options => options.AddPolicy(AuthNames.ManagementPolicy, policy => policy
            .AddAuthenticationSchemes(AuthNames.Scheme)
            .RequireAuthenticatedUser()));

        services.AddHostedService<SecurityStartupService>();
        return services;
    }
}

/// <summary>
/// At start-up: loads or creates the TLS certificate and logs its fingerprint, and writes the setup token
/// file on the first run (no password yet), so the user can open the UI and set a password.
/// </summary>
public sealed class SecurityStartupService : IHostedService
{
    private readonly ITlsCertificateSource _certificate;
    private readonly PasswordService _passwords;
    private readonly SetupTokenService _setupToken;
    private readonly ILogger<SecurityStartupService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="certificate">The TLS certificate.</param>
    /// <param name="passwords">The password.</param>
    /// <param name="setupToken">The setup token file.</param>
    /// <param name="logger">The logger.</param>
    public SecurityStartupService(
        ITlsCertificateSource certificate,
        PasswordService passwords,
        SetupTokenService setupToken,
        ILogger<SecurityStartupService> logger)
    {
        _certificate = certificate;
        _passwords = passwords;
        _setupToken = setupToken;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_certificate.Fingerprint is { } fingerprint)
        {
            SecurityLog.TlsFingerprint(_logger, fingerprint);
        }

        if (!await _passwords.HasPasswordAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_setupToken.Exists())
            {
                await _setupToken.CreateAsync(cancellationToken).ConfigureAwait(false);
            }

            SecurityLog.SetupTokenReady(_logger, _setupToken.FilePath);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
