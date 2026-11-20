using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Config.Rollout;
using QoffeeAutomat.Hub.Enrollment;
using QoffeeAutomat.Hub.Fleet;
using QoffeeAutomat.Hub.Fleet.Actions;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Metrics.Prometheus;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Hosting;

/// <summary>
/// Builds the hub host on the ASP.NET Core minimal host (PLAN.md S04, S20, T902): structured logging, the data directory, the database
/// with its migrations, the identity, the TLS certificate and the signing key, the Kestrel listener of S20, trusted proxies,
/// <c>/healthz</c>, graceful shutdown, and the security and API layer of T903. Everything that depends on the data directory or the settings is
/// created lazily, when the first service needs it, so a test host can replace <see cref="HubSettings"/> through the service collection.
/// </summary>
public static class HubHost
{
    /// <summary>The unauthenticated liveness endpoint (PLAN.md S20).</summary>
    public const string HealthPath = "/healthz";

    /// <summary>The time the host waits for work to finish after a stop request.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Builds the host without starting it.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="configureServices">Hook for tests to add or replace services; runs after the hub's own registrations.</param>
    /// <returns>The application, ready for <c>RunAsync</c>.</returns>
    public static WebApplication Build(HubOptions options, Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Options the hub does not know (--environment, --contentRoot and so on) belong to ASP.NET Core.
        string[] hostArguments = [.. options.PassThrough];
        WebApplicationBuilder builder = WebApplication.CreateBuilder(hostArguments);
        ConfigureLogging(builder.Logging, HubSettings.ResolveLogFormat(options, Environment.GetEnvironmentVariable));
        ConfigureServices(builder.Services, options);
        configureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();

        // Ingress gate first: only the Supervisor may talk to the ingress listener, before any forwarded-header rewrite.
        app.UseMiddleware<IngressGateMiddleware>();

        // Forwarded headers next: everything after it (the attempt limits, the audit log, the Secure flag of the cookie) sees the client
        // address and the scheme that the trusted proxy reports, and nobody else can choose them.
        app.UseMiddleware<TrustedProxyMiddleware>();
        app.Use(AddResponseHeaders);

        // The hub UI (T915): static files need neither the audit log nor a session, so they are answered before both.
        HubUiHosting.UseFiles(app);

        // The agent link (T906) is a WebSocket: the keep-alive ping of the hub and the time without traffic after which an agent is dropped (S07).
        app.UseWebSockets(AgentLinkHandler.CreateWebSocketOptions(app.Services.GetRequiredService<GatewayOptions>()));

        // The audit log wraps authentication and authorization, so a refused request leaves an entry too.
        app.UseMiddleware<AuditMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet(HealthPath, () => Results.Text(
            $"{{\"status\":\"ok\",\"version\":\"{HubVersion.Current}\"}}",
            "application/json"));
        HubApi.MapEndpoints(app);

        // The Prometheus export (T911): an API token authenticates it, not a session.
        PrometheusEndpoint.Map(app);

        // The endpoints that agents call (T904): the enrollment token and the agent credential authenticate them, not a session.
        EnrollEndpoint.Map(app);
        AgentLinkEndpoint.Map(app);

        // The endpoints that Home Assistant calls (T913): the pairing code and the fleet token authenticate them, not a session.
        PairEndpoint.Map(app);
        FleetLinkEndpoint.Map(app);

        // Client routes of the single-page UI answer with index.html; mapped last, it has the lowest order and never takes a route above (T915).
        HubUiHosting.MapFallback(app);
        return app;
    }

    private static Task AddResponseHeaders(HttpContext context, RequestDelegate next)
    {
        // Answers of the API carry credentials or personal data; browsers and proxies must not keep them.
        IHeaderDictionary headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        if (context.Request.Path.StartsWithSegments(HubApi.BasePath, StringComparison.Ordinal)
            || context.Request.Path.StartsWithSegments(HubOpenApiDocument.Path, StringComparison.Ordinal))
        {
            headers.CacheControl = "no-store";
        }

        return next(context);
    }

    private static void ConfigureLogging(ILoggingBuilder logging, HubLogFormat format)
    {
        logging.ClearProviders();
        if (format == HubLogFormat.Json)
        {
            // One JSON object per line with the named values of the message as fields: for log collectors and `docker logs`.
            logging.AddJsonConsole(console =>
            {
                console.IncludeScopes = true;
                console.UseUtcTimestamp = true;
                console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
            });
            return;
        }

        logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });
    }

    private static void ConfigureServices(IServiceCollection services, HubOptions options)
    {
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => HubSettings.Resolve(
            options,
            Environment.GetEnvironmentVariable,
            HubSettings.IsRunningInContainer(Environment.GetEnvironmentVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

        services.Configure<HostOptions>(host => host.ShutdownTimeout = ShutdownTimeout);

        // Storage (T902): the directory first, then the database and the key file below it.
        services.AddSingleton<HubDataDirectory>();
        services.AddSingleton(provider => HubDatabase.Open(provider.GetRequiredService<HubDataDirectory>().Paths.DatabasePath));
        services.AddSingleton(provider => HubIdentityStore.LoadOrCreate(
            provider.GetRequiredService<HubDatabase>(),
            provider.GetRequiredService<HubSettings>().InitialName,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new FileKeyProtector(provider.GetRequiredService<HubDataDirectory>().Paths.SecretKeyPath));
        services.AddSingleton<ISecretStore>(provider => new SecretStore(
            provider.GetRequiredService<HubDataDirectory>().Paths.SecretsFilePath,
            provider.GetRequiredService<FileKeyProtector>()));

        // Keys and the listener (T902).
        services.AddSingleton<HubSigningKeyStore>();
        services.AddSingleton(provider => new HubCertificateProvider(
            provider.GetRequiredService<HubSettings>(),
            provider.GetRequiredService<ISecretStore>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IConfigureOptions<KestrelServerOptions>, HubListenerSetup>();
        services.AddHostedService<HubStartupService>();

        // Users, roles, tokens, audit log (T903) and the published OpenAPI document.
        services.AddHubSecurity();

        // The gateway first: enrollment only registers placeholders for the notifier and the link handler when nothing else is there (T904, T906).
        services.AddHubGateway();
        services.AddHubEnrollment();
        services.AddHubFleet();
        services.AddHubFleetActions();
        services.AddHubConfig();
        services.AddHubRollout();
        services.AddHubMetrics();
        services.AddSingleton<HubOpenApiProvider>();
    }
}
