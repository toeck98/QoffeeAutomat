namespace QoffeeAutomat.Core.Config;

/// <summary>Fans a change out to every subscriber.</summary>
internal sealed class ChangeBroadcaster
{
    private readonly object _gate = new();
    private ConfigSubscription[] _subscribers = [];
    private bool _completed;

    public ConfigSubscription Subscribe()
    {
        var subscription = new ConfigSubscription(Remove);
        lock (_gate)
        {
            if (_completed)
            {
                subscription.Complete();
            }
            else
            {
                _subscribers = [.. _subscribers, subscription];
            }
        }

        return subscription;
    }

    public void Publish(ConfigChange change)
    {
        ConfigSubscription[] snapshot;
        lock (_gate)
        {
            snapshot = _subscribers;
        }

        foreach (ConfigSubscription subscriber in snapshot)
        {
            subscriber.Post(change);
        }
    }

    public void Complete()
    {
        ConfigSubscription[] snapshot;
        lock (_gate)
        {
            _completed = true;
            snapshot = _subscribers;
            _subscribers = [];
        }

        foreach (ConfigSubscription subscriber in snapshot)
        {
            subscriber.Complete();
        }
    }

    private void Remove(ConfigSubscription subscription)
    {
        lock (_gate)
        {
            _subscribers = [.. _subscribers.Where(existing => !ReferenceEquals(existing, subscription))];
        }
    }
}
