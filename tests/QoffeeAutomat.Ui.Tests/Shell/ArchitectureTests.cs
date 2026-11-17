using System.Xml.Linq;
using QoffeeAutomat.Ui.Components;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Shell;

/// <summary>The component library stays reusable by the hub UI (PLAN.md S04, D-033): it knows Contracts only.</summary>
[Trait("Category", "Ui")]
public sealed class ArchitectureTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "QoffeeAutomat.Agent",
        "QoffeeAutomat.Core",
        "QoffeeAutomat.Platform",
        "QoffeeAutomat.SessionHost",
        "QoffeeAutomat.Hub",
    ];

    [Fact]
    public void Ui_components_references_no_agent_core_or_platform_assembly()
    {
        string[] references = [.. typeof(App).Assembly.GetReferencedAssemblies().Select(name => name.Name ?? string.Empty)];

        Assert.Contains("QoffeeAutomat.Contracts", references);
        foreach (string reference in references)
        {
            foreach (string forbidden in ForbiddenPrefixes)
            {
                Assert.False(
                    reference.StartsWith(forbidden, StringComparison.Ordinal),
                    "QoffeeAutomat.Ui.Components references " + reference + ", which a component library must not know.");
            }
        }
    }

    [Fact]
    public void The_project_file_of_ui_components_references_contracts_only()
    {
        XDocument project = XDocument.Load(RepositoryPaths.Combine("src", "QoffeeAutomat.Ui.Components", "QoffeeAutomat.Ui.Components.csproj"));

        string[] references =
        [
            .. project.Descendants("ProjectReference")
                .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
                .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/'))),
        ];

        Assert.Equal(["QoffeeAutomat.Contracts"], references);
    }

    [Fact]
    public void No_source_file_of_ui_components_names_the_agent_core_or_a_platform_namespace()
    {
        string directory = RepositoryPaths.Combine("src", "QoffeeAutomat.Ui.Components");
        string[] files =
        [
            .. Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".razor", StringComparison.Ordinal))
                .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
        ];

        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (string forbidden in ForbiddenPrefixes)
            {
                Assert.DoesNotContain(forbidden + ".", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_ui_shell_references_contracts_and_the_component_library_only()
    {
        XDocument project = XDocument.Load(RepositoryPaths.Combine("src", "QoffeeAutomat.Ui", "QoffeeAutomat.Ui.csproj"));

        string[] references =
        [
            .. project.Descendants("ProjectReference")
                .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
                .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(["QoffeeAutomat.Contracts", "QoffeeAutomat.Ui.Components"], references);
    }
}
