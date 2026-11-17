using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Ui.Components.Api;

/// <summary>
/// One item of the output stream of a run (<see cref="IAgentApi.StreamRunAsync"/>): either a piece of output or the end of
/// the run, never both.
/// </summary>
/// <param name="Output">A piece of standard output or standard error.</param>
/// <param name="End">The last item of a stream: how the run ended.</param>
public sealed record RunStreamItem(RunOutputEvent? Output = null, RunEndEvent? End = null);
