using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
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
using QoffeeAutomat.Core.Metrics.Custom;

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

        // The values of state sensors (T714): the verbs 'state set' and 'state get' of the command line.
        routes.MapPut(LocalChannelNames.StatePath + "/{id}", SetStateAsync);
        routes.MapGet(LocalChannelNames.StatePath + "/{id}", GetState);
        return routes.MapPost(LocalChannelNames.EmitPath, EmitAsync);
    }

    /// <summary>Applies the rules of the local channel to a request: loopback callers only, and the secret in the bearer header.</summary>
    /// <returns>The refusal, or null when the caller may use the channel.</returns>
    private static IResult? Authorize(HttpContext context, LocalChannel channel)
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

        return null;
    }

    /// <summary>Reads the request body up to <see cref="MaxBodyBytes"/>.</summary>
    /// <returns>The body, or the refusal when it is too large.</returns>
    private static async Task<(byte[]? Body, IResult? Failure)> ReadLimitedBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return (null, Error(StatusCodes.Status413PayloadTooLarge, "The request is too large."));
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
            return (null, Error(StatusCodes.Status413PayloadTooLarge, "The request is too large."));
        }

        return body.Length > MaxBodyBytes
            ? (null, Error(StatusCodes.Status413PayloadTooLarge, "The request is too large."))
            : (body, null);
    }

    private static async Task<IResult> SetStateAsync(string id, HttpContext context, LocalChannel channel, CustomSensorProvider sensors)
    {
        IResult? refusal = Authorize(context, channel);
        if (refusal is not null)
        {
            return refusal;
        }

        (byte[]? body, IResult? failure) = await ReadLimitedBodyAsync(context);
        if (body is null)
        {
            return failure!;
        }

        // The command line sends the value as text; a script that calls the endpoint directly may send a number or a boolean.
        string? text;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out JsonElement value))
            {
                return Error(StatusCodes.Status400BadRequest, "The body must be a JSON object with the member 'value'.");
            }

            text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                _ => null,
            };
            if (text is null)
            {
                return Error(StatusCodes.Status400BadRequest, "'value' must be a string, a number or a boolean.");
            }
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "The body is not valid JSON.");
        }

        StateSetResult result = await sensors.States.SetTextAsync(id, text, context.RequestAborted);
        return result.IsSuccess
            ? StateBody(id, result.Value, updatedAt: null)
            : StateFailure(result.ErrorCode!, result.Message!);
    }

    private static IResult GetState(string id, HttpContext context, LocalChannel channel, CustomSensorProvider sensors)
    {
        IResult? refusal = Authorize(context, channel);
        if (refusal is not null)
        {
            return refusal;
        }

        StateGetResult result = sensors.States.Get(id);
        return result.IsSuccess
            ? StateBody(id, result.Reading!.Value, result.Reading.UpdatedAt)
            : StateFailure(result.ErrorCode!, result.Message!);
    }

    /// <summary>The answer for a value: <c>{"id":"...","value":...}</c>, with <c>updatedAt</c> when the value was set; <c>value</c> is null for a sensor that has none.</summary>
    private static IResult StateBody(string id, object? value, DateTimeOffset? updatedAt)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WritePropertyName("value");
            switch (value)
            {
                case double number:
                    writer.WriteNumberValue(number);
                    break;
                case bool flag:
                    writer.WriteBooleanValue(flag);
                    break;
                case DateTimeOffset moment:
                    writer.WriteStringValue(moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                    break;
                case string text:
                    writer.WriteStringValue(text);
                    break;
                default:
                    writer.WriteNullValue();
                    break;
            }

            if (updatedAt is { } at)
            {
                writer.WriteString("updatedAt", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            }

            writer.WriteEndObject();
        }

        return Results.Text(Encoding.UTF8.GetString(buffer.WrittenSpan), "application/json");
    }

    /// <summary>Maps the reason that a state value was refused to an HTTP status: unknown sensor 404, sensor of another type 409, invalid value 400, a value that could not be saved 500.</summary>
    private static IResult StateFailure(string errorCode, string message)
    {
        int status = errorCode switch
        {
            StateSensorErrors.UnknownSensor => StatusCodes.Status404NotFound,
            StateSensorErrors.NotStateSensor => StatusCodes.Status409Conflict,
            StateSensorErrors.PersistFailed => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status400BadRequest,
        };
        return Error(status, message);
    }

    private static async Task<IResult> EmitAsync(HttpContext context, LocalChannel channel, IEventBus bus)
    {
        IResult? refusal = Authorize(context, channel);
        if (refusal is not null)
        {
            return refusal;
        }

        (byte[]? body, IResult? tooLarge) = await ReadLimitedBodyAsync(context);
        if (body is null)
        {
            return tooLarge!;
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
