using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Hub.Security;

/// <summary>
/// What an endpoint tells the audit log about the request it handles (PLAN.md S20 "Hub security" item 2). The audit middleware reads it
/// when the request is done. An endpoint sets the target (what it changed), the agents it affected, and, where the caller is not
/// authenticated yet (sign-in, setup), the user.
/// </summary>
public sealed class AuditContext
{
    /// <summary>The key in <see cref="HttpContext.Items"/>.</summary>
    public const string ItemKey = "QoffeeAutomat.Hub.Audit";

    /// <summary>What the action was applied to, for example <c>user:3f2a...</c>.</summary>
    public string? Target { get; set; }

    /// <summary>The user of the entry when the request has no authenticated principal (a failed sign-in records the name that was tried).</summary>
    public string? User { get; set; }

    /// <summary>The role of the entry when the request has no authenticated principal.</summary>
    public string? Role { get; set; }

    /// <summary>Overrides the result that the middleware derives from the HTTP status; null keeps it.</summary>
    public string? Result { get; set; }

    /// <summary>The ids of the agents that the action affected.</summary>
    public ISet<string> Agents { get; } = new SortedSet<string>(StringComparer.Ordinal);
}

/// <summary>Access to the <see cref="AuditContext"/> of a request.</summary>
public static class AuditHttpContextExtensions
{
    /// <summary>Gets the audit context of the request; it is created on first use.</summary>
    /// <param name="context">The request context.</param>
    /// <returns>The audit context.</returns>
    public static AuditContext Audit(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(AuditContext.ItemKey, out object? existing) && existing is AuditContext audit)
        {
            return audit;
        }

        var created = new AuditContext();
        context.Items[AuditContext.ItemKey] = created;
        return created;
    }
}

/// <summary>
/// Writes exactly one audit entry for every request that can change state (PLAN.md S20 "Hub security" item 2, T903): every request
/// with a method other than GET, HEAD, OPTIONS and TRACE to a route below <c>/api/v1</c>. The middleware wraps authentication and
/// authorization, so a request that is refused (not signed in, role too low, missing anti-forgery header, too many attempts) leaves an entry
/// with the result <see cref="AuditResults.Denied"/>, and a failing request one with <see cref="AuditResults.Failure"/>. A path that matches
/// no route changes nothing and is not audited. The entry is written when the request is done, with a token that cannot be cancelled by
/// a client that disconnects; if the database cannot be written, the failure is logged.
/// </summary>
public sealed class AuditMiddleware
{
    private const string ApiPrefix = "/api/v1";

    private readonly RequestDelegate _next;
    private readonly AuditLog _audit;
    private readonly ILogger<AuditMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="audit">The audit log.</param>
    /// <param name="logger">The logger.</param>
    public AuditMiddleware(RequestDelegate next, AuditLog audit, ILogger<AuditMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Handles a request.</summary>
    /// <param name="context">The request context.</param>
    /// <returns>A task that completes when the request is handled and its entry is written.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsAudited(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        AuditContext scope = context.Audit();
        bool crashed = false;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch
        {
            crashed = true;
            throw;
        }
        finally
        {
            await WriteAsync(context, scope, crashed).ConfigureAwait(false);
        }
    }

    /// <summary>Tells whether a request is audited.</summary>
    /// <param name="context">The request context, after routing.</param>
    /// <returns>True for a state-changing method on a route below <c>/api/v1</c>.</returns>
    public static bool IsAudited(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HttpRequest request = context.Request;
        return !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
                || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
            && request.Path.StartsWithSegments(ApiPrefix, StringComparison.Ordinal)
            && context.GetEndpoint() is not null;
    }

    private static string ResultOf(int status)
    {
        return status switch
        {
            < 400 => AuditResults.Success,
            StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden or StatusCodes.Status429TooManyRequests => AuditResults.Denied,
            _ => AuditResults.Failure,
        };
    }

    private async Task WriteAsync(HttpContext context, AuditContext scope, bool crashed)
    {
        Endpoint? endpoint = context.GetEndpoint();
        string action = endpoint?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
            ?? context.Request.Method + " " + context.Request.Path;
        int status = crashed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;

        var entry = new NewAuditEntry(
            scope.User ?? HubPrincipal.Name(context.User) ?? "anonymous",
            scope.Role ?? HubPrincipal.Role(context.User),
            HubPrincipal.Method(context.User),
            action,
            context.Request.Method,
            scope.Target,
            [.. scope.Agents],
            scope.Result ?? ResultOf(status),
            status,
            context.Connection.RemoteIpAddress?.ToString());

        try
        {
            await _audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            SecurityLog.AuditWriteFailed(_logger, exception, action);
        }
    }
}
