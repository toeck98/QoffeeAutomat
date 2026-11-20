using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Hub.Ui.Tests.Support;

/// <summary>A browser for component tests: the clipboard and the storage are lists and a dictionary.</summary>
internal sealed class FakeBrowserInterop : IBrowserInterop
{
    public bool Visible { get; set; } = true;

    public Dictionary<string, string> Storage { get; } = [];

    /// <summary>The texts that were copied to the clipboard.</summary>
    public List<string> Copied { get; } = [];

    public ValueTask<bool> IsPageVisibleAsync() => ValueTask.FromResult(Visible);

    public ValueTask<IAsyncDisposable> SubscribePageVisibilityAsync(Func<bool, Task> onChanged)
    {
        return ValueTask.FromResult<IAsyncDisposable>(new Subscription());
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

    public ValueTask<bool> CopyTextAsync(string text)
    {
        Copied.Add(text);
        return ValueTask.FromResult(true);
    }

    public ValueTask DownloadFileAsync(string fileName, string contentType, byte[] content) => ValueTask.CompletedTask;

    private sealed class Subscription : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
