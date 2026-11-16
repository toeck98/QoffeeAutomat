using QoffeeAutomat.Core.Execution;
using static QoffeeAutomat.Core.Tests.Execution.ProcessTestSupport;

namespace QoffeeAutomat.Core.Tests.Execution;

/// <summary>The live output of a running process, for the UI (PLAN.md T206).</summary>
[Trait("Category", "ProcessRunner")]
public sealed class ProcessRunnerOutputStreamTests
{
    private const string WaitsForFile = "echo ready; while [ ! -e \"$1\" ]; do sleep 0.05; done; echo done >&2; echo finished";

    private static async Task<List<ProcessOutputChunk>> ReadAllAsync(IAsyncEnumerable<ProcessOutputChunk> output)
    {
        var chunks = new List<ProcessOutputChunk>();
        using var timeout = new CancellationTokenSource(Patience);
        await foreach (ProcessOutputChunk chunk in output.WithCancellation(timeout.Token))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static string Text(IEnumerable<ProcessOutputChunk> chunks, ProcessOutputSource source)
    {
        return string.Concat(chunks.Where(chunk => chunk.Source == source).Select(chunk => chunk.Text));
    }

    [UnixFact]
    public async Task Output_is_streamed_while_the_process_is_still_running()
    {
        using var temp = new TempDirectory();
        string go = temp.Combine("go");
        var runner = new ProcessRunner();

        await using IProcessRun run = runner.Start(Sh(WaitsForFile, [go]));
        await using IAsyncEnumerator<ProcessOutputChunk> chunks = run.Output.GetAsyncEnumerator();

        Assert.True(await chunks.MoveNextAsync().AsTask().WaitAsync(Patience));
        ProcessOutputChunk first = chunks.Current;
        Assert.Equal(ProcessOutputSource.StandardOutput, first.Source);
        Assert.Equal("ready\n", first.Text);
        Assert.False(run.Completion.IsCompleted);

        await File.WriteAllTextAsync(go, string.Empty);
        var rest = new List<ProcessOutputChunk>();
        while (await chunks.MoveNextAsync().AsTask().WaitAsync(Patience))
        {
            rest.Add(chunks.Current);
        }

        Assert.Equal("done\n", Text(rest, ProcessOutputSource.StandardError));
        Assert.Equal("finished\n", Text(rest, ProcessOutputSource.StandardOutput));
        ProcessRunResult result = await run.Completion;
        Assert.Equal("ready\nfinished\n", result.StandardOutput);
        Assert.Equal("done\n", result.StandardError);
    }

    [UnixFact]
    public async Task The_stream_ends_when_the_run_ends()
    {
        var runner = new ProcessRunner();

        await using IProcessRun run = runner.Start(Sh("echo one; echo two >&2"));
        List<ProcessOutputChunk> chunks = await ReadAllAsync(run.Output);

        Assert.Equal("one\n", Text(chunks, ProcessOutputSource.StandardOutput));
        Assert.Equal("two\n", Text(chunks, ProcessOutputSource.StandardError));
        Assert.True((await run.Completion).Succeeded);
    }

    [UnixFact]
    public async Task Output_that_nobody_reads_does_not_stop_the_run()
    {
        var runner = new ProcessRunner();

        ProcessRunResult result = await runner.RunAsync(Sh("echo one; echo two >&2"));

        Assert.True(result.Succeeded);
        Assert.Equal("one\n", result.StandardOutput);
        Assert.Equal("two\n", result.StandardError);
    }

    [UnixFact]
    public async Task The_cap_applies_to_the_live_stream_as_well()
    {
        var runner = new ProcessRunner();

        await using IProcessRun run = runner.Start(Sh("head -c 102400 /dev/zero | tr '\\0' a"));
        List<ProcessOutputChunk> chunks = await ReadAllAsync(run.Output);

        Assert.Equal(64 * 1024, Text(chunks, ProcessOutputSource.StandardOutput).Length);
        Assert.True((await run.Completion).OutputTruncated);
    }

    [UnixFact]
    public async Task Output_that_is_not_captured_is_not_streamed()
    {
        var runner = new ProcessRunner();

        await using IProcessRun run = runner.Start(Sh("echo hidden", captureOutput: false));
        List<ProcessOutputChunk> chunks = await ReadAllAsync(run.Output);

        Assert.Empty(chunks);
        Assert.True((await run.Completion).Succeeded);
    }

    [UnixFact]
    public async Task A_run_that_could_not_start_ends_the_stream_too()
    {
        var runner = new ProcessRunner();

        await using IProcessRun run = runner.Start(Request("/definitely/not/here/tool"));
        List<ProcessOutputChunk> chunks = await ReadAllAsync(run.Output);

        Assert.Empty(chunks);
        Assert.NotNull((await run.Completion).StartError);
    }
}
