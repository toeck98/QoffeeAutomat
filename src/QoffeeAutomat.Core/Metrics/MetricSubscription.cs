using System.Threading.Channels;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Core.Metrics;

/// <summary>
/// The change feed of one subscriber (a QoffeeLink connection, the UI, the hub). After every
/// <see cref="SetSelection"/> a snapshot of the selected metrics arrives first; later updates carry
/// only changed values, coalesced and delivered at most once per flush interval (PLAN.md S07, R-06).
/// </summary>
public sealed class MetricSubscription : IDisposable
{
    private const int ChannelCapacity = 64;

    private readonly MetricEngine _engine;
    private readonly Channel<MetricUpdate> _channel = Channel.CreateBounded<MetricUpdate>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    internal MetricSubscription(MetricEngine engine)
    {
        _engine = engine;
    }

    /// <summary>The updates in order. The channel completes when the subscription or the engine is disposed.</summary>
    public ChannelReader<MetricUpdate> Updates => _channel.Reader;

    // The members below are only touched while the engine holds its lock.

    internal bool IsAll { get; set; }

    internal HashSet<string> Ids { get; set; } = new(StringComparer.Ordinal);

    internal Dictionary<string, double> Intervals { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The value last delivered for each metric (null counts as a delivered value).</summary>
    internal Dictionary<string, object?> Sent { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, DateTimeOffset> SentAt { get; } = new(StringComparer.Ordinal);

    internal bool SnapshotPending { get; set; }

    internal DateTimeOffset? LastFlush { get; set; }

    internal bool IsDisposed { get; set; }

    /// <summary>Replaces the set of subscribed metrics (PLAN.md S07, <c>subscribe</c>); a full snapshot follows.</summary>
    /// <param name="selection">The metric ids or all metrics.</param>
    /// <param name="intervals">
    /// Optional minimum interval in seconds per metric id: the subscriber then receives at most one
    /// value per interval for that metric. Values that are not positive numbers are ignored.
    /// </param>
    public void SetSelection(MetricSelection selection, IReadOnlyDictionary<string, double>? intervals = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _engine.SetSelection(this, selection, intervals);
    }

    /// <summary>Ends the subscription; metrics nobody else wants are no longer sampled.</summary>
    public void Dispose()
    {
        _engine.Unsubscribe(this);
    }

    internal bool TryWrite(MetricUpdate update)
    {
        return _channel.Writer.TryWrite(update);
    }

    internal void Complete()
    {
        _channel.Writer.TryComplete();
    }
}
