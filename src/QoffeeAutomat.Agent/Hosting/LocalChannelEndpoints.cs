using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// The endpoint behind <c>qoffeeautomat emit</c> (PLAN.md T217): <c>POST /local/v1/emit</c> with a
/// bearer token that only processes started by the agent (and administrators who can read the secrets
/// directory) know. It accepts connections from loopback only and does nothing but publish a
/// <see cref="EventTypes.Custom"/> event, so even a leaked token grants no more than that. It is
/// separate from the management API, whose authentication (D-017) is the password session.
/// </summary>
public static class LocalChannelEndpoints
{
    /// <summary>The largest request body that is read.</summary>
    public const int MaxBodyBytes = 32 * 1024;

    /// <summary>Maps the endpoint.</summary>
    /// <param name="routes">The route builder.</param>
    /// <returns>The builder of the endpoint.</returns>
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        return routes.MapPost(LocalChannelNames.EmitPath, EmitAsync);
    }

    private static async Task<IResult> EmitAsync(HttpContext context, LocalChannel channel, IEventBus bus)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return Error(StatusCodes.Status403Forbidden, "The local channel accepts connections from this PC only.");
        }

        if (!channel.Verify(BearerToken(context.Request)))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Error(StatusCodes.Status401Unauthorized, "A valid local channel token is required.");
        }

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large.");
        }

        IHttpMaxRequestBodySizeFeature? limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        byte[] body;
        try
        {
            body = await ReadBodyAsync(context.Request.Body, context.RequestAborted);
        }
        catch (BadHttpRequestException)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large.");
        }

        if (body.Length > MaxBodyBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "The request is too large.");
        }

        string? name;
        JsonElement? data = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out JsonElement nameElement)
                || nameElement.ValueKind != JsonValueKind.String)
            {
                return Error(StatusCodes.Status400BadRequest, "The body must be a JSON object with a string member 'name'.");
            }

            name = nameElement.GetString();
            if (root.TryGetProperty("data", out JsonElement dataElement))
            {
                data = dataElement.Clone();
            }
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "The body is not valid JSON.");
        }

        CustomEventResult result = CustomEvents.Create(name, data);
        if (!result.IsValid)
        {
            return Error(StatusCodes.Status400BadRequest, result.Error!);
        }

        AgentEvent published = await bus.PublishAsync(EventTypes.Custom, result.Payload, context.RequestAborted);
        return Results.Text("{\"id\":" + published.Id.ToString(CultureInfo.InvariantCulture) + "}", "application/json");
    }

    private static string? BearerToken(HttpRequest request)
    {
        string? header = request.Headers.Authorization;
        const string prefix = "Bearer ";
        return header is not null && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    private static async Task<byte[]> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes)
            {
                break;
            }
        }

        return buffer.ToArray();
    }

    private static IResult Error(int statusCode, string message)
    {
        return Results.Text("{\"error\":\"" + JsonEncodedText.Encode(message) + "\"}", "application/json", statusCode: statusCode);
    }
}

/// <summary>Publishes the address of the loopback listener to the local channel and keeps the channel file in step with the running agent.</summary>
public sealed class LocalChannelService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServer _server;
    private readonly LocalChannel _channel;
    private readonly IAppPaths _paths;
    private readonly ILogger<LocalChannelService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="lifetime">The application lifetime.</param>
    /// <param name="server">The web server, for its addresses.</param>
    /// <param name="channel">The local channel.</param>
    /// <param name="paths">The data directories.</param>
    /// <param name="logger">The logger.</param>
    public LocalChannelService(
        IHostApplicationLifetime lifetime,
        IServer server,
        LocalChannel channel,
        IAppPaths paths,
        ILogger<LocalChannelService> logger)
    {
        _lifetime = lifetime;
        _server = server;
        _channel = channel;
        _paths = paths;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_paths.SecretsDirectory, LocalChannelNames.FileName);

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

        _channel.SetBaseAddress(loopback);
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

            await _channel.WriteFileAsync(FilePath, files: null, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AgentLog.LocalChannelFileFailed(_logger, exception.Message);
        }
    }
}
