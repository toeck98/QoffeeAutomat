using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Core.Config;

/// <summary>
/// The effective view over an ordered list of layers (highest priority first): on equal kind and id
/// the first layer that has the item wins (PLAN.md D-028). With only the local layer it is that
/// layer's content.
/// </summary>
internal sealed class LayeredConfigView : IConfigView
{
    private readonly IReadOnlyList<IConfigLayer> _layers;

    public LayeredConfigView(IReadOnlyList<IConfigLayer> layers)
    {
        _layers = layers;
    }

    public T? GetDocument<T>(string id)
        where T : class, IConfigDocument
    {
        foreach (IConfigLayer layer in _layers)
        {
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
        if (_layers.Count == 1)
        {
            return _layers[0].List<T>();
        }

        var byId = new SortedDictionary<string, T>(StringComparer.Ordinal);
        foreach (IConfigLayer layer in _layers)
        {
            foreach (T document in layer.List<T>())
            {
                string? id = document switch
                {
                    CommandDocument command => command.Id,
                    SensorDocument sensor => sensor.Id,
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
        if (_layers.Count == 1)
        {
            return await _layers[0].ListScriptsAsync(cancellationToken).ConfigureAwait(false);
        }

        var byName = new SortedDictionary<string, ScriptInfo>(StringComparer.Ordinal);
        foreach (IConfigLayer layer in _layers)
        {
            foreach (ScriptInfo script in await layer.ListScriptsAsync(cancellationToken).ConfigureAwait(false))
            {
                byName.TryAdd(script.Name, script);
            }
        }

        return [.. byName.Values];
    }

    public async Task<ScriptFile?> GetScriptAsync(string name, CancellationToken cancellationToken)
    {
        foreach (IConfigLayer layer in _layers)
        {
            ScriptFile? script = await layer.GetScriptAsync(name, cancellationToken).ConfigureAwait(false);
            if (script is not null)
            {
                return script;
            }
        }

        return null;
    }
}
