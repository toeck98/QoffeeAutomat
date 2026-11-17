using System.Text.RegularExpressions;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.CommandEditor;

/// <summary>
/// Monaco, the editor of the PowerShell code, comes with the BlazorMonaco package and is served by the agent like every
/// other file of the UI (T404, PLAN.md D-015): the page loads it from <c>_content/BlazorMonaco/</c> and from nowhere else.
/// That the agent really serves these files is tested in <c>UiHostingTests</c>.
/// </summary>
[Trait("Category", "CommandEditor")]
public sealed partial class MonacoAssetTests
{
    [GeneratedRegex(@"<script[^>]*\ssrc\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptSource();

    [Fact]
    public void The_index_page_loads_monaco_from_the_package_in_the_order_that_its_loader_needs()
    {
        string html = File.ReadAllText(RepositoryPaths.Combine("src", "QoffeeAutomat.Ui", "wwwroot", "index.html"));

        string[] scripts = [.. ScriptSource().Matches(html).Select(match => match.Groups[1].Value)];

        string[] monaco =
        [
            "_content/BlazorMonaco/jsInterop.js",
            "_content/BlazorMonaco/lib/monaco-editor/min/vs/loader.js",
            "_content/QoffeeAutomat.Ui.Components/monaco-config.js",
            "_content/BlazorMonaco/lib/monaco-editor/min/vs/editor/editor.main.js",
        ];
        int previous = -1;
        foreach (string script in monaco)
        {
            int position = Array.IndexOf(scripts, script);
            Assert.True(position > previous, script + " is missing from index.html or comes too early.");
            previous = position;
        }

        // The framework starts the app after Monaco is loaded, so that the editor can be created at once.
        Assert.True(Array.IndexOf(scripts, "_framework/blazor.webassembly.js") > previous);
        Assert.All(scripts, script => Assert.DoesNotContain("://", script, StringComparison.Ordinal));
    }

    [Fact]
    public void The_loader_is_told_to_look_for_monaco_in_the_package_and_names_no_host()
    {
        string script = File.ReadAllText(RepositoryPaths.Combine("src", "QoffeeAutomat.Ui.Components", "wwwroot", "monaco-config.js"));

        Assert.Contains("_content/BlazorMonaco/lib/monaco-editor/min/vs", script, StringComparison.Ordinal);
        Assert.DoesNotContain("://", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_package_is_referenced_by_the_component_library_with_a_central_version()
    {
        string props = File.ReadAllText(RepositoryPaths.Combine("Directory.Packages.props"));
        string project = File.ReadAllText(RepositoryPaths.Combine("src", "QoffeeAutomat.Ui.Components", "QoffeeAutomat.Ui.Components.csproj"));

        Assert.Contains("<PackageVersion Include=\"BlazorMonaco\" Version=\"3.5.0\" />", props, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"BlazorMonaco\" />", project, StringComparison.Ordinal);
    }
}
