using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Gateway;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Tunnel;

/// <summary>The codes of the problem responses that the tunnel proxy adds to those of the hub API (T912).</summary>
public static class TunnelProblemCodes
{
    /// <summary>The management tunnel is switched off on the agent (or the agent does not offer it). Status 403.</summary>
    public const string TunnelDisabled = "tunnel_disabled";
}

/// <summary>
/// The tunnel proxy of the hub (PLAN.md S20, T912): <c>/api/v1/agents/{id}/agent-api/{rest}</c> forwards a call to <c>/api/v1/{rest}</c> of the management API of a connected
/// agent, over the agent link (<c>mgmt_request</c>), for a signed-in hub user. A GET that accepts <c>text/event-stream</c> opens the agent's event stream
/// (<c>mgmt_stream_open</c>) and the hub streams the events to the browser while the agent sends them (live output of a run, live metric values).
/// <list type="bullet">
/// <item>Hub roles map one to one to the roles of the tunnel (<see cref="TunnelRoles"/>). The hub requires <c>viewer</c> for GET and <c>operator</c> for every other method;
/// the role rules (<see cref="TunnelAccess"/>) are applied here to save the round trip and again on the agent, which has the last word. The agent's own HTTP answer, a 403
/// of a viewer for example, is passed on unchanged.</item>
/// <item>An agent that is not connected answers 503 <c>agent_offline</c>; an agent whose tunnel is switched off answers 403 <c>tunnel_disabled</c>, with the reason in the
/// message so that the UI can show it; no answer in time is 504 <c>timeout</c>; other failures of the link are 502.</item>
/// <item>Every call that changes state is written to the audit log of the hub by the audit middleware, with the user, the role and the agent; this endpoint adds the target
/// (<c>agent:ID:tunnel:METHOD /api/v1/...</c>). Reads and streams leave a line in the hub's log with the same facts. The agent writes every tunnelled call to its own log.</item>
/// </list>
/// </summary>
public static class TunnelEndpoints
{
    /// <summary>The route below <c>/api/v1</c>.</summary>
    public const string Route = "/agents/{id}/agent-api/{**rest}";

    /// <summary>The largest request body that is forwarded; it has to fit into one frame with the envelope.</summary>
    public const int MaxBodyBytes = 900 * 1024;

    /// <summary>How long a stream may be quiet before the hub writes a comment line, so that proxies and browsers keep the connection.</summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private const int MaxUserLength = 128;
    private const string EventStreamMedia = "text/event-stream";
    private const string Tag = "Tunnel";

