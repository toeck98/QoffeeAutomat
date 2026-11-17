using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Updates;

/// <summary>
/// Adds the built-in action <c>agent.update</c> (<see cref="AgentUpdateSpec"/>, T704) to the actions of the platform. The action belongs
/// to the agent, not to a platform: it downloads, verifies and starts an installer, so it needs the agent's services. While
/// <see cref="UpdateOptions.Supported"/> is false the action does not exist, and the manifest does not announce the feature
/// <c>update</c>. Every other action goes to the platform unchanged.
/// </summary>
public sealed class UpdateBuiltinActions : IBuiltinActionProvider
{
    private static readonly BuiltinActionDescriptor UpdateDescriptor = new(
        AgentUpdateSpec.ActionName,
        AgentUpdateSpec.ActionTitle,
        RunAsValues.System,
        AgentUpdateSpec.Parameters,
        AgentUpdateSpec.ActionDescription);

    private readonly IBuiltinActionProvider _inner;
    private readonly IAgentUpdater _updater;
    private readonly UpdateOptions _options;

    /// <summary>Creates the decorator.</summary>
    /// <param name="inner">The actions of the platform.</param>
    /// <param name="updater">The self-update.</param>
    /// <param name="options">The parameters; <see cref="UpdateOptions.Supported"/> decides whether the action exists.</param>
    public UpdateBuiltinActions(IBuiltinActionProvider inner, IAgentUpdater updater, UpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(options);

        _inner = inner;
        _updater = updater;
        _options = options;
    }

    /// <inheritdoc />
    public IReadOnlyList<BuiltinActionDescriptor> Actions => _options.Supported ? [.. _inner.Actions, UpdateDescriptor] : _inner.Actions;

    /// <inheritdoc />
    public ValueTask<ExecutionResult> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(name, AgentUpdateSpec.ActionName, StringComparison.Ordinal))
        {
            return _inner.ExecuteAsync(name, variables, cancellationToken);
        }

        return _options.Supported
            ? new ValueTask<ExecutionResult>(_updater.UpdateAsync(cancellationToken))
            : ValueTask.FromResult(ExecutionResult.Failure(ErrorCodes.BadRequest, "This agent cannot update itself on this platform."));
    }
}
