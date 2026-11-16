using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Agent.Api.Runtime;

/// <summary>The recent events (<c>/events</c>) and the log tail (<c>/logs</c>) of the management API (PLAN.md S08).</summary>
public static class EventAndLogEndpoints
{
    /// <summary>The page size of <c>GET /events</c> and <c>GET /logs</c> when the request names none.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The largest page size of <c>GET /events</c>.</summary>
    public const int MaxEvents = 500;

    /// <summary>The largest page size of <c>GET /logs</c>.</summary>
    public const int MaxLogEntries = 1000;

    /// <summary>Maps the endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/events", GetEventsAsync)
            .Describe("Events", "listEvents", "Lists the recent events, newest first.")
            .Query("limit", "integer", "The most events to return, 1 to 500; the default is 50.")
            .Query("type", "string", "Only events of this type.")
            .Returns<EventList>()
            .Fails(StatusCodes.Status400BadRequest);
        api.MapGet("/logs", GetLogsAsync)
            .Describe("Logs", "tailLogs", "Reads the newest log entries, oldest first.")
            .Query("level", "string", "The lowest level: trace, debug, information, warning, error or critical; the default is information.")
            .Query("limit", "integer", "The most entries to return, 1 to 1000; the default is 50.")
            .Returns<LogList>()
            .Fails(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> GetEventsAsync(IEventBus bus, int? limit, string? type, CancellationToken cancellationToken)
    {
        int size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxEvents)
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_query", "limit must be between 1 and 500.");
        }

        IReadOnlyList<AgentEvent> events = await bus
            .GetRecentAsync(size, string.IsNullOrWhiteSpace(type) ? null : type, cancellationToken)
            .ConfigureAwait(false);
        return ApiResults.Json(
            new EventList([.. events.Select(item => new EventInfo(item.Id, item.Type, item.Timestamp, item.Data))]),
            ApiJsonContext.Default.EventList);
    }

    private static async Task<IResult> GetLogsAsync(IAppPaths paths, string? level, int? limit, CancellationToken cancellationToken)
    {
        int size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLogEntries)
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_query", "limit must be between 1 and 1000.");
        }

        LogLevel minimum = LogLevel.Information;
        if (!string.IsNullOrWhiteSpace(level)
            && (!Enum.TryParse(level, ignoreCase: true, out minimum) || minimum == LogLevel.None))
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_query", "level must be trace, debug, information, warning, error or critical.");
        }

        IReadOnlyList<LogEntry> entries = await LogTail.ReadAsync(paths.LogsDirectory, minimum, size, cancellationToken).ConfigureAwait(false);
        return ApiResults.Json(new LogList(entries), ApiJsonContext.Default.LogList);
    }
}
