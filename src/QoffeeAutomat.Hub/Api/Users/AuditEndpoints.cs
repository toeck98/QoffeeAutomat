using System.Globalization;
using QoffeeAutomat.Hub.Api.OpenApi;
using QoffeeAutomat.Hub.Security;

namespace QoffeeAutomat.Hub.Api.Users;

/// <summary>
/// The query endpoint of the audit log (PLAN.md S20 "Hub security" item 2, T903): <c>GET /api/v1/audit</c>, newest entry first, filtered by user,
/// action, result, agent and time, and paged with a cursor. Only an administrator may read it.
/// </summary>
public static class AuditEndpoints
{
    /// <summary>Maps the endpoint.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/audit", QueryAsync).RequireAuthorization(HubPolicies.Admin)
            .Describe("Audit", "queryAuditLog", "Reads the audit log, newest entry first.")
            .Query("user", "string", "Only entries of this user (without regard to case); an API token is token:<name>.")
            .Query("action", "string", "Only entries of this action, for example createUser.")
            .Query("result", "string", "Only entries with this result: success, failure or denied.")
            .Query("agent", "string", "Only entries that affected this agent id.")
            .Query("from", "string", "Only entries at or after this time (ISO 8601).")
            .Query("to", "string", "Only entries before this time (ISO 8601).")
            .Query("before", "integer", "Only entries with a smaller id: the nextBefore of the previous page.")
            .Query("limit", "integer", "The page size: 1 to 500, 100 by default.")
            .Returns<AuditPage>()
            .Fails(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> QueryAsync(HttpContext context, AuditLog audit, CancellationToken cancellationToken)
    {
        IQueryCollection query = context.Request.Query;

        string? result = Text(query, "result");
        if (result is not (null or AuditResults.Success or AuditResults.Failure or AuditResults.Denied))
        {
            return Bad("result must be success, failure or denied.");
        }

        if (!TryTime(query, "from", out DateTimeOffset? fromTime) || !TryTime(query, "to", out DateTimeOffset? toTime))
        {
            return Bad("from and to must be times in ISO 8601 format, for example 2026-11-18T09:30:00Z.");
        }

        long? before = null;
        if (Text(query, "before") is { } beforeText)
        {
            if (!long.TryParse(beforeText, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedBefore))
            {
                return Bad("before must be a non-negative number.");
            }

            before = parsedBefore;
        }

        int limit = AuditQuery.DefaultLimit;
        if (Text(query, "limit") is { } limitText && !int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit))
        {
            return Bad("limit must be a non-negative number.");
        }

        var request = new AuditQuery
        {
            User = Text(query, "user"),
            Action = Text(query, "action"),
            Result = result,
            Agent = Text(query, "agent"),
            From = fromTime,
            To = toTime,
            Before = before,
            Limit = limit,
        };
        IReadOnlyList<AuditEntry> entries = await audit.QueryAsync(request, cancellationToken).ConfigureAwait(false);

        AuditEntryInfo[] page = [.. entries.Select(ToInfo)];
        long? next = page.Length > 0 && page.Length >= request.EffectiveLimit ? page[^1].Id : null;
        return HubApiResults.Json(new AuditPage(page, next), HubApiJsonContext.Default.AuditPage);
    }

    private static string? Text(IQueryCollection query, string name)
    {
        string value = query[name].ToString();
        return value.Length == 0 ? null : value;
    }

    private static bool TryTime(IQueryCollection query, string name, out DateTimeOffset? time)
    {
        time = null;
        if (Text(query, name) is not { } text)
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
        {
            return false;
        }

        time = parsed;
        return true;
    }

    private static IResult Bad(string detail) => HubApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_query", detail);

    private static AuditEntryInfo ToInfo(AuditEntry entry)
    {
        return new AuditEntryInfo(
            entry.Id,
            entry.At,
            entry.User,
            entry.Role,
            entry.AuthMethod,
            entry.Action,
            entry.Method,
            entry.Target,
            entry.Agents,
            entry.Result,
            entry.Status,
            entry.RemoteAddress);
    }
}
