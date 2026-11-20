namespace QoffeeAutomat.Hub.Gateway;

/// <summary>Counters of the agent gateway for the health metrics of the hub (T911).</summary>
public interface IGatewayStatistics
{
    /// <summary>How many times an agent opened its link again since the hub started: every accepted link of an agent that had connected before counts, the first link of an agent does not.</summary>
    long LinkReconnects { get; }
}
