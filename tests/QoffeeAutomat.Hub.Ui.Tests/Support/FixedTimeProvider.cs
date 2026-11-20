namespace QoffeeAutomat.Hub.Ui.Tests.Support;

/// <summary>A clock that stands still, so that a time range that a page computes from "now" can be compared exactly.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;
}
