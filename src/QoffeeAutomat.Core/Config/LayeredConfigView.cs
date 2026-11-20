using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Core.Config;

/// <summary>
/// The effective view over an ordered list of layers (highest priority first): on equal kind and id
/// the first layer that has the item wins (PLAN.md D-028). The hub layer comes first, so a hub document shadows
/// the local one with the same kind and id. While the hub locks the local configuration (D-029) the local layer
/// is inactive for commands, sensors, modes, metric overrides and scripts; the agent settings stay local.
/// </summary>
internal sealed class LayeredConfigView : IConfigView
{
    private readonly IReadOnlyList<IConfigLayer> _layers;
    private readonly Func<bool> _localLocked;

    public LayeredConfigView(IReadOnlyList<IConfigLayer> layers, Func<bool>? localLocked = null)
    {
        _layers = layers;
        _localLocked = localLocked ?? (() => false);
    }

    public T? GetDocument<T>(string id)
        where T : class, IConfigDocument
    {
        bool locked = _localLocked();
        foreach (IConfigLayer layer in _layers)
        {
            if (IsInactive(layer, locked, typeof(T)))
            {
                continue;
            }

            T? document = layer.GetDocument<T>(id);
            if (document is not null)
            {
                return document;
            }
        }

        return null;
    }

    public IReadOnlyList<T> List<T>()
        where T : class, IConfigDocument
    {
        bool locked = _localLocked();
        var byId = new SortedDictionary<string, T>(StringComparer.Ordinal);
        foreach (IConfigLayer layer in _layers)
        {
            if (IsInactive(layer, locked, typeof(T)))
            {
                continue;
            }

            foreach (T document in layer.List<T>())
            {
                string? id = document switch
                {
                    CommandDocument command => command.Id,
                    SensorDocument sensor => sensor.Id,
                    ModeGroupDocument mode => mode.Id,
                    MetricOverridesDocument => ConfigIds.MetricOverrides,
                    AgentSettingsDocument => ConfigIds.Settings,
                    _ => null,
                };

                if (id is not null)
                {
                    byId.TryAdd(id, document);
                }
            }
        }

        return [.. byId.Values];
    }

    public async Task<IReadOnlyList<ScriptInfo>> ListScriptsAsync(CancellationToken cancellationToken)
    {
        bool locked = _localLocked();
        var byName = new SortedDictionary<string, ScriptInfo>(StringComparer.Ordinal);
        foreach (IConfigLayer layer in _layers)
        {
            if (IsInactive(layer, locked, documentType: null))
            {
                continue;
            }

            foreach (ScriptInfo script in await layer.ListScriptsAsync(cancellationToken).ConfigureAwait(false))
            {
                byName.TryAdd(script.Name, script);
            }
        }

        return [.. byName.Values];
    }

    public async Task<ScriptFile?> GetScriptAsync(string name, CancellationToken cancellationToken)
    {
        bool locked = _localLocked();
        foreach (IConfigLayer layer in _layers)
        {
            if (IsInactive(layer, locked, documentType: null))
            {
                continue;
            }

            ScriptFile? script = await layer.GetScriptAsync(name, cancellationToken).ConfigureAwait(false);
            if (script is not null)
            {
                return script;
            }
        }

        return null;
    }

    /// <summary>The local layer does not take part while it is locked, except for the agent settings (a null type stands for scripts).</summary>
    private static bool IsInactive(IConfigLayer layer, bool localLocked, Type? documentType)
    {
        return localLocked
            && string.Equals(layer.Name, FileConfigLayer.LayerName, StringComparison.Ordinal)
            && documentType != typeof(AgentSettingsDocument);
    }
}
