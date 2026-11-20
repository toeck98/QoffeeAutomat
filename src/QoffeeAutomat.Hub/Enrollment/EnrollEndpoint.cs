using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Security;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Enrollment;

/// <summary>
/// <c>POST /hub/v1/enroll</c> (PLAN.md S20, T904): the agent exchanges an enrollment token for its credential. The route needs no sign-in, because
/// the token is the credential; the request is rate limited like the sign-in (failures per address and overall) and every enrollment, every refused
/// token and every hand-over of a credential is written to the audit log. Failures answer <c>{"error": {"code", "message"}}</c>: 400
/// <c>bad_request</c>, 401 <c>invalid_token</c>, 403 <c>rejected</c>, 429 <c>rate_limited</c> (with <c>Retry-After</c>).
/// </summary>
public static class EnrollEndpoint
{
    /// <summary>The scope of the failure counters of the rate limiter.</summary>
    public const string RateLimitScope = "enroll";

    /// <summary>Maps the endpoint.</summary>
    /// <param name="routes">The route builder.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // Outside /api/v1: no session and no anti-forgery header, the enrollment token authenticates the call.
        routes.MapPost(HubProtocol.EnrollPath, HandleAsync).WithName("enrollAgent");
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        EnrollmentService enrollment,
        LoginRateLimiter limiter,
        HubIdentity identity,
        HubEnrollmentOptions options,
        AuditLog audit,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggers.CreateLogger("QoffeeAutomat.Hub.Enrollment");
        context.Response.Headers.CacheControl = "no-store";
        string caller = context.Connection.RemoteIpAddress?.ToString() ?? "local";

        if (limiter.GetLockout(RateLimitScope, caller) is { } wait)
        {
            int seconds = (int)Math.Ceiling(wait.TotalSeconds);
            EnrollmentLog.EnrollLockedOut(logger, caller, seconds);
            return RateLimited(context, seconds);
        }

        (EnrollRequest? request, IResult? failure) = await ReadAsync(context.Request, options.MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            limiter.RecordFailure(RateLimitScope, caller);
            return failure!;
        }

        EnrollOutcome outcome = await enrollment.EnrollAsync(request, cancellationToken).ConfigureAwait(false);
        var hub = new EnrollHubInfo(identity.Id, identity.Name);
        (IResult result, int status) = outcome.Status switch
        {
            EnrollStatus.Enrolled => (Json(new EnrollResponse(EnrollStates.Enrolled, hub, outcome.Credential)), StatusCodes.Status200OK),
            EnrollStatus.Pending => (Json(new EnrollResponse(EnrollStates.Pending, hub, null, enrollment.RetryAfterSeconds)), StatusCodes.Status200OK),
            EnrollStatus.Rejected => (HubLinkErrors.Result(StatusCodes.Status403Forbidden, "rejected", outcome.Message ?? "The agent was rejected."), StatusCodes.Status403Forbidden),
            EnrollStatus.TooManyPending => (RateLimited(context, enrollment.RetryAfterSeconds), StatusCodes.Status429TooManyRequests),
            EnrollStatus.BadRequest => (HubLinkErrors.Result(StatusCodes.Status400BadRequest, "bad_request", outcome.Message ?? "The request is not valid."), StatusCodes.Status400BadRequest),
            _ => (HubLinkErrors.Result(StatusCodes.Status401Unauthorized, "invalid_token", outcome.Message ?? "The enrollment token is not valid."), StatusCodes.Status401Unauthorized),
        };

        if (outcome.Status is EnrollStatus.Enrolled or EnrollStatus.Pending or EnrollStatus.Rejected)
        {
            limiter.Reset(RateLimitScope, caller);
        }
        else if (outcome.Status is EnrollStatus.InvalidToken or EnrollStatus.BadRequest)
        {
            limiter.RecordFailure(RateLimitScope, caller);
            EnrollmentLog.EnrollRefused(logger, caller, outcome.Status == EnrollStatus.InvalidToken ? "invalid token" : "bad request");
        }

        if (outcome.Notable || outcome.Status is EnrollStatus.InvalidToken or EnrollStatus.Rejected or EnrollStatus.TooManyPending)
        {
            await WriteAuditAsync(audit, logger, outcome, status, caller).ConfigureAwait(false);
        }

        if (outcome.Notable)
        {
            if (outcome.Status == EnrollStatus.Enrolled)
            {
                EnrollmentLog.AgentEnrolled(logger, outcome.AgentId ?? string.Empty, request.Agent.Name);
            }
            else if (outcome.Status == EnrollStatus.Pending)
            {
                EnrollmentLog.AgentPending(logger, outcome.AgentId ?? string.Empty);
            }
        }

        return result;
    }

    private static IResult Json(EnrollResponse response)
    {
        return Results.Text(JsonSerializer.Serialize(response, HubJsonContext.Default.EnrollResponse), "application/json");
    }

    private static IResult RateLimited(HttpContext context, int seconds)
    {
        context.Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture);
        return HubLinkErrors.Result(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests. Try again later.");
    }

    private static async Task<(EnrollRequest? Request, IResult? Failure)> ReadAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        IResult tooLarge = HubLinkErrors.Result(StatusCodes.Status413PayloadTooLarge, "bad_request", "The request body is too large.");
        if (request.ContentLength > maxBytes)
        {
            return (null, tooLarge);
        }

        IHttpMaxRequestBodySizeFeature? limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = maxBytes;
        }

        using var buffer = new MemoryStream();
        try
        {
            byte[] chunk = new byte[4096];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
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
            EnrollRequest? value = JsonSerializer.Deserialize(buffer.ToArray(), HubJsonContext.Default.EnrollRequest);
            return value is null
                ? (null, HubLinkErrors.Result(StatusCodes.Status400BadRequest, "bad_request", "The request body is missing."))
                : (value, null);
        }
        catch (JsonException)
        {
            return (null, HubLinkErrors.Result(StatusCodes.Status400BadRequest, "bad_request", "The request body is not valid JSON or lacks a required member."));
        }
    }

    private static async Task WriteAuditAsync(AuditLog audit, ILogger logger, EnrollOutcome outcome, int status, string caller)
    {
        string result = outcome.Status switch
        {
            EnrollStatus.Enrolled or EnrollStatus.Pending => AuditResults.Success,
            EnrollStatus.BadRequest => AuditResults.Failure,
            _ => AuditResults.Denied,
        };
        string? target = outcome.AgentId is null ? null : "agent:" + outcome.AgentId;
        var entry = new NewAuditEntry(
            target ?? "anonymous",
            null,
            "enrollment_token",
            "enrollAgent",
            HttpMethods.Post,
            target,
            outcome.AgentId is null ? [] : [outcome.AgentId],
            result,
            status,
            caller);
        try
        {
            await audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            EnrollmentLog.AuditWriteFailed(logger, exception, "enrollAgent");
        }
    }
}
