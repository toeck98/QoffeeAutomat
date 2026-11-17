using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Shell;

/// <summary>English and German resources (T401): the same keys, the same placeholders, and every key the code asks for exists.</summary>
[Trait("Category", "Ui")]
public sealed partial class LocalizationTests
{
    [GeneratedRegex(@"\{(\d+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    // L["Key"], L.Format("Key", ...), localizer["Key"] and Localizer.Find("Key"); a key that is built from parts ("Error." + code) is not matched.
    [GeneratedRegex(@"\b(?:L|localizer|Localizer)(?:\.Format\(|\.Find\(|\[)\s*""([A-Za-z0-9_.]+)""\s*[\],)]", RegexOptions.CultureInvariant)]
    private static partial Regex KeyUse();

    [Fact]
    public void English_and_german_have_exactly_the_same_keys()
    {
        Dictionary<string, string> english = Load("Strings.resx");
        Dictionary<string, string> german = Load("Strings.de.resx");

        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.Order(StringComparer.Ordinal), german.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_text_is_filled_in_and_uses_the_same_placeholders_in_both_languages()
    {
        Dictionary<string, string> english = Load("Strings.resx");
        Dictionary<string, string> german = Load("Strings.de.resx");

        foreach ((string key, string text) in english)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), "Strings.resx has no text for " + key);
            Assert.False(string.IsNullOrWhiteSpace(german[key]), "Strings.de.resx has no text for " + key);
            Assert.Equal(Placeholders(text), Placeholders(german[key]));
        }
    }

    [Fact]
    public void Every_key_that_the_components_use_exists_in_the_resources()
    {
        Dictionary<string, string> english = Load("Strings.resx");
        string root = RepositoryPaths.Combine("src", "QoffeeAutomat.Ui.Components");
        string[] files =
        [
            .. Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(file => file.EndsWith(".razor", StringComparison.Ordinal) || file.EndsWith(".cs", StringComparison.Ordinal))
                .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
        ];

        int used = 0;
        foreach (string file in files)
        {
            foreach (Match match in KeyUse().Matches(File.ReadAllText(file)))
            {
                used++;
                string key = match.Groups[1].Value;
                Assert.True(english.ContainsKey(key), file + " asks for the text " + key + ", which Strings.resx does not have.");
            }
        }

        Assert.True(used > 40, "The scan found only " + used.ToString(CultureInfo.InvariantCulture) + " uses of texts; the pattern is probably wrong.");
    }

    [Fact]
    public void Every_error_code_of_the_sign_in_flow_has_a_text_in_both_languages()
    {
        Dictionary<string, string> english = Load("Strings.resx");

        foreach (string code in new[] { "network_error", "unauthorized", "invalid_credentials", "invalid_setup_token", "locked_out", "remote_ui_disabled", "setup_unavailable" })
        {
            Assert.True(english.ContainsKey("Error." + code), "No text for the error code " + code);
        }
    }

    [Fact]
    public void The_localizer_answers_in_the_chosen_language_and_falls_back_to_english_and_to_the_key()
    {
        var localizer = new Localizer();
        localizer.SetLanguage("en");
        Assert.Equal("Sign in", localizer["Login.Title"]);
        Assert.Equal("No.Such.Key", localizer["No.Such.Key"]);
        Assert.Equal("The agent answered with status 502.", localizer.Format("Error.http", 502));

        if (Globalization.IsInvariant)
        {
            return;
        }

        int changes = 0;
        localizer.Changed += (_, _) => changes++;
        localizer.SetLanguage("de");
        Assert.Equal("Anmelden", localizer["Login.Title"]);
        Assert.Equal("de", localizer.Language);
        Assert.Equal(1, changes);

        localizer.SetLanguage("de-AT");
        Assert.Equal(1, changes);

        // A language without resources selects English.
        localizer.SetLanguage("fr");
        Assert.Equal("Sign in", localizer["Login.Title"]);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void An_error_is_described_by_its_translated_code_or_else_by_the_detail_of_the_agent()
    {
        var localizer = new Localizer();
        localizer.SetLanguage("en");

        var known = new ApiException(401, Samples.Problem(401, "invalid_credentials", "English detail."));
        var locked = new ApiException(429, Samples.Problem(429, "locked_out", "English detail."), retryAfterSeconds: 30);
        var unknown = new ApiException(400, Samples.Problem(400, "weak_password", "The password must have at least 12 characters."));
        var bare = new ApiException(500, null);
        var network = new ApiException(0, Samples.Problem(0, ApiException.NetworkErrorCode, "x"));

        Assert.Equal("The password is wrong.", localizer.Describe(known));
        Assert.Equal("Too many failed attempts. Try again in 30 seconds.", localizer.Describe(locked));
        Assert.Equal("The password must have at least 12 characters.", localizer.Describe(unknown));
        Assert.Equal("The agent answered with status 500.", localizer.Describe(bare));
        Assert.Equal("The agent cannot be reached.", localizer.Describe(network));
    }

    private static string[] Placeholders(string text)
    {
        return [.. Placeholder().Matches(text).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static Dictionary<string, string> Load(string file)
    {
        XDocument document = XDocument.Load(RepositoryPaths.Combine("src", "QoffeeAutomat.Ui.Components", "Resources", file));
        return document.Root!.Elements("data").ToDictionary(
            data => (string)data.Attribute("name")!,
            data => (string?)data.Element("value") ?? string.Empty,
            StringComparer.Ordinal);
    }
}
