namespace QoffeeAutomat.Ui.Components.Services;

/// <summary>
/// The few things the UI needs from the browser that Blazor does not offer by itself: whether the tab is visible
/// (the dashboard must not keep metrics sampled for a page nobody looks at, PLAN.md D-007, T402) and a small
/// persistent store for the chosen language. Components depend on this interface so that tests can drive it.
/// </summary>
public interface IBrowserInterop
{
    /// <summary>Tells whether the page is visible (the tab is in the foreground and the window is not minimised).</summary>
    /// <returns>True when visible.</returns>
    ValueTask<bool> IsPageVisibleAsync();

    /// <summary>Calls <paramref name="onChanged"/> whenever the page becomes visible or hidden.</summary>
    /// <param name="onChanged">Receives true when the page became visible.</param>
    /// <returns>Dispose it to stop listening.</returns>
    ValueTask<IAsyncDisposable> SubscribePageVisibilityAsync(Func<bool, Task> onChanged);

    /// <summary>Reads a value from the persistent store of this browser profile.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or <c>null</c> when there is none or the store is blocked.</returns>
    ValueTask<string?> GetStoredAsync(string key);

    /// <summary>Writes a value to the persistent store; a blocked store is ignored.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns>A task that completes when the value is written.</returns>
    ValueTask SetStoredAsync(string key, string value);

    /// <summary>Puts a text on the clipboard.</summary>
    /// <param name="text">The text.</param>
    /// <returns>True when the text was copied; false when the browser refused.</returns>
    ValueTask<bool> CopyTextAsync(string text);

    /// <summary>Offers bytes to the user as a file download (the export and the diagnostics bundle).</summary>
    /// <param name="fileName">The suggested file name.</param>
    /// <param name="contentType">The media type.</param>
    /// <param name="content">The bytes.</param>
    /// <returns>A task that completes when the browser has been handed the file.</returns>
    ValueTask DownloadFileAsync(string fileName, string contentType, byte[] content);
}
