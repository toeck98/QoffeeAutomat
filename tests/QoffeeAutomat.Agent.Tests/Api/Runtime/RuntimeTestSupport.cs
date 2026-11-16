using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Tests.Api.Runtime;

/// <summary>
/// A process runner that starts no process: a test emits the output of the "process" and decides when it ends. With
/// <c>autoFinish</c> every run ends at once with <see cref="ExitCode"/> and one line of output.
/// </summary>
internal sealed class ControlledProcessRunner : IProcessRunner
{
    private readonly object _gate = new();
    private readonly List<ControlledProcessRun> _runs = [];
    private readonly bool _autoFinish;

    public ControlledProcessRunner(bool autoFinish = false)
    {
        _autoFinish = autoFinish;
    }

    /// <summary>The exit code of runs that end by themselves.</summary>
    public int ExitCode { get; set; }

    /// <summary>The runs that were started, in order.</summary>
    public ControlledProcessRun[] Runs
    {
        get
        {
            lock (_gate)
            {
                return [.. _runs];
            }
        }
    }

    public IProcessRun Start(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        var run = new ControlledProcessRun(request);
        lock (_gate)
        {
            _runs.Add(run);
        }

        if (_autoFinish)
        {
            run.Emit(ProcessOutputSource.StandardOutput, "done\n");
            run.Finish(ExitCode, "done\n", string.Empty);
        }

        return run;
    }

    public async Task<ProcessRunResult> RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
    {
        await using IProcessRun run = Start(request, cancellationToken);
        return await run.Completion;
    }

    /// <summary>Waits until the number of started runs is at least <paramref name="count"/> and returns the last one.</summary>
    public async Task<ControlledProcessRun> WaitForRunAsync(int count = 1)
    {
        await TestWait.UntilAsync(() => Runs.Length >= count, "the agent to start a process");
        return Runs[count - 1];
    }
}

/// <summary>One "process" of the <see cref="ControlledProcessRunner"/>.</summary>
internal sealed class ControlledProcessRun : IProcessRun
{
    private readonly Channel<ProcessOutputChunk> _output = Channel.CreateUnbounded<ProcessOutputChunk>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<ProcessRunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ControlledProcessRun(ProcessLaunchRequest request)
    {
        Request = request;
    }

    public ProcessLaunchRequest Request { get; }

    public IAsyncEnumerable<ProcessOutputChunk> Output => _output.Reader.ReadAllAsync();

    public Task<ProcessRunResult> Completion => _completion.Task;

    public void Emit(ProcessOutputSource source, string text)
    {
        _output.Writer.TryWrite(new ProcessOutputChunk(source, text, DateTimeOffset.UtcNow));
    }

    public void Finish(int exitCode, string standardOutput, string standardError)
    {
        _output.Writer.TryComplete();
        _completion.TrySetResult(new ProcessRunResult(exitCode, standardOutput, standardError, TimeSpan.FromMilliseconds(5), TimedOut: false, OutputTruncated: false));
    }

    public void Cancel()
    {
        _output.Writer.TryComplete();
        _completion.TrySetResult(new ProcessRunResult(-1, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false, OutputTruncated: false, Cancelled: true));
    }

    public ValueTask DisposeAsync()
    {
        Cancel();
        return ValueTask.CompletedTask;
    }
}

/// <summary>One event of a Server-Sent Events stream.</summary>
/// <param name="Name">The event name.</param>
/// <param name="Data">The parsed JSON data.</param>
internal sealed record ServerEvent(string Name, JsonNode Data);

/// <summary>Reads a Server-Sent Events response while it is being written.</summary>
internal sealed class ServerEventReader : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;

    private ServerEventReader(HttpResponseMessage response, StreamReader reader)
    {
        _response = response;
        _reader = reader;
    }

    public HttpResponseMessage Response => _response;

    /// <summary>Sends the request and returns when the response headers have arrived.</summary>
    public static async Task<ServerEventReader> OpenAsync(ApiTestHost host, string path)
    {
        HttpResponseMessage response = await host.SendAsync(HttpMethod.Get, path, completion: HttpCompletionOption.ResponseHeadersRead);
        Stream stream = await response.Content.ReadAsStreamAsync();
        return new ServerEventReader(response, new StreamReader(stream));
    }

    /// <summary>Reads the next event; fails the test when none arrives in time.</summary>
    public async Task<ServerEvent> ReadAsync(TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TestWait.DefaultTimeout);
        string? name = null;
        string? data = null;
        try
        {
            while (true)
            {
                string? line = await _reader.ReadLineAsync(cancellation.Token);
                if (line is null)
                {
                    throw new EndOfStreamException("The event stream ended.");
                }

                if (line.Length == 0)
                {
                    if (name is not null)
                    {
                        return new ServerEvent(name, JsonNode.Parse(data ?? "null") ?? new JsonObject());
                    }

                    continue;
                }

                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    name = line["event: ".Length..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line["data: ".Length..];
                }
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("Timed out waiting for the next event of the stream.");
            throw;
        }
    }

    /// <summary>True when the server has ended the stream (the next read finds the end).</summary>
    public async Task<bool> HasEndedAsync()
    {
        using var cancellation = new CancellationTokenSource(TestWait.DefaultTimeout);
        string? line = await _reader.ReadLineAsync(cancellation.Token);
        return line is null;
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Starts the agent with a controlled process runner.</summary>
internal static class RuntimeHost
{
    public static Task<ApiTestHost> StartAsync(ControlledProcessRunner runner)
    {
        return ApiTestHost.StartAsync(services =>
        {
            services.RemoveAll<IProcessRunner>();
            services.AddSingleton<IProcessRunner>(runner);
        });
    }
}
