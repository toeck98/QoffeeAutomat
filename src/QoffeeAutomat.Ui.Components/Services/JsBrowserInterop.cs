using Microsoft.JSInterop;

namespace QoffeeAutomat.Ui.Components.Services;

/// <summary>
/// <see cref="IBrowserInterop"/> over the small script <c>wwwroot/qoffeeautomat.js</c> of this library, which is served
/// as <c>_content/QoffeeAutomat.Ui.Components/qoffeeautomat.js</c> (no external script is involved).
/// </summary>
public sealed class JsBrowserInterop : IBrowserInterop, IAsyncDisposable
{
    private const string ModulePath = "./_content/QoffeeAutomat.Ui.Components/qoffeeautomat.js";

    private readonly IJSRuntime _js;
    private Task<IJSObjectReference>? _module;

    /// <summary>Creates the interop.</summary>
    /// <param name="js">The JavaScript runtime of the app.</param>
    public JsBrowserInterop(IJSRuntime js)
    {
        ArgumentNullException.ThrowIfNull(js);

        _js = js;
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsPageVisibleAsync()
    {
        IJSObjectReference module = await GetModuleAsync();
        return await module.InvokeAsync<bool>("isPageVisible");
    }

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> SubscribePageVisibilityAsync(Func<bool, Task> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        IJSObjectReference module = await GetModuleAsync();
        var callback = DotNetObjectReference.Create(new VisibilityCallback(onChanged));
        int id = await module.InvokeAsync<int>("subscribeVisibility", callback);
        return new VisibilitySubscription(module, callback, id);
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetStoredAsync(string key)
    {
        IJSObjectReference module = await GetModuleAsync();
        return await module.InvokeAsync<string?>("getStored", key);
    }

    /// <inheritdoc />
    public async ValueTask SetStoredAsync(string key, string value)
    {
        IJSObjectReference module = await GetModuleAsync();
        await module.InvokeVoidAsync("setStored", key, value);
    }

    /// <inheritdoc />
    public async ValueTask<bool> CopyTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        IJSObjectReference module = await GetModuleAsync();
        return await module.InvokeAsync<bool>("copyText", text);
    }

    /// <inheritdoc />
    public async ValueTask DownloadFileAsync(string fileName, string contentType, byte[] content)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(content);

        IJSObjectReference module = await GetModuleAsync();
        await module.InvokeVoidAsync("downloadFile", fileName, contentType, content);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            IJSObjectReference module = await _module;
            await module.DisposeAsync();
        }
        catch (JSException)
        {
            // The module never loaded; there is nothing to release.
        }
    }

    private Task<IJSObjectReference> GetModuleAsync()
    {
        _module ??= _js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();
        return _module;
    }

    private sealed class VisibilitySubscription : IAsyncDisposable
    {
        private readonly IJSObjectReference _module;
        private readonly DotNetObjectReference<VisibilityCallback> _callback;
        private readonly int _id;
        private bool _disposed;

        public VisibilitySubscription(IJSObjectReference module, DotNetObjectReference<VisibilityCallback> callback, int id)
        {
            _module = module;
            _callback = callback;
            _id = id;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                await _module.InvokeVoidAsync("unsubscribeVisibility", _id);
            }
            catch (JSException)
            {
                // The page is going away; the listener goes with it.
            }

            _callback.Dispose();
        }
    }
}

/// <summary>The receiver of the visibility events of the browser; JavaScript calls it.</summary>
public sealed class VisibilityCallback
{
    private readonly Func<bool, Task> _handler;

    /// <summary>Creates the receiver.</summary>
    /// <param name="handler">What to call.</param>
    public VisibilityCallback(Func<bool, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handler = handler;
    }

    /// <summary>Called by JavaScript when the visibility changed.</summary>
    /// <param name="visible">True when the page became visible.</param>
    /// <returns>A task that completes when the handler is done.</returns>
    [JSInvokable]
    public Task OnChanged(bool visible) => _handler(visible);
}
