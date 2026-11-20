using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Api;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Hub;

/// <summary>
/// The channel of the command-line verbs <c>hub join</c>, <c>hub leave</c> and <c>hub status</c> to the running agent. It is a second local channel next to
/// the one of <c>emit</c> and <c>state</c> (<see cref="LocalChannel"/>), with its own secret, because those verbs may not be allowed to every process that
/// the agent starts: the secret of the first channel is in the environment of every command, and a command may run as a standard user, whereas joining a
/// hub gives the hub full control over the PC. The secret of this channel is only in <see cref="FileName"/> in the secrets directory, which only
/// administrators can read (PLAN.md S05, S09), and is never passed to a process. The reason that the verbs go through the running agent at all is that the
/// agent keeps the secret store in memory and rewrites it as a whole; a second process that wrote to it would be overwritten.
/// </summary>
public sealed class HubAdminChannel
{
    /// <summary>The file in the secrets directory with the address and the secret of the channel.</summary>
    public const string FileName = "hub-channel.json";

    /// <summary>The base path of the endpoints.</summary>
    public const string BasePath = "/local/v1/hub";

    /// <summary>The path of <c>POST</c> that joins a hub.</summary>
    public const string JoinPath = BasePath + "/join";

    /// <summary>The path of <c>POST</c> that leaves the hub.</summary>
    public const string LeavePath = BasePath + "/leave";

    /// <summary>The channel with a new random secret.</summary>
    public LocalChannel Channel { get; } = new();
}

