using System.Diagnostics;

namespace QoffeeAutomat.Agent.Benchmarks;

/// <summary>
/// The performance budget benchmark (PLAN.md S14, T218): the agent with the simulated platform and one QoffeeLink client
/// that is subscribed to 20 metrics. The exit code is 0 when every value is within its budget plus tolerance, 1 when
/// a budget is exceeded or the run is not meaningful (no state frames, the connection ended), and 2 for a bad
/// command line or a failure to start.
/// </summary>
internal static class BenchmarkProgram
{
    private const int Success = 0;
    private const int BudgetExceeded = 1;
    private const int Usage = 2;

    public static async Task<int> Main(string[] args)
    {
        BenchmarkOptions options = BenchmarkOptions.Parse(args);
        if (options.Problem is not null)
        {
            await Console.Error.WriteLineAsync(options.Problem);
            return Usage;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(BenchmarkOptions.Usage);
            return Success;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.WarmupSeconds + options.DurationSeconds + 180));
        try
        {
            BenchmarkResult result = await MeasureAsync(options, cancellation.Token);
            IReadOnlyList<BudgetCheck> checks = BudgetEvaluator.Evaluate(result);
            Console.Write(BenchmarkReport.ToText(result, checks));
            if (options.SummaryFile is not null)
            {
                await File.AppendAllTextAsync(options.SummaryFile, BenchmarkReport.ToMarkdown(result, checks) + Environment.NewLine, cancellation.Token);
            }

            if (result.StateFrames == 0)
            {
                await Console.Error.WriteLineAsync("No state frame arrived during the measurement; the numbers say nothing about the agent.");
                return BudgetExceeded;
            }

            return checks.Any(check => check.Outcome == BudgetOutcome.Exceeded) ? BudgetExceeded : Success;
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or OperationCanceledException or IOException or System.Net.WebSockets.WebSocketException)
        {
            await Console.Error.WriteLineAsync("The benchmark failed: " + exception.Message);
            return Usage;
        }
    }

    private static async Task<BenchmarkResult> MeasureAsync(BenchmarkOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Starting the agent (simulated platform), {options.MetricCount} metrics, warm-up {options.WarmupSeconds} s, measurement {options.DurationSeconds} s.");
        await using AgentUnderTest agent = await AgentUnderTest.StartAsync(options.MetricCount, cancellationToken);
        (string token, string fingerprint) = await agent.PairAsync(cancellationToken);
        await using LinkProbe probe = await LinkProbe.ConnectAsync(agent.LinkPort, token, fingerprint, agent.Metrics, cancellationToken);

        await Task.Delay(TimeSpan.FromSeconds(options.WarmupSeconds), cancellationToken);
        if (!probe.IsConnected)
        {
            throw new InvalidOperationException("The link connection ended during the warm-up: " + probe.CloseReason);
        }

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpuStart = process.TotalProcessorTime;
        (long framesStart, long bytesStart) = probe.Counters;
        long peakWorkingSet = process.WorkingSet64;
        var clock = Stopwatch.StartNew();
        var length = TimeSpan.FromSeconds(options.DurationSeconds);
        while (clock.Elapsed < length)
        {
            TimeSpan step = TimeSpan.FromSeconds(1);
            TimeSpan left = length - clock.Elapsed;
            await Task.Delay(left < step ? left : step, cancellationToken);
            process.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        }

        clock.Stop();
        process.Refresh();
        TimeSpan cpuEnd = process.TotalProcessorTime;
        (long framesEnd, long bytesEnd) = probe.Counters;
        if (!probe.IsConnected)
        {
            throw new InvalidOperationException("The link connection ended during the measurement: " + probe.CloseReason);
        }

        return new BenchmarkResult(
            options.MetricCount,
            clock.Elapsed.TotalSeconds,
            (cpuEnd - cpuStart).TotalSeconds,
            peakWorkingSet / (1024d * 1024d),
            framesEnd - framesStart,
            bytesEnd - bytesStart,
            probe.SnapshotBytes);
    }
}
