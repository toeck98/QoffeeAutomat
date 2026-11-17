using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Metrics;
using LinkManifest = QoffeeAutomat.Contracts.Link.Manifest;

namespace QoffeeAutomat.Core.Manifest;

/// <summary>
/// Builds the manifest (PLAN.md S06, S07) from the agent identity, the metric catalog, the command documents
/// and the event catalog. Pure and deterministic: the same inputs give the same manifest and so the same
/// <c>rev</c>, whatever order the inputs arrive in. Only what Home Assistant needs goes in:
/// <list type="bullet">
/// <item>metrics that are exposed to Home Assistant (<see cref="MetricCatalogEntry.ExposeToHa"/>), with the effective interval and deadband;</item>
/// <item>commands that are enabled and exposed, with their variables and control mapping; a command without a control, or with the control <c>none</c>, has no <c>control</c> member; the options of an enum variable with <c>optionsFrom</c> (T713) are the current options of that metric, so they follow the state of the PC and <c>rev</c> changes with them;</item>
/// <item>the catalogued events.</item>
/// </list>
/// Everything that does not change what a client can see or do is left out, so editing only the script of a
/// command (its inline code, its script file, its timeout, concurrency or <c>runAs</c>) never changes <c>rev</c>.
/// Lists are sorted by id (ordinal) so that the order in which providers or the file system enumerate things
/// cannot cause a spurious <c>manifest_changed</c>.
/// </summary>
public static class ManifestBuilder
{
    /// <summary>Builds the manifest and computes its revision.</summary>
    /// <param name="agent">The agent identity.</param>
    /// <param name="metrics">The metric catalog, including metrics that are not exposed (they are skipped).</param>
    /// <param name="commands">All command documents, including disabled and unexposed ones (they are skipped).</param>
    /// <param name="events">The event types that the agent can raise.</param>
    /// <param name="features">Optional features of the agent; null or empty leaves the member out.</param>
    /// <returns>The manifest with the correct <see cref="LinkManifest.Rev"/>.</returns>
    public static LinkManifest Build(
        AgentInfo agent,
        IEnumerable<MetricCatalogEntry> metrics,
        IEnumerable<CommandDocument> commands,
        IEnumerable<EventTypeInfo> events,
        IEnumerable<string>? features = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(events);

        MetricCatalogEntry[] catalog = [.. metrics];
        MetricDescriptor[] metricDescriptors =
        [
            .. catalog
                .Where(entry => entry.ExposeToHa)
                .Select(entry => entry.Descriptor)
                .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal),
        ];

        // The options of every metric of the catalog, exposed or not: a variable may take its options from a metric that only
        // the commands use (optionsFrom, T713).
        Dictionary<string, IReadOnlyList<string>> metricOptions = OptionsOf(catalog);

        CommandDescriptor[] commandDescriptors =
        [
            .. commands
                .Where(command => command.Enabled && command.ExposeToHa)
                .OrderBy(command => command.Id, StringComparer.Ordinal)
                .Select(command => Describe(command, metricOptions)),
        ];

        EventDescriptor[] eventDescriptors =
        [
            .. events
                .OrderBy(entry => entry.Type, StringComparer.Ordinal)
                .Select(entry => new EventDescriptor(entry.Type, entry.Name)),
        ];

        string[] featureList = [.. (features ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        var manifest = new LinkManifest(
            string.Empty,
            Normalize(agent),
            metricDescriptors,
            commandDescriptors,
            eventDescriptors,
            featureList.Length == 0 ? null : featureList);
        return ManifestRevision.WithRevision(manifest);
    }

    /// <summary>Describes one command the way the manifest announces it (PLAN.md S06, S07).</summary>
    /// <param name="command">The command document.</param>
    /// <param name="metricOptions">The current options of the enum metrics by metric id, for the variables that have <c>optionsFrom</c> (T713); null, or a metric that is missing from it, gives such a variable no options.</param>
    /// <returns>The descriptor.</returns>
    public static CommandDescriptor Describe(
        CommandDocument command,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? metricOptions = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        VariableDescriptor[] variables = [.. (command.Parameters ?? []).Select(parameter => DescribeVariable(parameter, metricOptions))];
        return new CommandDescriptor(
            command.Id,
            command.Name,
            command.Icon,
            command.Description,
            command.CaptureOutput ? true : null,
            variables.Length == 0 ? null : variables,
            DescribeControl(command.HaControl));
    }

    private static Dictionary<string, IReadOnlyList<string>> OptionsOf(IEnumerable<MetricCatalogEntry> catalog)
    {
        var options = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (MetricCatalogEntry entry in catalog)
        {
            if (entry.Descriptor.Options is { } list)
            {
                options[entry.Id] = list;
            }
        }

        return options;
    }

    /// <summary>
    /// <c>optionsFrom</c> replaces <c>options</c>: the variable gets the current options of the metric. A metric without options
    /// (unknown, not an enum, or nothing to offer yet, for example no audio device) gives the variable none, because the manifest
    /// schema does not allow an empty list; the consumer then has nothing to offer until options appear and <c>rev</c> changes.
    /// </summary>
    private static VariableDescriptor DescribeVariable(ParameterDefinition parameter, IReadOnlyDictionary<string, IReadOnlyList<string>>? metricOptions)
    {
        IReadOnlyList<string>? options = parameter.Options;
        if (!string.IsNullOrEmpty(parameter.OptionsFrom))
        {
            options = metricOptions is not null
                && metricOptions.TryGetValue(parameter.OptionsFrom, out IReadOnlyList<string>? current)
                && current.Count > 0
                    ? current
                    : null;
        }

        return new VariableDescriptor(
            parameter.Name,
            parameter.Type,
            parameter.Required ? true : null,
            parameter.Default,
            parameter.Min,
            parameter.Max,
            parameter.Step,
            parameter.Pattern,
            parameter.MaxLength,
            options,
            parameter.Secret ? true : null,
            parameter.Unit,
            parameter.Description);
    }

    private static ControlDescriptor? DescribeControl(HaControl? control)
    {
        if (control is null || string.Equals(control.Type, HaControlTypes.None, StringComparison.Ordinal))
        {
            return null;
        }

        return new ControlDescriptor(control.Type, control.Parameter, control.StateMetric);
    }

    /// <summary>The MAC addresses are a set: sorted and without duplicates, so the order of the adapters does not matter.</summary>
    private static AgentInfo Normalize(AgentInfo agent)
    {
        string[] macs = [.. (agent.Macs ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];
        return agent with { Macs = macs };
    }
}
