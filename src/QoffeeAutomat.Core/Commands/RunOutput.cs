using System.Runtime.CompilerServices;
using System.Threading.Channels;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Commands;

/// <summary>
/// One item of the live output of a run: a piece of output, or the end of the run. Exactly one of the
/// two members is set.
/// </summary>
/// <param name="Chunk">A piece of output.</param>
/// <param name="Final">The outcome, as the last item.</param>
public readonly record struct RunOutputItem(ProcessOutputChunk? Chunk, RunResult? Final);

/// <summary>
/// The live output of the runs that have not finished (PLAN.md S08 <c>/runs/{id}/stream</c>, T214). The
/// command engine registers a run when it accepts it, feeds the output of its process while it runs and
/// removes the run when it ends. A subscriber that arrives late first receives everything that was output
/// so far ("replay from the start"), then the live output, then the end. Finished runs are not kept; their
/// output is in the run history.
/// </summary>
public sealed class RunOutputHub
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RunOutputFeed> _feeds = new(StringComparer.Ordinal);
    private readonly int _maxBufferedChars;

    /// <summary>The default for the number of characters that a feed keeps for replay.</summary>
    public const int DefaultMaxBufferedChars = 256 * 1024;

    /// <summary>Creates the hub.</summary>
    /// <param name="maxBufferedChars">How many characters of output a run keeps for subscribers that arrive late; later output is still delivered live.</param>
    public RunOutputHub(int maxBufferedChars = DefaultMaxBufferedChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBufferedChars, 1);

        _maxBufferedChars = maxBufferedChars;
    }

    /// <summary>Registers a run. Called by the command engine when it accepts the run.</summary>
    /// <param name="runId">The run id.</param>
    /// <returns>The feed that receives the output.</returns>
    public RunOutputFeed Register(string runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        var feed = new RunOutputFeed(runId, _maxBufferedChars);
        lock (_gate)
        {
            _feeds[runId] = feed;
        }

        return feed;
    }

    /// <summary>Finds a run that has not finished.</summary>
    /// <param name="runId">The run id.</param>
    /// <returns>The feed, or null when the run is unknown or has finished.</returns>
    public RunOutputFeed? Find(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);

        lock (_gate)
        {
            return _feeds.GetValueOrDefault(runId);
        }
    }

    /// <summary>Ends a run: its subscribers receive the outcome and the run is forgotten.</summary>
    /// <param name="runId">The run id.</param>
    /// <param name="result">The outcome.</param>
    /// <param name="standardOutput">The output of the run as it is stored in the history (redacted); shown when no live output arrived.</param>
    /// <param name="standardError">The error output, see <paramref name="standardOutput"/>.</param>
    public void Finish(string runId, RunResult result, string standardOutput, string standardError)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(result);

        RunOutputFeed? feed;
        lock (_gate)
        {
            _feeds.Remove(runId, out feed);
        }

        feed?.Complete(result, standardOutput, standardError);
    }
}

/// <summary>The live output of one run; see <see cref="RunOutputHub"/>.</summary>
public sealed class RunOutputFeed
{
    private readonly object _gate = new();
    private readonly List<ProcessOutputChunk> _chunks = [];
    private readonly List<ChannelWriter<RunOutputItem>> _subscribers = [];
    private readonly int _maxBufferedChars;
    private int _bufferedChars;
    private bool _truncated;
    private RunResult? _final;

    internal RunOutputFeed(string runId, int maxBufferedChars)
    {
        RunId = runId;
        _maxBufferedChars = maxBufferedChars;
    }

    /// <summary>The run id.</summary>
    public string RunId { get; }

    /// <summary>True when the run ended.</summary>
    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _final is not null;
            }
        }
    }

    /// <summary>True when the replay buffer is full, so a subscriber that arrives late misses the middle of the output.</summary>
    public bool IsReplayTruncated
    {
        get
        {
            lock (_gate)
            {
                return _truncated;
            }
        }
    }

    /// <summary>Adds output. Called from the thread that reads the process.</summary>
    /// <param name="chunk">The output.</param>
    public void Append(ProcessOutputChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        lock (_gate)
        {
            if (_final is not null)
            {
                return;
            }

            AddChunk(chunk);
        }
    }

    /// <summary>
    /// Reads the output of the run: everything so far, then what follows, and finally the outcome. Several
    /// readers may read the same run; each gets the whole output. The sequence ends after the outcome.
    /// </summary>
    /// <param name="cancellationToken">Stops reading; the run is not affected.</param>
    /// <returns>The output items.</returns>
    public async IAsyncEnumerable<RunOutputItem> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Channel<RunOutputItem> channel = Channel.CreateUnbounded<RunOutputItem>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate)
        {
            foreach (ProcessOutputChunk chunk in _chunks)
            {
                channel.Writer.TryWrite(new RunOutputItem(chunk, null));
            }

            if (_final is { } final)
            {
                channel.Writer.TryWrite(new RunOutputItem(null, final));
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel.Writer);
            }
        }

        try
        {
            await foreach (RunOutputItem item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel.Writer);
            }
        }
    }

    internal void Complete(RunResult result, string standardOutput, string standardError)
    {
        lock (_gate)
        {
            if (_final is not null)
            {
                return;
            }

            if (_chunks.Count == 0)
            {
                // The process ran where no live output is available (another session, elevated) or the
                // output of a secret variable was held back: show what the history keeps.
                if (standardOutput.Length > 0)
                {
                    AddChunk(new ProcessOutputChunk(ProcessOutputSource.StandardOutput, standardOutput, result.StartedAt));
                }

                if (standardError.Length > 0)
                {
                    AddChunk(new ProcessOutputChunk(ProcessOutputSource.StandardError, standardError, result.StartedAt));
                }
            }

            _final = result;
            foreach (ChannelWriter<RunOutputItem> writer in _subscribers)
            {
                writer.TryWrite(new RunOutputItem(null, result));
                writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    private void AddChunk(ProcessOutputChunk chunk)
    {
        if (_bufferedChars + chunk.Text.Length <= _maxBufferedChars)
        {
            _chunks.Add(chunk);
            _bufferedChars += chunk.Text.Length;
        }
        else
        {
            _truncated = true;
        }

        foreach (ChannelWriter<RunOutputItem> writer in _subscribers)
        {
            writer.TryWrite(new RunOutputItem(chunk, null));
        }
    }
}

/// <summary>
/// Wraps a launcher so that the output of the processes it starts also goes to a run's feed while the
/// process runs. Only processes that the agent supervises itself (<c>runAs: system</c>) stream; the others
/// deliver their output when they end.
/// </summary>
internal sealed class OutputTapLauncher : IProcessLauncher
{
    private readonly IProcessLauncher _inner;
    private readonly RunOutputFeed _feed;

    public OutputTapLauncher(IProcessLauncher inner, RunOutputFeed feed)
    {
        _inner = inner;
        _feed = feed;
    }

    public ValueTask<ExecutionResult> LaunchAsync(string runAs, ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _inner.LaunchAsync(runAs, request with { OnOutput = _feed.Append }, cancellationToken);
    }
}