/// <summary>Publishes the address of the loopback listener to the hub channel and keeps its file in step with the running agent, like <see cref="LocalChannelService"/>.</summary>
public sealed class HubAdminChannelService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServer _server;
    private readonly HubAdminChannel _channel;
    private readonly IAppPaths _paths;
    private readonly ILogger<HubAdminChannelService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="lifetime">The application lifetime.</param>
    /// <param name="server">The web server, for its addresses.</param>
    /// <param name="channel">The hub channel.</param>
    /// <param name="paths">The data directories.</param>
    /// <param name="logger">The logger.</param>
    public HubAdminChannelService(
        IHostApplicationLifetime lifetime,
        IServer server,
        HubAdminChannel channel,
        IAppPaths paths,
        ILogger<HubAdminChannelService> logger)
    {
        _lifetime = lifetime;
        _server = server;
        _channel = channel;
        _paths = paths;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_paths.SecretsDirectory, HubAdminChannel.FileName);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A file from a crashed run holds a secret that matches nothing; remove it before anyone reads it.
        LocalChannel.DeleteFile(FilePath);
        _lifetime.ApplicationStarted.Register(() => _ = PublishAsync());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        LocalChannel.DeleteFile(FilePath);
        return Task.CompletedTask;
    }

    private async Task PublishAsync()
    {
        IServerAddressesFeature? addresses = _server.Features.Get<IServerAddressesFeature>();
        Uri? loopback = addresses?.Addresses
            .Select(address => Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ? uri : null)
            .FirstOrDefault(uri => uri is { IsLoopback: true } && uri.Scheme == Uri.UriSchemeHttp);
        if (loopback is null)
        {
            return;
        }

        _channel.Channel.SetBaseAddress(loopback);
        try
        {
            string directory = _paths.SecretsDirectory;
            if (!Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(directory);
                }
                else
                {
                    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            await _channel.Channel.WriteFileAsync(FilePath, files: null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            HubLog.AdminChannelFileFailed(_logger, exception.Message);
        }
    }
}

/// <summary>
/// The endpoints behind <c>qoffeeautomat hub</c>: <c>GET /local/v1/hub</c>, <c>POST /local/v1/hub/join</c> and <c>POST /local/v1/hub/leave</c>, with the
/// secret of the <see cref="HubAdminChannel"/> as bearer token, from loopback only. They do the same as the management API (<see cref="HubEndpoints"/>) and
/// answer like the other local endpoints with <c>{"error":"..."}</c>; the success answer is the <see cref="HubLinkStatus"/>.
/// </summary>
public static class HubLocalEndpoints
{
    private const int MaxBodyBytes = 8 * 1024;

    /// <summary>Maps the endpoints.</summary>
    /// <param name="routes">The route builder.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(HubAdminChannel.BasePath, StatusAsync);
        routes.MapPost(HubAdminChannel.JoinPath, JoinAsync);
        routes.MapPost(HubAdminChannel.LeavePath, LeaveAsync);
    }

    private static IResult? Authorize(HttpContext context, HubAdminChannel channel)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return Error(StatusCodes.Status403Forbidden, "The hub channel accepts connections from this PC only.");
        }

        string? header = context.Request.Headers.Authorization;
        const string prefix = "Bearer ";
        string? token = header is not null && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
        if (!channel.Channel.Verify(token))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Error(StatusCodes.Status401Unauthorized, "A valid hub channel token is required.");
        }

        return null;
    }

    private static async Task<IResult> StatusAsync(HttpContext context, HubAdminChannel channel, HubLinkService service)
    {
        IResult? refusal = Authorize(context, channel);
        return refusal ?? Status(await service.GetStatusAsync(context.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> JoinAsync(HttpContext context, HubAdminChannel channel, HubLinkManager manager, HubLinkService service)
    {
        IResult? refusal = Authorize(context, channel);
        if (refusal is not null)
        {
            return refusal;
        }

        (HubJoinRequest? request, IResult? failure) = await ReadAsync(context, ApiJsonContext.Default.HubJoinRequest).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        if (string.IsNullOrWhiteSpace(request?.Enrollment))
        {
            return Error(StatusCodes.Status400BadRequest, "The body must be a JSON object with the member 'enrollment'.");
        }

        try
        {
            HubJoinResult result = await manager.JoinAsync(request.Enrollment, context.RequestAborted).ConfigureAwait(false);
            return result.Failure switch
            {
                HubJoinFailure.None => Status(await service.GetStatusAsync(context.RequestAborted).ConfigureAwait(false)),
                HubJoinFailure.Invalid => Error(StatusCodes.Status400BadRequest, result.Error ?? "The enrollment string is not valid."),
                HubJoinFailure.AlreadyJoined => Error(StatusCodes.Status409Conflict, result.Error ?? "This agent has joined a hub already."),
                HubJoinFailure.Refused => Error(StatusCodes.Status422UnprocessableEntity, result.Error ?? "The hub refused the enrollment."),
                _ => Error(StatusCodes.Status502BadGateway, result.Error ?? "The hub could not be reached."),
            };
        }
        catch (SecretStoreException exception)
        {
            return Error(StatusCodes.Status500InternalServerError, exception.Message);
        }
    }

    private static async Task<IResult> LeaveAsync(HttpContext context, HubAdminChannel channel, HubLinkManager manager, HubLinkService service)
    {
        IResult? refusal = Authorize(context, channel);
        if (refusal is not null)
        {
            return refusal;
        }

        (HubLeaveRequest? request, IResult? failure) = await ReadAsync(context, ApiJsonContext.Default.HubLeaveRequest).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        string? documents = request?.Documents;
        if (documents is not (null or HubLinkManager.DocumentsConvert or HubLinkManager.DocumentsRemove))
        {
            return Error(StatusCodes.Status400BadRequest, "documents must be 'convert' or 'remove'.");
        }

        try
        {
            _ = await manager.LeaveAsync(documents, context.RequestAborted).ConfigureAwait(false);
            return Status(await service.GetStatusAsync(context.RequestAborted).ConfigureAwait(false));
        }
        catch (SecretStoreException exception)
        {
            return Error(StatusCodes.Status500InternalServerError, exception.Message);
        }
    }

    /// <summary>Reads an optional JSON body of at most <see cref="MaxBodyBytes"/>; an empty body gives null.</summary>
    private static async Task<(T? Value, IResult? Failure)> ReadAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return (null, Error(StatusCodes.Status413PayloadTooLarge, "The request is too large."));
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes)
            {
                return (null, Error(StatusCodes.Status413PayloadTooLarge, "The request is too large."));
            }
        }

        if (buffer.Length == 0)
        {
            return (null, null);
        }

        try
        {
            return (JsonSerializer.Deserialize(buffer.ToArray(), typeInfo), null);
        }
        catch (JsonException)
        {
            return (null, Error(StatusCodes.Status400BadRequest, "The body is not valid JSON."));
        }
    }

    private static IResult Status(HubLinkStatus status)
    {
        return ApiResults.Json(status, ApiJsonContext.Default.HubLinkStatus);
    }

    private static IResult Error(int statusCode, string message)
    {
        return Results.Text("{\"error\":\"" + JsonEncodedText.Encode(message) + "\"}", "application/json", Encoding.UTF8, statusCode);
    }
}
