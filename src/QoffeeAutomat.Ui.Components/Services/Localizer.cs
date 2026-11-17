using System.Globalization;
using System.Resources;
using QoffeeAutomat.Ui.Components.Api;

namespace QoffeeAutomat.Ui.Components.Services;

/// <summary>
/// The text of the UI in English and German (PLAN.md T401). The strings are in <c>Resources/Strings.resx</c> (English,
/// the neutral language) and <c>Resources/Strings.de.resx</c>; a missing key shows the key itself, so a gap is visible
/// and never breaks a page. The language starts as the language of the browser and can be switched at run time; the
/// components that show text listen to <see cref="Changed"/>.
/// </summary>
public sealed class Localizer
{
    /// <summary>The languages that have resources, English first (the fallback).</summary>
    public static readonly IReadOnlyList<string> SupportedLanguages = ["en", "de"];

    /// <summary>The base name of the resources.</summary>
    public const string ResourceBaseName = "QoffeeAutomat.Ui.Components.Resources.Strings";

    private static readonly ResourceManager Resources = new(ResourceBaseName, typeof(Localizer).Assembly);

    /// <summary>Creates a localizer for the language of the current UI culture.</summary>
    public Localizer()
    {
        Culture = Resolve(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
    }

    /// <summary>Raised after <see cref="SetLanguage"/> changed the language.</summary>
    public event EventHandler? Changed;

    /// <summary>The culture of the texts and of the formatting of numbers and times.</summary>
    public CultureInfo Culture { get; private set; }

    /// <summary>The two-letter code of the current language.</summary>
    public string Language => Culture.TwoLetterISOLanguageName;

    /// <summary>Looks a text up.</summary>
    /// <param name="key">The resource key.</param>
    /// <returns>The text, or the key when there is none.</returns>
    public string this[string key] => Find(key) ?? key;

    /// <summary>Looks a text up and fills in its placeholders.</summary>
    /// <param name="key">The resource key.</param>
    /// <param name="arguments">The values for <c>{0}</c>, <c>{1}</c> and so on.</param>
    /// <returns>The text.</returns>
    public string Format(string key, params object?[] arguments)
    {
        return string.Format(Culture, this[key], arguments);
    }

    /// <summary>Looks a text up without the key as fallback.</summary>
    /// <param name="key">The resource key.</param>
    /// <returns>The text, or <c>null</c> when there is none.</returns>
    public string? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Resources.GetString(key, Culture);
    }

    /// <summary>Switches the language. A language without resources selects English.</summary>
    /// <param name="language">A language code such as <c>de</c> or <c>de-AT</c>.</param>
    public void SetLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);

        CultureInfo culture = Resolve(language);
        if (culture.Name == Culture.Name)
        {
            return;
        }

        Culture = culture;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The text of an error: a translation of its code when there is one, else the English detail of the agent.</summary>
    /// <param name="exception">The error.</param>
    /// <returns>The text to show.</returns>
    public string Describe(ApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string? translated = Find("Error." + exception.Code);
        if (translated is not null)
        {
            return string.Format(Culture, translated, exception.RetryAfterSeconds ?? 0);
        }

        string? detail = exception.Problem?.Detail;
        return string.IsNullOrWhiteSpace(detail)
            ? Format("Error.http", exception.StatusCode)
            : detail;
    }

    private static CultureInfo Resolve(string language)
    {
        string code = language.Length >= 2 ? language[..2] : language;
        string selected = SupportedLanguages[0];
        foreach (string supported in SupportedLanguages)
        {
            if (string.Equals(supported, code, StringComparison.OrdinalIgnoreCase))
            {
                selected = supported;
                break;
            }
        }

        try
        {
            return CultureInfo.GetCultureInfo(selected);
        }
        catch (CultureNotFoundException)
        {
            // Invariant globalization mode has no cultures; the texts then come from the neutral (English) resources.
            return CultureInfo.InvariantCulture;
        }
    }
}
