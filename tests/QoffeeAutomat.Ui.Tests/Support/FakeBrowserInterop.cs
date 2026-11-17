using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Tests.Support;

/// <summary>A browser for component tests: the tab can be shown and hidden, and the storage is a dictionary.</summary>
internal sealed class FakeBrowserInterop : IBrowserInterop
{
    private readonly List<Func<bool, Task>> _handlers = [];

    public bool Visible { get; set; } = true;

    public Dictionary<string, string> Storage { get; } = [];

    /// <summary>The number of visibility listeners that are registered now.</summary>
    public int ActiveSubscriptions => _handlers.Count;

    public ValueTask<bool> IsPageVisibleAsync() => ValueTask.FromResult(Visible);

    public ValueTask<IAsyncDisposable> SubscribePageVisibilityAsync(Func<bool, Task> onChanged)
    {
        _handlers.Add(onChanged);
        return ValueTask.FromResult<IAsyncDisposable>(new Subscription(this, onChanged));
    }

    public ValueTask<string?> GetStoredAsync(string key)
    {
        return ValueTask.FromResult(Storage.TryGetValue(key, out string? value) ? value : null);
    }

    public ValueTask SetStoredAsync(string key, string value)
    {
        Storage[key] = value;
        return ValueTask.CompletedTask;
    }

    /// <summary>The texts that were copied to the clipboard.</summary>
    public List<string> Copied { get; } = [];

    public ValueTask<bool> CopyTextAsync(string text)
    {
        Copied.Add(text);
        return ValueTask.FromResult(true);
    }

    /// <summary>The files that were offered as downloads.</summary>
    public List<(string FileName, string ContentType, byte[] Content)> Downloads { get; } = [];

    public ValueTask DownloadFileAsync(string fileName, string contentType, byte[] content)
    {
        Downloads.Add((fileName, contentType, content));
        return ValueTask.CompletedTask;
    }

    /// <summary>Shows or hides the tab and tells the listeners, as the browser does.</summary>
    public async Task SetVisibleAsync(bool visible)
    {
        Visible = visible;
        foreach (Func<bool, Task> handler in _handlers.ToArray())
        {
            await handler(visible);
        }
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly FakeBrowserInterop _owner;
        private readonly Func<bool, Task> _handler;

        public Subscription(FakeBrowserInterop owner, Func<bool, Task> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public ValueTask DisposeAsync()
        {
            _owner._handlers.Remove(_handler);
            return ValueTask.CompletedTask;
        }
    }
}
