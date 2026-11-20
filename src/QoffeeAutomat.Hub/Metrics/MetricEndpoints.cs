using System.Globalization;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Agents;
using QoffeeAutomat.Hub.Api;
using QoffeeAutomat.Hub.Api.OpenApi;

namespace QoffeeAutomat.Hub.Metrics;

/// <summary>
/// The metric endpoints of the hub API (PLAN.md S20 "Metrics on the hub", T910); every signed-in user may read them. <c>GET /api/v1/agents/{id}/metrics</c> lists the series of an
/// agent with their newest value. <c>GET /api/v1/metrics/query</c> returns the series of one or several agents for a time range: raw samples, five minute or one hour
/// roll-ups (minimum, average, maximum and count per bucket), chosen from the range unless the request names one; string, enum and timestamp metrics are returned as
/// the change log.
/// </summary>
public static class MetricEndpoints
{
    /// <summary>The most agents in one query.</summary>
    public const int MaxAgents = 50;

    /// <summary>The most metric ids or prefixes in one query.</summary>
    public const int MaxMetricPatterns = 100;

    /// <summary>The range of a query when the request names no start: one hour before its end.</summary>
    public static readonly TimeSpan DefaultRange = TimeSpan.FromHours(1);

    private static readonly char[] ListSeparators = [','];

    /// <summary>Maps the endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        const string tag = "Metrics";
        api.MapGet("/agents/{id}/metrics", GetLatestAsync)
            .Describe(tag, "getAgentMetrics", "Lists the metrics that the hub stores for an agent with the newest value of each.")
            .Returns<AgentMetricList>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapGet("/metrics/query", QueryAsync)
            .Describe(tag, "queryMetrics", "Returns the stored series of one or several agents for a time range. The resolution (raw samples, five minute or one hour buckets) follows from the range unless the request names one.")
            .Query("agent", "string", "An agent id; repeat the parameter or separate ids with commas. At least one, at most 50.")
            .Query("metric", "string", "A metric id, or a prefix that ends in * (cpu.*); repeat or separate with commas. All metrics of the agents by default.")
            .Query("from", "string", "The start of the range (ISO 8601); one hour before to by default.")
            .Query("to", "string", "The end of the range (ISO 8601); now by default.")
            .Query("resolution", "string", "auto (default), raw, 5m or 1h. Text metrics are always returned as the change log.")
            .Query("maxPoints", "integer", "The most points per series, 1 to 5000 (the limit of the hub); a series with more is cut and marked truncated.")
            .Returns<MetricQueryResult>()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
    }

    private static IResult Bad(string detail) => HubApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_query", detail);

    private static async Task<IResult> GetLatestAsync(string id, AgentRegistry registry, IMetricStore store, CancellationToken cancellationToken)
    {
        if (await registry.FindAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return HubApiResults.NotFound(HubErrorCodes.UnknownAgent, "There is no such agent.");
        }

        IReadOnlyList<LatestMetric> metrics = await store.GetLatestAsync(id, cancellationToken).ConfigureAwait(false);
        return HubApiResults.Json(new AgentMetricList(id, metrics), HubApiJsonContext.Default.AgentMetricList);
    }

    private static async Task<IResult> QueryAsync(
        HttpContext context,
        AgentRegistry registry,
        IMetricStore store,
        HubMetricsOptions options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        IQueryCollection query = context.Request.Query;

        string[] agents = [.. List(query, "agent")];
        if (agents.Length == 0 || agents.Length > MaxAgents)
        {
            return Bad("agent must name between 1 and " + MaxAgents.ToString(CultureInfo.InvariantCulture) + " agents.");
        }

        string[] metrics = [.. List(query, "metric")];
        if (metrics.Length == 0)
        {
            metrics = [MetricPattern.Wildcard.ToString()];
        }

        if (metrics.Length > MaxMetricPatterns || metrics.Any(pattern => !MetricPattern.IsValid(pattern)))
        {
            return Bad("metric must name at most " + MaxMetricPatterns.ToString(CultureInfo.InvariantCulture) + " metric ids or prefixes that end in *.");
        }

        DateTimeOffset now = time.GetUtcNow();
        if (!TryTime(query, "to", now, out DateTimeOffset to) || !TryTime(query, "from", to - DefaultRange, out DateTimeOffset from))
        {
            return Bad("from and to must be times in ISO 8601 format, for example 2026-11-18T09:30:00Z.");
        }

        if (from >= to)
        {
            return Bad("from must be before to.");
        }

        int maxPoints = options.MaxQueryPoints;
        string maxPointsText = query["maxPoints"].ToString();
        if (maxPointsText.Length > 0
            && (!int.TryParse(maxPointsText, NumberStyles.None, CultureInfo.InvariantCulture, out maxPoints) || maxPoints < 1 || maxPoints > options.MaxQueryPoints))
        {
            return Bad("maxPoints must be a number from 1 to " + options.MaxQueryPoints.ToString(CultureInfo.InvariantCulture) + ".");
        }

        MetricResolution resolution;
        string resolutionText = query["resolution"].ToString();
        if (resolutionText.Length == 0 || resolutionText == "auto")
        {
            resolution = MetricQueryPlanner.Choose(from, to, now, options);
        }
        else if (!MetricResolutions.TryParse(resolutionText, out resolution))
        {
            return Bad("resolution must be auto, raw, 5m or 1h.");
        }

        foreach (string agentId in agents)
        {
            if (await registry.FindAsync(agentId, cancellationToken).ConfigureAwait(false) is null)
            {
                return HubApiResults.NotFound(HubErrorCodes.UnknownAgent, "There is no such agent: " + agentId + ".");
            }
        }

        int matched = await store.CountSeriesAsync(agents, metrics, cancellationToken).ConfigureAwait(false);
        if (matched > options.MaxQuerySeries)
        {
            return Bad(
                "The query selects " + matched.ToString(CultureInfo.InvariantCulture) + " series; at most "
                + options.MaxQuerySeries.ToString(CultureInfo.InvariantCulture) + " are allowed. Name fewer agents or metrics.");
        }

        IReadOnlyList<MetricSeriesData> series = await store.QueryAsync(new MetricQuery(agents, metrics, from, to, resolution, maxPoints), cancellationToken).ConfigureAwait(false);
        return HubApiResults.Json(new MetricQueryResult(MetricResolutions.ToText(resolution), from, to, series), HubApiJsonContext.Default.MetricQueryResult);
    }

    private static IEnumerable<string> List(IQueryCollection query, string name)
    {
        return query[name]
            .SelectMany(value => (value ?? string.Empty).Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal);
    }

    private static bool TryTime(IQueryCollection query, string name, DateTimeOffset fallback, out DateTimeOffset time)
    {
        string text = query[name].ToString();
        if (text.Length == 0)
        {
            time = fallback;
            return true;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);
    }
}
