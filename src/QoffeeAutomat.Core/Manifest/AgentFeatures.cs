using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Manifest;

/// <summary>
/// The optional features that an agent announces in <c>hello.features</c> and in the manifest (PLAN.md S07, S10). A feature
/// tells the Home Assistant integration that it can create an entity which needs the agent to cooperate; the manifest lists
/// the metrics and commands that the entity uses.
/// </summary>
public static class AgentFeatures
{
    /// <summary>The agent can show notifications from Home Assistant (built-in action <c>notify.toast</c>, T702).</summary>
    public const string Notify = "notify";

    /// <summary>The agent publishes the system media session and can control it (metrics <c>media.*</c>, action <c>media.control</c>, T716, D-038).</summary>
    public const string Media = "media";

    /// <summary>The agent can update itself from the newest release and reports the versions (metrics <c>agent.*version</c>, action <c>agent.update</c>, T704).</summary>
    public const string Update = "update";

    /// <summary>
    /// The features that a platform offers, derived from its built-in actions: <see cref="Notify"/> when it has
    /// <c>notify.toast</c>, <see cref="Media"/> when it has <c>media.control</c> and <see cref="Update"/> when it has
    /// <c>agent.update</c>.
    /// </summary>
    /// <param name="actions">The built-in actions of the platform.</param>
    /// <returns>The features in a stable order; empty for a platform that has none of these actions.</returns>
    public static IReadOnlyList<string> FromActions(IBuiltinActionProvider actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        IReadOnlyList<BuiltinActionDescriptor> available = actions.Actions;
        var features = new List<string>(3);
        if (Has(available, MediaControlSpec.ActionName))
        {
            features.Add(Media);
        }

        if (Has(available, NotifyToastSpec.ActionName))
        {
            features.Add(Notify);
        }

        if (Has(available, AgentUpdateSpec.ActionName))
        {
            features.Add(Update);
        }

        return features;
    }

    private static bool Has(IReadOnlyList<BuiltinActionDescriptor> actions, string name)
    {
        return actions.Any(action => string.Equals(action.Name, name, StringComparison.Ordinal));
    }
}
