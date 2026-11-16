using System.Text.RegularExpressions;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Shell;

/// <summary>
/// No request leaves the PC (PLAN.md D-015, AGENTS.md "Things you must not do"): the UI has no CDN, no web font and no
/// external script, neither in its sources nor in what the build and the publish produce.
/// </summary>
[Trait("Category", "Ui")]
public sealed partial class NoExternalHostTests
{
    /// <summary>A directory that holds a finished <c>wwwroot</c>, for example the output of <c>dotnet publish</c>; optional.</summary>
    private const string PublishDirectoryVariable = "QOFFEE_UI_PUBLISH_DIR";

    private static readonly string[] MarkupExtensions = [".html", ".htm", ".css", ".svg", ".webmanifest"];

    // src="https://x", href='//x', url(https://x), url("//x"), @import "https://x" : the ways that a browser fetches something.
    [GeneratedRegex(@"(?:\b(?:src|href|action|poster|data)\s*=\s*|\burl\(\s*|@import\s+)[""']?\s*(?:https?:)?//", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalReference();

    [GeneratedRegex(@"(?:https?|wss?|ftp):\/\/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnyUrl();

    [GeneratedRegex(@"(?:src|href)\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoadedFile();

    [Fact]
    public void The_wwwroot_of_the_ui_shell_refers_to_no_external_host()
    {
        string root = RepositoryPaths.Combine("src", "QoffeeAutomat.Ui", "wwwroot");
        string[] files = MarkupFiles(root);

        Assert.Contains(files, file => file.EndsWith("index.html", StringComparison.Ordinal));
        AssertNoExternalReferences(files);
    }

    [Fact]
    public void The_scripts_and_styles_of_the_component_library_name_no_url_at_all()
    {
        string root = RepositoryPaths.Combine("src", "QoffeeAutomat.Ui.Components", "wwwroot");
        string[] files = [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)];

        Assert.Contains(files, file => file.EndsWith("qoffeeautomat.js", StringComparison.Ordinal));
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            Assert.False(AnyUrl().IsMatch(text), file + " contains an absolute URL.");
            Assert.False(ExternalReference().IsMatch(text), file + " refers to another host.");
        }
    }

    [Fact]
    public void The_razor_markup_of_the_component_library_links_to_no_external_host()
    {
        string root = RepositoryPaths.Combine("src", "QoffeeAutomat.Ui.Components");
        string[] files =
        [
            .. Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories),
        ];

        Assert.NotEmpty(files);
        AssertNoExternalReferences(files);
    }

    [Fact]
    public void The_index_page_loads_its_styles_and_scripts_from_the_agent_and_the_packages_only()
    {
        string html = File.ReadAllText(RepositoryPaths.Combine("src", "QoffeeAutomat.Ui", "wwwroot", "index.html"));

        // Every file that the page loads is a relative path: of the app, of MudBlazor's package or of the component library.
        MatchCollection loaded = LoadedFile().Matches(html);
        Assert.NotEmpty(loaded);
        foreach (Match match in loaded)
        {
            string target = match.Groups[1].Value;
            Assert.False(
                target.Contains("://", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal),
                "index.html loads " + target + ", which is not local.");
        }

        Assert.Contains("_content/MudBlazor/MudBlazor.min.css", html, StringComparison.Ordinal);
        Assert.Contains("_content/MudBlazor/MudBlazor.min.js", html, StringComparison.Ordinal);
        Assert.Contains("_framework/blazor.webassembly", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fonts.googleapis.com", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_built_output_of_the_ui_refers_to_no_external_host()
    {
        // Whatever the build or the publish put into wwwroot: the variable names a published wwwroot explicitly, and the
        // usual output folders of the agent and of the UI are searched. The test passes when nothing has been built yet;
        // CI runs it after `dotnet publish src/QoffeeAutomat.Agent -c Release` (task T401).
        string[] roots = [.. BuiltWebRoots().Where(Directory.Exists)];
        foreach (string root in roots)
        {
            AssertNoExternalReferences(MarkupFiles(root));
        }
    }

    private static string[] BuiltWebRoots()
    {
        var roots = new List<string>();
        string? explicitRoot = Environment.GetEnvironmentVariable(PublishDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            roots.Add(explicitRoot);
        }

        foreach (string project in new[] { "QoffeeAutomat.Agent", "QoffeeAutomat.Ui" })
        {
            string bin = RepositoryPaths.Combine("src", project, "bin");
            if (!Directory.Exists(bin))
            {
                continue;
            }

            roots.AddRange(Directory.EnumerateDirectories(bin, "wwwroot", SearchOption.AllDirectories));
        }

        return [.. roots];
    }

    private static string[] MarkupFiles(string root)
    {
        return
        [
            .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => MarkupExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)),
        ];
    }

    private static void AssertNoExternalReferences(IEnumerable<string> files)
    {
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            Match match = ExternalReference().Match(text);
            Assert.False(match.Success, file + " refers to another host: " + match.Value);
        }
    }
}
