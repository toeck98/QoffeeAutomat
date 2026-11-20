using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Core.Config.HubLayer;

/// <summary>
/// Describes what the hub configured on this agent for the management API and the UI (PLAN.md S20, T908): the documents of the hub layer, the local
/// documents that the hub layer or a lock keeps from being used, the policy and the names of the hub variables.
/// </summary>
public static class HubConfigOverview
{
    /// <summary>Builds the state that <c>GET /api/v1/hub/config</c> returns.</summary>
    /// <param name="store">The configuration store.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The state; for an agent without a hub the policy allows everything and every list is empty.</returns>
    public static async Task<HubConfigState> DescribeAsync(IConfigStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        HubLayerState state = store.Hub.State;
        IReadOnlyList<HubShadowedItem> shadowed = await GetShadowedAsync(store, cancellationToken).ConfigureAwait(false);
        return new HubConfigState(
            state.IsActive,
            state.Generation,
            state.AppliedAt,
            new HubConfigPolicy(state.Policy.LocalConfig, state.Policy.DirectLink),
            state.LocalConfigLocked,
            state.DirectLinkDenied,
            [.. state.Items.Select(item => new HubConfigItem(item.Kind, item.Id, item.Hash, item.State))],
            shadowed,
            [.. store.Hub.Variables.Keys.Order(StringComparer.Ordinal)],
            state.LastError);
    }

    /// <summary>Lists the local documents that are kept but not used.</summary>
    /// <param name="store">The configuration store.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// Every local command, sensor, mode, metric override document and script while the local configuration is locked (reason
    /// <see cref="HubShadowReasons.LocalConfigLocked"/>); otherwise those with the kind and id of a hub document (reason <see cref="HubShadowReasons.HubDocument"/>).
    /// </returns>
    public static async Task<IReadOnlyList<HubShadowedItem>> GetShadowedAsync(IConfigStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        bool locked = store.Hub.State.LocalConfigLocked;
        if (!store.Hub.State.IsActive)
        {
            return [];
        }

        var shadowed = new List<HubShadowedItem>();
        foreach (CommandDocument command in store.Local.List<CommandDocument>())
        {
            Add(store, shadowed, locked, ConfigKind.Command, DocumentKinds.Command, command.Id);
        }

        foreach (SensorDocument sensor in store.Local.List<SensorDocument>())
        {
            Add(store, shadowed, locked, ConfigKind.Sensor, DocumentKinds.Sensor, sensor.Id);
        }

        foreach (ModeGroupDocument mode in store.Local.List<ModeGroupDocument>())
        {
            Add(store, shadowed, locked, ConfigKind.Mode, DocumentKinds.Mode, mode.Id);
        }

        if (store.Local.GetDocument<MetricOverridesDocument>(ConfigIds.MetricOverrides) is not null)
        {
            Add(store, shadowed, locked, ConfigKind.MetricOverrides, DocumentKinds.MetricOverrides, ConfigIds.MetricOverrides);
        }

        foreach (ScriptInfo script in await store.Local.ListScriptsAsync(cancellationToken).ConfigureAwait(false))
        {
            Add(store, shadowed, locked, ConfigKind.Script, DocumentKinds.Script, script.Name);
        }

        return
        [
            .. shadowed
                .OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal),
        ];
    }

    private static void Add(IConfigStore store, List<HubShadowedItem> shadowed, bool locked, ConfigKind kind, string wireKind, string id)
    {
        if (locked)
        {
            shadowed.Add(new HubShadowedItem(wireKind, id, HubShadowReasons.LocalConfigLocked));
        }
        else if (store.Hub.Owns(kind, id))
        {
            shadowed.Add(new HubShadowedItem(wireKind, id, HubShadowReasons.HubDocument));
        }
    }
}
