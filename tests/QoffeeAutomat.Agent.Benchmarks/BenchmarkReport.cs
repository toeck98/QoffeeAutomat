using System.Globalization;
using System.Text;

namespace QoffeeAutomat.Agent.Benchmarks;

/// <summary>What one run measured.</summary>
/// <param name="Metrics">The number of subscribed metrics.</param>
/// <param name="DurationSeconds">The measured wall-clock time.</param>
/// <param name="CpuSeconds">The processor time that the process used during the measurement.</param>
/// <param name="PeakWorkingSetMiB">The largest working set that was sampled during the measurement.</param>
/// <param name="StateFrames">The state frames that arrived during the measurement (the first snapshot is not counted).</param>
/// <param name="StateBytes">The size of those frames together.</param>
/// <param name="SnapshotBytes">The size of the first full snapshot after the subscription.</param>
internal sealed record BenchmarkResult(
    int Metrics,
    double DurationSeconds,
    double CpuSeconds,
    double PeakWorkingSetMiB,
    long StateFrames,
    long StateBytes,
    int SnapshotBytes)
{
    /// <summary>The processor time as a percentage of one core.</summary>
    public double CpuPercent => DurationSeconds <= 0 ? 0 : CpuSeconds / DurationSeconds * 100d;

    /// <summary>The mean size of a state frame in bytes.</summary>
    public double AverageStateFrameBytes => StateFrames == 0 ? 0 : (double)StateBytes / StateFrames;

    /// <summary>The number of state frames per second.</summary>
    public double FramesPerSecond => DurationSeconds <= 0 ? 0 : StateFrames / DurationSeconds;
}

/// <summary>How a measured value compares with its budget.</summary>
internal enum BudgetOutcome
{
    /// <summary>At or below the budget.</summary>
    Within,

    /// <summary>Above the budget but not above the tolerance.</summary>
    Tolerated,

    /// <summary>Above the tolerance: the benchmark fails.</summary>
    Exceeded,
}

/// <summary>One line of the report.</summary>
/// <param name="Name">What was measured.</param>
/// <param name="Value">The measured value.</param>
/// <param name="Budget">The budget of PLAN.md S14.</param>
/// <param name="Limit">The budget plus the tolerance; above it the benchmark fails.</param>
/// <param name="Unit">The unit of the three numbers.</param>
/// <param name="Outcome">The judgement.</param>
internal sealed record BudgetCheck(string Name, double Value, double Budget, double Limit, string Unit, BudgetOutcome Outcome);

/// <summary>
/// The budgets of PLAN.md S14 for an idle agent with 20 subscribed metrics (under 1 % of one core, under 120 MB working
/// set, a typical state frame under 300 bytes) and the tolerance that a shared CI machine needs. The processor time
/// is the one of the whole benchmark process, which hosts the agent and the client, and a few seconds of measurement
/// are noisy, so it has the widest tolerance.
/// </summary>
internal static class BudgetEvaluator
{
    public const double CpuPercentBudget = 1d;
    public const double CpuTolerance = 1d;
    public const double WorkingSetMiBBudget = 120d;
    public const double WorkingSetTolerance = 0.25d;
    public const double StateFrameBytesBudget = 300d;
    public const double StateFrameTolerance = 0.25d;

    /// <summary>Compares a result with the budgets.</summary>
    public static IReadOnlyList<BudgetCheck> Evaluate(BenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return
        [
            Check("CPU (percent of one core)", result.CpuPercent, CpuPercentBudget, CpuTolerance, "%"),
            Check("Working set (peak)", result.PeakWorkingSetMiB, WorkingSetMiBBudget, WorkingSetTolerance, "MiB"),
            Check("State frame (average)", result.AverageStateFrameBytes, StateFrameBytesBudget, StateFrameTolerance, "bytes"),
        ];
    }

    private static BudgetCheck Check(string name, double value, double budget, double tolerance, string unit)
    {
        double limit = budget * (1d + tolerance);
        BudgetOutcome outcome = value <= budget ? BudgetOutcome.Within : value <= limit ? BudgetOutcome.Tolerated : BudgetOutcome.Exceeded;
        return new BudgetCheck(name, value, budget, limit, unit, outcome);
    }
}

/// <summary>Writes the report to the console and to a markdown file.</summary>
internal static class BenchmarkReport
{
    /// <summary>The report as plain text.</summary>
    public static string ToText(BenchmarkResult result, IReadOnlyList<BudgetCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(checks);

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Measured {result.DurationSeconds:F1} s with {result.Metrics} subscribed metrics.");
        text.AppendLine(CultureInfo.InvariantCulture, $"CPU time {result.CpuSeconds:F3} s, {result.StateFrames} state frames ({result.FramesPerSecond:F2} per second), first snapshot {result.SnapshotBytes} bytes.");
        foreach (BudgetCheck check in checks)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {check.Name,-28} {check.Value,9:F2} {check.Unit,-5} budget {check.Budget:F0}, limit {check.Limit:F2}: {Describe(check.Outcome)}");
        }

        return text.ToString();
    }

    /// <summary>The report as markdown for the job summary.</summary>
    public static string ToMarkdown(BenchmarkResult result, IReadOnlyList<BudgetCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(checks);

        var text = new StringBuilder();
        text.AppendLine("### Performance budget (idle agent, simulated platform)");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"One QoffeeLink client subscribed to {result.Metrics} metrics for {result.DurationSeconds:F0} seconds.");
        text.AppendLine();
        text.AppendLine("| Measurement | Value | Budget | Limit with tolerance | Result |");
        text.AppendLine("|---|---:|---:|---:|---|");
        foreach (BudgetCheck check in checks)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {check.Name} | {check.Value:F2} {check.Unit} | {check.Budget:F0} {check.Unit} | {check.Limit:F2} {check.Unit} | {Describe(check.Outcome)} |");
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"State frames: {result.StateFrames} ({result.FramesPerSecond:F2} per second), CPU time {result.CpuSeconds:F3} s, first snapshot {result.SnapshotBytes} bytes.");
        return text.ToString();
    }

    private static string Describe(BudgetOutcome outcome)
    {
        return outcome switch
        {
            BudgetOutcome.Within => "within budget",
            BudgetOutcome.Tolerated => "over budget, within tolerance",
            _ => "EXCEEDED",
        };
    }
}
