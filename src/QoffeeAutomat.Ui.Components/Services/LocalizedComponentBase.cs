using Microsoft.AspNetCore.Components;

namespace QoffeeAutomat.Ui.Components.Services;

/// <summary>
/// Base class of the components that show text: it provides the <see cref="Localizer"/> and renders the component again
/// when the language changes (a component without parameters is not rendered again just because its parent is).
/// Derived components that need to release something override <see cref="OnDisposeAsync"/>.
/// </summary>
public abstract class LocalizedComponentBase : ComponentBase, IAsyncDisposable
{
    private bool _subscribed;

    /// <summary>The texts of the UI.</summary>
    [Inject]
    protected Localizer L { get; set; } = null!;

    /// <summary>True once the component was disposed; asynchronous work must not touch the component afterwards.</summary>
    protected bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        if (_subscribed)
        {
            L.Changed -= OnLanguageChanged;
        }

        await OnDisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public override Task SetParametersAsync(ParameterView parameters)
    {
        if (!_subscribed)
        {
            // The injected services are set before the first call.
            _subscribed = true;
            L.Changed += OnLanguageChanged;
        }

        return base.SetParametersAsync(parameters);
    }

    /// <summary>Releases what the component holds; runs once, when the component leaves the page.</summary>
    /// <returns>A task that completes when everything is released.</returns>
    protected virtual ValueTask OnDisposeAsync() => ValueTask.CompletedTask;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
        {
            _ = InvokeAsync(StateHasChanged);
        }
    }
}
