namespace QoffeeAutomat.Agent.Api;

/// <summary>
/// The configuration endpoints of the management API (PLAN.md S08, T213): commands, scripts, sensors, metrics,
/// settings and the status. They are mapped on the <c>/api/v1</c> group, which requires a session and the
/// anti-forgery header.
/// </summary>
public static class ConfigurationEndpoints
{
    /// <summary>Maps all configuration endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        CommandEndpoints.Map(api);
        ScriptEndpoints.Map(api);
        SensorEndpoints.Map(api);
        MetricEndpoints.Map(api);
        SettingsAndStatusEndpoints.Map(api);
        RevisionEndpoints.Map(api);
        AgentEndpoints.Map(api);
    }
}
