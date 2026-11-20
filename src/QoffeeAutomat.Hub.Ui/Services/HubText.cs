using System.Resources;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Hub.Ui.Services;

/// <summary>
/// The texts of the hub pages in English and German (PLAN.md S20, T915). The strings are in <c>Resources/HubStrings.resx</c> (English, the neutral
/// language) and <c>Resources/HubStrings.de.resx</c>; the language is the one of the shared <see cref="Localizer"/>, so one switch changes the hub
/// pages and the pages of the component library together. A key that the hub resources lack is looked up in the shared strings (<c>Common.Retry</c>,
/// <c>Hub.Origin.Hub</c> and so on) and, failing that, shown as the key itself, so a gap is visible and never breaks a page.
/// </summary>
public sealed class HubText
{
    /// <summary>The base name of the resources.</summary>
    public const string ResourceBaseName = "QoffeeAutomat.Hub.Ui.Resources.HubStrings";

    private static readonly ResourceManager Resources = new(ResourceBaseName, typeof(HubText).Assembly);

    private readonly Localizer _localizer;

    /// <summary>Creates the texts.</summary>
    /// <param name="localizer">The shared localizer, which holds the language.</param>
    public HubText(Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        _localizer = localizer;
    }

    /// <summary>Looks a text up.</summary>
    /// <param name="key">The resource key.</param>
    /// <returns>The text, or the key when there is none.</returns>
    public string this[string key] => Find(key) ?? _localizer.Find(key) ?? key;

    /// <summary>Looks a text up and fills in its placeholders.</summary>
    /// <param name="key">The resource key.</param>
    /// <param name="arguments">The values for <c>{0}</c>, <c>{1}</c> and so on.</param>
    /// <returns>The text.</returns>
    public string Format(string key, params object?[] arguments)
    {
        return string.Format(_localizer.Culture, this[key], arguments);
    }

    /// <summary>
    /// The text of an error of the hub: the hub's own translation of its code when there is one (<c>Error.&lt;code&gt;</c> in the hub resources, which wins
    /// because the shared texts talk about "the password" of an agent), else what the component library says, else the English detail of the hub.
    /// </summary>
    /// <param name="exception">The error.</param>
    /// <returns>The text to show.</returns>
    public string Describe(ApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string? translated = Find("Error." + exception.Code);
        return translated is not null
            ? string.Format(_localizer.Culture, translated, exception.RetryAfterSeconds ?? 0)
            : _localizer.Describe(exception);
    }

    /// <summary>Looks a text up in the hub resources only.</summary>
    /// <param name="key">The resource key.</param>
    /// <returns>The text, or <c>null</c> when the hub resources have none.</returns>
    public string? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Resources.GetString(key, _localizer.Culture);
    }
}
