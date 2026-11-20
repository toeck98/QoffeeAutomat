using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Fleet;

/// <summary>
/// <c>POST /hub/v1/pair</c> (PLAN.md S07 pairing, S20 "Fleet link", T913): Home Assistant exchanges the pairing code that an administrator created for a fleet token. The route
/// needs no sign-in, because the code is the credential; it is rate limited like the sign-in (failures per address and overall), a code lives five minutes, works once and is
/// invalidated after five wrong attempts, and every pairing and every refused code is written to the audit log. The request is the <c>pair_request</c> of S07 with the client kind
/// <c>homeassistant</c> or <c>tool</c>; the answer is <c>hub/pair_response.schema.json</c>: the token (shown once, stored as a hash), the hub, the scope and the certificate fingerprint.
/// Failures answer <c>{"error": {"code", "message"}}</c>: 400 <c>bad_request</c>, 401 <c>unauthorized</c> (wrong or expired code), 429 <c>rate_limited</c> (with <c>Retry-After</c>).
/// </summary>
public static class PairEndpoint
{
    /// <summary>The scope of the failure counters of the rate limiter.</summary>
    public const string RateLimitScope = "pair";

    /// <summary>The largest request body that is read.</summary>
    public const int MaxBodyBytes = 4 * 1024;

    /// <summary>Maps the endpoint.</summary>
    /// <param name="routes">The route builder.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // Outside /api/v1: no session and no anti-forgery header, the pairing code authenticates the call.
        routes.MapPost(HubProtocol.PairPath, HandleAsync).WithName("pairFleet");
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        FleetPairingService pairing,
        LoginRateLimiter limiter,
        HubIdentity identity,
        HubCertificateProvider certificate,
        AuditLog audit,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggers.CreateLogger("QoffeeAutomat.Hub.Fleet");
        context.Response.Headers.CacheControl = "no-store";
        string caller = context.Connection.RemoteIpAddress?.ToString() ?? "local";

        if (limiter.GetLockout(RateLimitScope, caller) is { } wait)
        {
            int seconds = (int)Math.Ceiling(wait.TotalSeconds);
            FleetLog.PairingLockedOut(logger, caller, seconds);
            return RateLimited(context, seconds);
        }

        (PairRequest? request, IResult? failure) = await ReadAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            limiter.RecordFailure(RateLimitScope, caller);
            return failure!;
        }

        string? invalid = FleetPairingService.ValidateClient(request.Client.Kind, request.Client.Name);
        if (invalid is not null)
        {
            limiter.RecordFailure(RateLimitScope, caller);
            return HubLinkErrors.Result(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, invalid);
        }

        FleetPairingResult result = await pairing.PairAsync(request.Code, request.Client.Kind, request.Client.Name, cancellationToken).ConfigureAwait(false);
        if (result.Token is not { } issued)
        {
            limiter.RecordFailure(RateLimitScope, caller);
            FleetLog.PairingRefused(logger, caller, result.Attempt.ToString());
            await WriteAuditAsync(audit, logger, "anonymous", null, AuditResults.Denied, StatusCodes.Status401Unauthorized, caller).ConfigureAwait(false);
            return HubLinkErrors.Result(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized, "The pairing code is invalid or has expired.");
        }

        limiter.Reset(RateLimitScope, caller);
        FleetLog.ClientPaired(logger, request.Client.Kind, issued.Info.Id, issued.Info.Scope.Type);
        await WriteAuditAsync(
            audit,
            logger,
            "fleet-client:" + issued.Info.ClientName,
            "fleet-token:" + issued.Info.Id,
            AuditResults.Success,
            StatusCodes.Status200OK,
            caller).ConfigureAwait(false);

        var response = new FleetPairResponse(
            issued.Secret,
            new EnrollHubInfo(identity.Id, identity.Name),
            ToInfo(issued.Info.Scope),
            certificate.Fingerprint);

        // The response holds the only copy of the token in plain text.
        return Results.Text(JsonSerializer.Serialize(response, HubJsonContext.Default.FleetPairResponse), "application/json");
    }

    private static FleetScopeInfo ToInfo(FleetScope scope) => new(scope.Type, scope.All ? null : scope.Tags);

    private static IResult RateLimited(HttpContext context, int seconds)
    {
        context.Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture);
        return HubLinkErrors.Result(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests. Try again later.");
    }

    private static async Task<(PairRequest? Request, IResult? Failure)> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        IResult tooLarge = HubLinkErrors.Result(StatusCodes.Status413PayloadTooLarge, ErrorCodes.BadRequest, "The request body is too large.");
        if (request.ContentLength > MaxBodyBytes)
        {
            return (null, tooLarge);
        }

        IHttpMaxRequestBodySizeFeature? limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        using var buffer = new MemoryStream();
        try
        {
            byte[] chunk = new byte[4096];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBodyBytes)
                {
                    return (null, tooLarge);
                }
            }
        }
        catch (BadHttpRequestException)
        {
            return (null, tooLarge);
        }

        try
        {
            PairRequest? value = JsonSerializer.Deserialize(buffer.ToArray(), LinkJsonContext.Default.PairRequest);
            return value is null
                ? (null, HubLinkErrors.Result(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "The request body is missing."))
                : (value, null);
        }
        catch (JsonException)
        {
            return (null, HubLinkErrors.Result(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "The request body is not valid JSON or lacks a required member."));
        }
    }

    private static async Task WriteAuditAsync(AuditLog audit, ILogger logger, string user, string? target, string result, int status, string caller)
    {
        var entry = new NewAuditEntry(user, null, AuthMethods.PairingCode, "pairFleet", HttpMethods.Post, target, [], result, status, caller);
        try
        {
            await audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            FleetLog.AuditWriteFailed(logger, "pairFleet", exception.GetType().Name);
        }
    }
}
