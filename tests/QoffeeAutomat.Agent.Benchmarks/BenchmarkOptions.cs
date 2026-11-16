using System.Globalization;

namespace QoffeeAutomat.Agent.Benchmarks;

/// <summary>The command line of the benchmark.</summary>
/// <param name="DurationSeconds">How long the measurement lasts.</param>
/// <param name="WarmupSeconds">How long the agent runs before the measurement starts (start-up, JIT, the snapshot).</param>
/// <param name="MetricCount">The number of metrics that the client subscribes to.</param>
/// <param name="SummaryFile">A markdown file that the report is appended to, for example <c>$GITHUB_STEP_SUMMARY</c>.</param>
/// <param name="ShowHelp">True for <c>--help</c>.</param>
/// <param name="Problem">A message when the command line is invalid.</param>
internal sealed record BenchmarkOptions(
    int DurationSeconds,
    int WarmupSeconds,
    int MetricCount,
    string? SummaryFile,
    bool ShowHelp,
    string? Problem)
{
    public const int DefaultDurationSeconds = 60;
    public const int DefaultWarmupSeconds = 5;
    public const int DefaultMetricCount = 20;

    public const string Usage = """
        Usage: QoffeeAutomat.Agent.Benchmarks [options]

        Starts the agent with the simulated platform, connects one QoffeeLink client that is subscribed
        to 20 metrics and measures CPU, memory and state frames (PLAN.md S14). Exits with 1 when a
        budget is exceeded by more than its tolerance.

        Options:
          --duration <seconds>  Length of the measurement (default 60).
          --warmup <seconds>    Time before the measurement starts (default 5).
          --metrics <count>     Number of subscribed metrics (default 20).
          --summary <file>      Append a markdown report to this file (for $GITHUB_STEP_SUMMARY).
          --help                Show this text.
        """;

    /// <summary>Parses the arguments; a problem is reported in <see cref="Problem"/> instead of an exception.</summary>
    public static BenchmarkOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        int duration = DefaultDurationSeconds;
        int warmup = DefaultWarmupSeconds;
        int metrics = DefaultMetricCount;
        string? summary = null;
        bool help = false;

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            int equals = argument.IndexOf('=', StringComparison.Ordinal);
            string name = equals < 0 ? argument : argument[..equals];
            string? inline = equals < 0 ? null : argument[(equals + 1)..];

            switch (name)
            {
                case "--help":
                case "-h":
                    help = true;
                    break;
                case "--duration":
                    if (!TryReadNumber(args, ref index, inline, 1, 3600, out duration))
                    {
                        return Fail("--duration needs a number of seconds between 1 and 3600.");
                    }

                    break;
                case "--warmup":
                    if (!TryReadNumber(args, ref index, inline, 0, 600, out warmup))
                    {
                        return Fail("--warmup needs a number of seconds between 0 and 600.");
                    }

                    break;
                case "--metrics":
                    if (!TryReadNumber(args, ref index, inline, 1, 500, out metrics))
                    {
                        return Fail("--metrics needs a number between 1 and 500.");
                    }

                    break;
                case "--summary":
                    summary = inline ?? (index + 1 < args.Count ? args[++index] : null);
                    if (string.IsNullOrWhiteSpace(summary))
                    {
                        return Fail("--summary needs a file name.");
                    }

                    break;
                default:
                    return Fail($"Unknown argument '{argument}'. Try --help.");
            }
        }

        return new BenchmarkOptions(duration, warmup, metrics, summary, help, null);
    }

    private static bool TryReadNumber(IReadOnlyList<string> args, ref int index, string? inline, int min, int max, out int value)
    {
        string? text = inline ?? (index + 1 < args.Count ? args[++index] : null);
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    }

    private static BenchmarkOptions Fail(string problem)
    {
        return new BenchmarkOptions(DefaultDurationSeconds, DefaultWarmupSeconds, DefaultMetricCount, null, false, problem);
    }
}