    /// <summary>Maps the proxy for GET (viewer) and for POST, PUT, PATCH and DELETE (operator).</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet(Route, ProxyAsync)
            .Describe(Tag, "tunnelGet", "Reads from the management API of a connected agent: GET /api/v1/{rest} on the agent. With Accept: text/event-stream the agent's event stream is forwarded as it arrives.")
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status502BadGateway, StatusCodes.Status503ServiceUnavailable, StatusCodes.Status504GatewayTimeout);
        api.MapPost(Route, ProxyAsync).RequireAuthorization(HubPolicies.Operator)
            .Describe(Tag, "tunnelPost", "Calls POST /api/v1/{rest} of a connected agent. Operators may start runs; everything else needs an administrator. The agent decides.")
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status502BadGateway, StatusCodes.Status503ServiceUnavailable, StatusCodes.Status504GatewayTimeout);
        api.MapPut(Route, ProxyAsync).RequireAuthorization(HubPolicies.Operator)
            .Describe(Tag, "tunnelPut", "Calls PUT /api/v1/{rest} of a connected agent; administrators only, decided by the agent.")
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status502BadGateway, StatusCodes.Status503ServiceUnavailable, StatusCodes.Status504GatewayTimeout);
        api.MapPatch(Route, ProxyAsync).RequireAuthorization(HubPolicies.Operator)
            .Describe(Tag, "tunnelPatch", "Calls PATCH /api/v1/{rest} of a connected agent; administrators only, decided by the agent.")
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status502BadGateway, StatusCodes.Status503ServiceUnavailable, StatusCodes.Status504GatewayTimeout);
        api.MapDelete(Route, ProxyAsync).RequireAuthorization(HubPolicies.Operator)
            .Describe(Tag, "tunnelDelete", "Calls DELETE /api/v1/{rest} of a connected agent; administrators only, decided by the agent.")
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound, StatusCodes.Status502BadGateway, StatusCodes.Status503ServiceUnavailable, StatusCodes.Status504GatewayTimeout);
    }

    private static async Task<IResult> ProxyAsync(
        string id,
        string? rest,
        HttpContext context,
        AgentRegistry registry,
        IAgentGateway gateway,
        IAgentRequestChannel requests,
        IAgentStreamChannel streams,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggers.CreateLogger("QoffeeAutomat.Hub.Tunnel");
        string method = context.Request.Method.ToUpperInvariant();
        string path = TunnelAccess.ApiPrefix + rest;
        AuditContext audit = context.Audit();
        audit.Target = "agent:" + id;
        audit.Agents.Add(id);

        AgentRecord? agent = await registry.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            return HubApiResults.NotFound(HubErrorCodes.UnknownAgent, "There is no such agent.");
        }

        string? role = HubPrincipal.Role(context.User);
        string user = Truncate(HubPrincipal.Name(context.User) ?? "unknown");
        if (!TunnelAccess.IsRole(role))
        {
            return HubApiResults.Problem(StatusCodes.Status403Forbidden, HubErrorCodes.Forbidden, "Your role cannot use the management tunnel.");
        }

        if (!TunnelAccess.IsValidPath(path, out string? pathError))
        {
            return HubApiResults.Problem(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, pathError ?? "The path is not valid.");
        }

        audit.Target = "agent:" + id + ":tunnel:" + method + " " + path;
        TunnelDecision decision = TunnelAccess.Check(role, method, path);
        if (!decision.Allowed)
        {
            TunnelLog.Refused(logger, user, role!, id, method, path, decision.Message ?? "refused");
            return HubApiResults.Problem(decision.Status, decision.Code ?? HubErrorCodes.Forbidden, decision.Message ?? "The request is not allowed.");
        }

        AgentPresence presence = gateway.GetPresence(id);
        if (!presence.Online)
        {
            return Failure(HubErrorCodes.AgentOffline, string.Empty);
        }

        if (!presence.Features.Contains(HubFeatures.MgmtTunnel, StringComparer.Ordinal))
        {
            // The agent did not announce the feature: it is switched off there (or this agent is too old to have it).
            return Failure(HubErrorCodes.Forbidden, string.Empty);
        }

        var principal = new TunnelPrincipal(user, role!);
        Dictionary<string, string>? query = QueryOf(context.Request);
        if (method == "GET" && AcceptsEventStream(context.Request))
        {
            return await StreamAsync(context, streams, presence, id, path, query, principal, logger, cancellationToken).ConfigureAwait(false);
        }

        (JsonElement? body, IResult? invalidBody) = await ReadBodyAsync(context.Request, method, cancellationToken).ConfigureAwait(false);
        if (invalidBody is not null)
        {
            return invalidBody;
        }

        AgentRequestOutcome outcome = await requests.RequestAsync(
            id,
            presence.ConnectionId,
            requestId => JsonSerializer.SerializeToUtf8Bytes(
                new MgmtRequestMessage(requestId, method, path, principal, query, body),
                HubJsonContext.Default.MgmtRequestMessage),
            cancellationToken).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            TunnelLog.Failed(logger, id, method, path, outcome.ErrorCode ?? ErrorCodes.Internal);
            return Failure(outcome.ErrorCode ?? ErrorCodes.Internal, outcome.ErrorMessage ?? string.Empty);
        }

        MgmtRequestResultData? data = null;
        try
        {
            data = outcome.Data?.Deserialize(HubJsonContext.Default.MgmtRequestResultData);
        }
        catch (JsonException)
        {
            // Answered below.
        }

        if (data is null || data.Status is < 100 or > 599)
        {
            return HubApiResults.Problem(StatusCodes.Status502BadGateway, "agent_error", "The agent answered with a result that is not valid.");
        }

        TunnelLog.Request(logger, user, role!, id, method, path, data.Status);
        return Relay(data);
    }

    // ------------------------------------------------------------------
    // Streams
    // ------------------------------------------------------------------

    private static async Task<IResult> StreamAsync(
        HttpContext context,
        IAgentStreamChannel streams,
        AgentPresence presence,
        string agentId,
        string path,
        Dictionary<string, string>? query,
        TunnelPrincipal principal,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        AgentStreamOutcome opened = await streams.OpenStreamAsync(agentId, presence.ConnectionId, path, query, principal, cancellationToken).ConfigureAwait(false);
        if (opened.Stream is not { } stream)
        {
            TunnelLog.Failed(logger, agentId, "GET", path, opened.ErrorCode ?? ErrorCodes.Internal);
            return Failure(opened.ErrorCode ?? ErrorCodes.Internal, opened.ErrorMessage ?? string.Empty);
        }

        TunnelLog.StreamOpened(logger, principal.User, principal.Role, agentId, path);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                await WriteStreamAsync(context, stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
                // The browser went away; disposing the stream tells the agent to close it.
            }
        }

        TunnelLog.StreamEnded(logger, principal.User, agentId, path, stream.EndReason ?? StreamEndReasons.Closed);
        return Results.Empty;
    }

    private static async Task WriteStreamAsync(HttpContext context, AgentStreamRelay stream, CancellationToken cancellationToken)
    {
        HttpResponse response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = EventStreamMedia;
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await response.StartAsync(cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(response, ": connected\n\n", cancellationToken).ConfigureAwait(false);

        while (true)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(KeepAliveInterval);
            bool more;
            try
            {
                more = await stream.Events.WaitToReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Quiet for a while: a comment line that the client ignores.
                await WriteAsync(response, ": keep-alive\n\n", cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!more)
            {
                break;
            }

            while (stream.Events.TryRead(out AgentStreamEvent? item))
            {
                await WriteEventAsync(response, item.Event, item.Data, cancellationToken).ConfigureAwait(false);
            }
        }

        // The last event tells the client why there are no more: the end of the endpoint, or a loss of the agent.
        string reason = stream.EndReason ?? StreamEndReasons.Completed;
        // The reasons are the constants of StreamEndReasons: plain words that need no escaping.
        await WriteEventAsync(response, "tunnel_closed", "{\"reason\":\"" + reason + "\"}", cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteEventAsync(HttpResponse response, string name, string data, CancellationToken cancellationToken)
    {
        var text = new StringBuilder("event: ").Append(Clean(name)).Append('\n');
        foreach (string line in data.Split('\n'))
        {
            _ = text.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
        }

        _ = text.Append('\n');
        return WriteAsync(response, text.ToString(), cancellationToken);
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken cancellationToken)
    {
        await response.WriteAsync(text, cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An event name that was sent by an agent ends up in a line of the stream; line breaks would let it add fields.</summary>
    private static string Clean(string name)
    {
        var text = new StringBuilder();
        foreach (char c in name)
        {
            _ = text.Append(char.IsControl(c) ? '_' : c);
        }

        return text.Length == 0 ? "message" : text.ToString();
    }

    // ------------------------------------------------------------------
    // Requests and answers
    // ------------------------------------------------------------------

    private static bool AcceptsEventStream(HttpRequest request)
    {
        foreach (string? accept in request.Headers.Accept)
        {
            if (accept is not null && accept.Contains(EventStreamMedia, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string>? QueryOf(HttpRequest request)
    {
        if (request.Query.Count == 0)
        {
            return null;
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> pair in request.Query)
        {
            query[pair.Key] = pair.Value.ToString();
        }

        return query;
    }

    /// <summary>Reads the body of a request that has one and parses it as JSON; a GET carries none to the agent.</summary>
    private static async Task<(JsonElement? Body, IResult? Failure)> ReadBodyAsync(HttpRequest request, string method, CancellationToken cancellationToken)
    {
        if (method == "GET")
        {
            return (null, null);
        }

        if (request.ContentLength > MaxBodyBytes)
        {
            return (null, TooLarge());
        }

        IHttpMaxRequestBodySizeFeature? limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBodyBytes)
                {
                    return (null, TooLarge());
                }
            }
        }
        catch (BadHttpRequestException)
        {
            return (null, TooLarge());
        }

        if (buffer.Length == 0)
        {
            return (null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
            return (document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (null, HubApiResults.Problem(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "The request body of a tunnelled call must be JSON."));
        }
    }

    private static IResult TooLarge() => HubApiResults.Problem(StatusCodes.Status413PayloadTooLarge, "payload_too_large", "The request body is too large for the management tunnel.");

    /// <summary>Passes the answer of the agent's API on: its status and its JSON body. The agent's errors are problem details; they keep that media type.</summary>
    private static IResult Relay(MgmtRequestResultData data)
    {
        if (data.Body is not { } body || data.Status is StatusCodes.Status204NoContent or StatusCodes.Status304NotModified)
        {
            return Results.StatusCode(data.Status);
        }

        return Results.Text(
            body.GetRawText(),
            data.Status >= StatusCodes.Status400BadRequest ? HubApiResults.ProblemContentType : "application/json",
            statusCode: data.Status);
    }

    /// <summary>
    /// Turns the failure of a tunnel call into a problem response. A message that begins with a status and a colon (<c>404: There is no run with this id.</c>) comes from an
    /// agent whose endpoint refused to open a stream; the status is restored.
    /// </summary>
    internal static IResult Failure(string code, string message)
    {
        int? hinted = null;
        string detail = message;
        if (message.Length > 5 && message[3] == ':' && int.TryParse(message.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int status) && status is >= 400 and <= 599)
        {
            hinted = status;
            detail = message[4..].TrimStart();
        }

        switch (code)
        {
            case HubErrorCodes.AgentOffline:
                return HubApiResults.Problem(StatusCodes.Status503ServiceUnavailable, HubErrorCodes.AgentOffline, "The agent is not connected to the hub, so its management API cannot be reached.");
            case HubErrorCodes.Forbidden when hinted is null:
                return HubApiResults.Problem(StatusCodes.Status403Forbidden, TunnelProblemCodes.TunnelDisabled, "The management tunnel is switched off on this agent. Switch it on in the hub settings of the agent to manage it from the hub.");
            case HubErrorCodes.Forbidden:
                return HubApiResults.Problem(hinted!.Value, HubErrorCodes.Forbidden, detail);
            case ErrorCodes.Timeout:
                return HubApiResults.Problem(StatusCodes.Status504GatewayTimeout, ErrorCodes.Timeout, "The agent did not answer in time.");
            case ErrorCodes.Busy:
                return HubApiResults.Problem(StatusCodes.Status503ServiceUnavailable, ErrorCodes.Busy, "Too many tunnelled requests are in progress for this agent. Try again in a moment.");
            default:
                int resolved = hinted ?? (code == ErrorCodes.BadRequest ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway);
                string resolvedCode = resolved switch
                {
                    StatusCodes.Status404NotFound => "not_found",
                    StatusCodes.Status409Conflict => "conflict",
                    >= 400 and < 500 => ErrorCodes.BadRequest,
                    _ => "agent_error",
                };
                return HubApiResults.Problem(resolved, resolvedCode, detail.Length > 0 ? detail : "The agent could not handle the request.");
        }
    }

    private static string Truncate(string user) => user.Length <= MaxUserLength ? user : user[..MaxUserLength];
}
