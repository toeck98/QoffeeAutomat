using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Agent.Hub.ConfigSync;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Metrics;

namespace QoffeeAutomat.Agent.Api;

/// <summary>
/// The metric endpoints (PLAN.md S08): the catalog with the current value, and the overrides of
/// <c>config/metrics.json</c> (enabled, exposeToHa, interval, deadband). A metric only has a value while
/// somebody samples it (D-007), so a metric that nobody subscribes to has none.
/// </summary>
public static class MetricEndpoints
{
    /// <summary>The largest override that is read.</summary>
    public const int MaxBodyBytes = 16 * 1024;

    private const string Tag = "Metrics";

    /// <summary>Maps <c>/metrics</c> and <c>/metrics/{id}</c>; the stream and the history are mapped by <c>RuntimeEndpoints</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/metrics", ListMetrics)
            .Describe(Tag, "listMetrics", "Lists the metric catalog with the current values.")
            .Returns<MetricList>();
        api.MapGet("/metrics/{id}", GetMetric)
            .Describe(Tag, "getMetric", "Reads one metric of the catalog.")
            .Returns<MetricInfo>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapPut("/metrics/{id}", PutOverrideAsync)
            .GuardsLocalConfig(LocalConfigArea.MetricOverrides)
            .Describe(Tag, "putMetricOverride", "Sets the override of a metric; an override without members removes it. The ETag is the revision of all overrides.")
            .Takes<MetricOverride>()
            .Returns<MetricOverridesDocument>()
            .HonoursIfMatch()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, ApiResults.UnprocessableContent);
        api.MapDelete("/metrics/{id}", DeleteOverrideAsync)
            .GuardsLocalConfig(LocalConfigArea.MetricOverrides)
            .Describe(Tag, "deleteMetricOverride", "Removes the override of a metric.")
            .Returns<MetricOverridesDocument>()
            .HonoursIfMatch()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
    }

    /// <summary>Describes a metric of the catalog.</summary>
    /// <param name="entry">The catalog entry.</param>
    /// <param name="engine">The engine, for the current value.</param>
    /// <returns>The description.</returns>
    public static MetricInfo Describe(MetricCatalogEntry entry, MetricEngine engine)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(engine);

        QoffeeAutomat.Contracts.Link.MetricDescriptor descriptor = entry.Descriptor;
        bool hasValue = engine.TryGetCurrentValue(entry.Id, out object? value);
        return new MetricInfo(
            descriptor.Id,
            descriptor.Name,
            descriptor.Kind,
            descriptor.ValueType,
            descriptor.Context,
            descriptor.Unit,
            descriptor.DeviceClass,
            descriptor.StateClass,
            descriptor.EntityCategory,
            descriptor.Icon,
            descriptor.Precision,
            descriptor.Options,
            descriptor.EnabledDefault,
            entry.Enabled,
            entry.ExposeToHa,
            entry.IntervalSeconds,
            entry.Deadband,
            entry.IsCustom,
            hasValue,
            hasValue ? ApiJson.ElementOrNull(value) : null);
    }

    private static IResult ListMetrics(MetricEngine engine)
    {
        return ApiResults.Json(
            new MetricList([.. engine.Catalog.Select(entry => Describe(entry, engine))]),
            ApiJsonContext.Default.MetricList);
    }

    private static IResult GetMetric(string id, MetricEngine engine)
    {
        MetricCatalogEntry? entry = engine.Catalog.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        return entry is null ? UnknownMetric() : ApiResults.Json(Describe(entry, engine), ApiJsonContext.Default.MetricInfo);
    }

    private static async Task<IResult> PutOverrideAsync(
        string id,
        HttpContext context,
        IConfigStore config,
        MetricEngine engine,
        CancellationToken cancellationToken)
    {
        IResult? header = ApiETag.ReadRevision(context.Request, out int? expected);
        if (header is not null)
        {
            return header;
        }

        (MetricOverride? change, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiDocumentJsonContext.Default.MetricOverride, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (change is null)
        {
            return failure!;
        }

        if (!engine.Catalog.Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal)))
        {
            return UnknownMetric();
        }

        bool isEmpty = change.Enabled is null && change.ExposeToHa is null && change.IntervalSeconds is null && change.Deadband is null;
        return await SaveOverrideAsync(id, isEmpty ? null : change, expected, context, config, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> DeleteOverrideAsync(string id, HttpContext context, IConfigStore config, MetricEngine engine, CancellationToken cancellationToken)
    {
        IResult? header = ApiETag.ReadRevision(context.Request, out int? expected);
        if (header is not null)
        {
            return header;
        }

        if (!engine.Catalog.Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal)))
        {
            return UnknownMetric();
        }

        return await SaveOverrideAsync(id, null, expected, context, config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets or removes the override of one metric in the single overrides document.</summary>
    private static async Task<IResult> SaveOverrideAsync(
        string id,
        MetricOverride? change,
        int? expected,
        HttpContext context,
        IConfigStore config,
        CancellationToken cancellationToken)
    {
        MetricOverridesDocument? existing = config.Local.GetDocument<MetricOverridesDocument>(ConfigIds.MetricOverrides);
        var overrides = new Dictionary<string, MetricOverride>(existing?.Overrides ?? new Dictionary<string, MetricOverride>(), StringComparer.Ordinal);
        if (change is null)
        {
            overrides.Remove(id);
        }
        else
        {
            overrides[id] = change;
        }

        var document = new MetricOverridesDocument(DocumentSchema.CurrentVersion, overrides);

        // The overrides of all metrics are one document: without If-Match the change applies to the revision that was read.
        SaveResult<MetricOverridesDocument> saved = await config.Local
            .SaveAsync(document, expected ?? existing?.Revision ?? 0, cancellationToken)
            .ConfigureAwait(false);
        if (saved.Status == ChangeStatus.Conflict && expected is null)
        {
            return ApiResults.Problem(StatusCodes.Status409Conflict, "concurrent_modification", "The overrides were changed while the request was handled. Repeat it.", currentRevision: saved.CurrentRevision);
        }

        return ApiDocuments.SaveOutcome(saved, ApiDocumentJsonContext.Default.MetricOverridesDocument, context, created: false, "/api/v1/metrics/" + id);
    }

    private static IResult UnknownMetric()
    {
        return ApiResults.NotFound("unknown_metric", "There is no metric with this id.");
    }
}
