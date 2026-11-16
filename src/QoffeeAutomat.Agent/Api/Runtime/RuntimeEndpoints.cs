namespace QoffeeAutomat.Agent.Api.Runtime;

/// <summary>
/// The runtime endpoints of the management API (PLAN.md S08, T214): runs and their live output, live and recorded
/// metric values, events, logs, the configuration archive and the diagnostics bundle. The pairing endpoints
/// (<c>/link/pairing</c>, <c>/link/clients</c>) belong to the pairing service and are mapped with it.
/// </summary>
public static class RuntimeEndpoints
{
    /// <summary>Maps all runtime endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        RunEndpoints.Map(api);
        MetricRuntimeEndpoints.Map(api);
        EventAndLogEndpoints.Map(api);
        ArchiveEndpoints.Map(api);
    }
}
