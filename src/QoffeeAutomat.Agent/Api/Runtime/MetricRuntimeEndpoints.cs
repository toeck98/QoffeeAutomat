using System.Text.Json;
using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Metrics;

namespace QoffeeAutomat.Agent.Api.Runtime;

/// <summary>
/// The live and recorded values of metrics (PLAN.md S08): <c>/metrics/stream</c> as Server-Sent Events and
/// <c>/metrics/history</c> for charts. The stream is a subscriber of the <see cref="MetricEngine"/> like a
/// QoffeeLink connection, so the engine samples the selected metrics while the stream is open and stops when the
/// last subscriber has gone (D-007).
/// </summary>
public static class MetricRuntimeEndpoints
{
    private const string Tag = "Metrics";

    /// <summary>Maps the endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/metrics/stream", StreamMetricsAsync)
            .Describe(Tag, "streamMetrics", "Streams metric values as Server-Sent Events: a snapshot event, then update events with the changed values.")
            .Query("metrics", "string", "Comma-separated metric ids, or * for all; the default is all.")
            .Query("interval", "integer", "At most one value per this many seconds and metric.")
            .ReturnsFile(ServerSentEvents.ContentType);
        api.MapGet("/metrics/history", GetHistory)
            .Describe(Tag, "getMetricHistory", "Reads the recorded values of metrics, oldest first. Only metrics that were sampled have history.")
            .Query("metrics", "string", "Comma-separated metric ids.")
            .Query("since", "string", "Only values at or after this time (ISO 8601).")
            .Query("seconds", "integer", "Only values of the last this many seconds; ignored when since is given.")
            .Returns<MetricHistoryResponse>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> StreamMetricsAsync(
        HttpContext context,
        MetricEngine engine,
        string? metrics,
        int? interval,
        CancellationToken cancellationToken)
    {
        if (interval is < 1)
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_query", "interval must be at least 1 second.");
        }

        string[] ids = Ids(metrics);
        bool all = ids.Length == 0 || ids.Contains(MetricSelection.WildcardText, StringComparer.Ordinal);
        MetricSelection selection = all ? MetricSelection.Everything : MetricSelection.From(ids);

        Dictionary<string, double>? intervals = null;
        if (interval is { } seconds)
        {
            IEnumerable<string> targets = all ? engine.Catalog.Select(entry => entry.Id) : ids;
            intervals = targets.ToDictionary(id => id, _ => (double)seconds, StringComparer.Ordinal);
        }

        // Subscribing is what makes the engine sample; disposing ends the demand.
        using MetricSubscription subscription = engine.Subscribe(selection, intervals);
        try
        {
            await ServerSentEvents.StartAsync(context, cancellationToken).ConfigureAwait(false);
            await foreach (MetricUpdate update in subscription.Updates.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Dictionary<string, JsonElement> values = update.Values.ToDictionary(
                    pair => pair.Key,
                    pair => ApiJson.Element(pair.Value),
                    StringComparer.Ordinal);
                string json = JsonSerializer.Serialize(
                    new MetricStreamUpdate(update.Timestamp, update.IsSnapshot, values),
                    ApiJsonContext.Default.MetricStreamUpdate);
                await ServerSentEvents.WriteAsync(context.Response, update.IsSnapshot ? "snapshot" : "update", json, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // The client went away.
        }

        return Results.Empty;
    }

    private static IResult GetHistory(MetricEngine engine, TimeProvider time, string? metrics, DateTimeOffset? since, int? seconds)
    {
        string[] ids = Ids(metrics);
        if (ids.Length == 0)
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_query", "metrics is required: a comma-separated list of metric ids.");
        }

        if (seconds is < 1)
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_query", "seconds must be at least 1.");
        }

        DateTimeOffset? from = since;
        if (from is null && seconds is { } back)
        {
            from = time.GetUtcNow() - TimeSpan.FromSeconds(back);
        }

        HashSet<string> known = [.. engine.Catalog.Select(entry => entry.Id)];
        string? unknown = ids.FirstOrDefault(id => !known.Contains(id));
        if (unknown is not null)
        {
            return ApiResults.NotFound("unknown_metric", "There is no metric '" + unknown + "'.");
        }

        MetricSeries[] series =
        [
            .. ids.Distinct(StringComparer.Ordinal).Select(id => new MetricSeries(
                id,
                [.. engine.GetHistory(id, from).Select(sample => new MetricPoint(sample.Timestamp, ApiJson.ElementOrNull(sample.Value)))])),
        ];
        return ApiResults.Json(new MetricHistoryResponse(series), ApiJsonContext.Default.MetricHistoryResponse);
    }

    private static string[] Ids(string? list)
    {
        return string.IsNullOrWhiteSpace(list)
            ? []
            : [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}
