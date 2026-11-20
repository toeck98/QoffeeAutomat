using Microsoft.Extensions.Hosting;
using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Hub;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Api;

/// <summary>Remembers when the agent started, for the uptime in <c>/status</c>. It is a hosted service so that the time is taken when the host starts, not when the first request arrives.</summary>
public sealed class AgentUptime : IHostedService
{
    private readonly TimeProvider _time;
    private DateTimeOffset? _startedAt;

    /// <summary>Creates the service.</summary>
    /// <param name="time">The clock.</param>
    public AgentUptime(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>When the host started; the time of the first read when the service was not started (a unit test without host).</summary>
    public DateTimeOffset StartedAt => _startedAt ??= _time.GetUtcNow();

    /// <summary>How long the agent has been running.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            TimeSpan elapsed = _time.GetUtcNow() - StartedAt;
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startedAt = _time.GetUtcNow();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>The <c>/settings</c> and <c>/status</c> endpoints (PLAN.md S08).</summary>
public static class SettingsAndStatusEndpoints
{
    /// <summary>The largest settings document that is read.</summary>
    public const int MaxBodyBytes = 16 * 1024;

    /// <summary>Maps the endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/status", GetStatusAsync)
            .Describe("Status", "getStatus", "Reports the version, uptime, platform, link clients, session state and the pause flag.")
            .Returns<AgentStatus>();
        api.MapGet("/hotkeys", GetHotkeys)
            .Describe("Settings", "getHotkeyStatus", "Reports which global hotkeys of the settings are registered and which conflict with other programs.")
            .Returns<HotkeyStatusResponse>();
        api.MapGet("/hardware-sensors", GetHardwareSensors)
            .Describe("Settings", "getHardwareSensorStatus", "Reports whether the optional hardware sensors (temperatures, fans, power draw) run and whether the kernel driver that they need is installed.")
            .Returns<HardwareSensorStatus>();
        api.MapGet("/settings", GetSettings)
            .Describe("Settings", "getSettings", "Reads the agent settings; the ETag is their revision.")
            .Returns<AgentSettingsDocument>();
        api.MapPut("/settings", PutSettingsAsync)
            .Describe("Settings", "putSettings", "Replaces the agent settings. Ports and remote UI take effect after a restart.")
            .Takes<AgentSettingsDocument>()
            .Returns<AgentSettingsDocument>()
            .HonoursIfMatch()
            .Fails(StatusCodes.Status400BadRequest, ApiResults.UnprocessableContent);
    }

    private static async Task<IResult> GetStatusAsync(
        AgentIdentity identity,
        AgentUptime uptime,
        IAgentPause pause,
        ISessionBridge session,
        PairingService pairing,
        LinkServer link,
        CommandEngine engine,
        ManifestService manifest,
        IConfigStore config,
        PlatformSelection platform,
        IPlatformInfo info,
        HubLinkService hub,
        CancellationToken cancellationToken)
    {
        AgentInfo agent = identity.Current();
        SessionState state = session.State;
        IReadOnlyList<LinkClient> clients = await pairing.ListClientsAsync(cancellationToken).ConfigureAwait(false);

        var status = new AgentStatus(
            AgentVersion.Current,
            agent.Id,
            agent.Name,
            uptime.StartedAt,
            (long)uptime.Elapsed.TotalSeconds,
            new PlatformStatus(platform.Kind.ToString().ToLowerInvariant(), info.Os.Name, info.Os.Version, info.Os.Architecture, info.Hostname),
            new SessionStatus(state.IsReady, state.UserName, state.IsLocked, state.IsUserAdministrator),
            new LinkStatus(identity.LinkPort, clients.Count, link.ConnectionCount),
            pause.IsPaused,
            engine.ActiveRuns.Count,
            manifest.Current.Rev,
            config.Problems.Count,
            await hub.GetStatusAsync(cancellationToken).ConfigureAwait(false));
        return ApiResults.Json(status, ApiJsonContext.Default.AgentStatus);
    }

    private static IResult GetHotkeys(IHotkeyStatusSource hotkeys)
    {
        HotkeyStatusEntry[] entries =
        [
            .. hotkeys.GetStatus().Select(status => new HotkeyStatusEntry(status.Name, status.Keys, status.State, status.Message)),
        ];
        return ApiResults.Json(new HotkeyStatusResponse(hotkeys.IsSupported, entries), ApiJsonContext.Default.HotkeyStatusResponse);
    }

    private static IResult GetHardwareSensors(IHardwareSensorStatusSource sensors)
    {
        return ApiResults.Json(sensors.GetStatus(), ApiJsonContext.Default.HardwareSensorStatus);
    }

    private static IResult GetSettings(IConfigStore config, HttpContext context)
    {
        AgentSettingsDocument? settings = config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings);
        ApiETag.Set(context.Response, ApiETag.ForRevision(settings?.Revision));
        return ApiResults.Json(
            settings ?? new AgentSettingsDocument(DocumentSchema.CurrentVersion, Environment.MachineName),
            ApiDocumentJsonContext.Default.AgentSettingsDocument);
    }

    private static async Task<IResult> PutSettingsAsync(HttpContext context, IConfigStore config, CancellationToken cancellationToken)
    {
        IResult? header = ApiETag.ReadRevision(context.Request, out int? expected);
        if (header is not null)
        {
            return header;
        }

        (AgentSettingsDocument? settings, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiDocumentJsonContext.Default.AgentSettingsDocument, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            return failure!;
        }

        SaveResult<AgentSettingsDocument> saved = await config.Local.SaveAsync(settings, expected, cancellationToken).ConfigureAwait(false);
        return ApiDocuments.SaveOutcome(saved, ApiDocumentJsonContext.Default.AgentSettingsDocument, context, created: false, "/api/v1/settings");
    }
}
