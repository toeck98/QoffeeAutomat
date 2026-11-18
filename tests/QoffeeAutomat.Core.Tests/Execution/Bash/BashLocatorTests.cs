using QoffeeAutomat.Core.Execution.Bash;

namespace QoffeeAutomat.Core.Tests.Execution.Bash;

/// <summary>Finding bash without starting anything (PLAN.md S17, T803).</summary>
[Trait("Category", "Bash")]
public sealed class BashLocatorTests
{
    private static string PathOf(params string[] directories) => string.Join(System.IO.Path.PathSeparator, directories);

    [Fact]
    public void Bash_on_the_PATH_is_found_before_the_fixed_locations()
    {
        string onPath = System.IO.Path.Combine("/opt/tools", "bash");
        var locator = new BashLocator(
            name => name == "PATH" ? PathOf("/opt/missing", "/opt/tools") : null,
            candidate => candidate == onPath || candidate == "/usr/bin/bash");

        BashResolution resolution = locator.Resolve();

        Assert.True(resolution.IsFound);
        Assert.Equal(onPath, resolution.Path);
        Assert.Null(resolution.Reason);
    }

    [Fact]
    public void A_relative_directory_on_the_PATH_is_ignored()
    {
        var locator = new BashLocator(
            name => name == "PATH" ? PathOf("bin", ".", string.Empty) : null,
            candidate => candidate == "/usr/bin/bash" || candidate.StartsWith("bin", StringComparison.Ordinal) || candidate.StartsWith('.'));

        BashResolution resolution = locator.Resolve();

        Assert.Equal("/usr/bin/bash", resolution.Path);
    }

    [Fact]
    public void The_fixed_locations_are_tried_when_the_PATH_is_short()
    {
        var locator = new BashLocator(_ => null, candidate => candidate == "/bin/bash");

        Assert.Equal("/bin/bash", locator.Resolve().Path);
    }

    [Fact]
    public void A_computer_without_bash_gets_a_reason_that_says_what_to_do()
    {
        var locator = new BashLocator(_ => null, _ => false);

        BashResolution resolution = locator.Resolve();

        Assert.False(resolution.IsFound);
        Assert.Null(resolution.Path);
        Assert.Contains("bash was not found", resolution.Reason, StringComparison.Ordinal);
        Assert.Contains("Install", resolution.Reason, StringComparison.Ordinal);
    }
}
