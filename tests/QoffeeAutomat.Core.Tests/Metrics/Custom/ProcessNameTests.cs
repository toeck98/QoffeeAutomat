using QoffeeAutomat.Core.Metrics.Custom;

namespace QoffeeAutomat.Core.Tests.Metrics.Custom;

/// <summary>Matching of process names for <c>process_running</c> sensors, and the real process list.</summary>
[Trait("Category", "CustomSensors")]
public sealed class ProcessNameTests
{
    private static readonly HashSet<string> Windows = new(["notepad", "Code", "svchost"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Unix = new(["bash", "Xorg", "my.app"], StringComparer.Ordinal);

    [Theory]
    [InlineData("notepad", true)]
    [InlineData("NOTEPAD", true)]
    [InlineData("notepad.exe", true)]
    [InlineData("Notepad.EXE", true)]
    [InlineData("  notepad  ", true)]
    [InlineData("code", true)]
    [InlineData("notepad2", false)]
    [InlineData("note", false)]
    [InlineData("notepad.exe.exe", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Names_are_matched_the_way_windows_does(string requested, bool expected)
    {
        Assert.Equal(expected, ProcessNames.IsRunning(Windows, requested));
    }

    [Theory]
    [InlineData("bash", true)]
    [InlineData("Bash", false)]
    [InlineData("Xorg", true)]
    [InlineData("xorg", false)]
    [InlineData("my.app", true)]
    [InlineData("bash.exe", true)]
    [InlineData("zsh", false)]
    public void Names_are_matched_exactly_where_the_file_system_is_case_sensitive(string requested, bool expected)
    {
        Assert.Equal(expected, ProcessNames.IsRunning(Unix, requested));
    }

    [Fact]
    public void A_name_that_already_ends_in_exe_is_found_as_it_is()
    {
        var running = new HashSet<string>(["tool.exe"], StringComparer.OrdinalIgnoreCase);

        Assert.True(ProcessNames.IsRunning(running, "tool.exe"));
        Assert.False(ProcessNames.IsRunning(running, "tool"));
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ProcessNames.IsRunning(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => ProcessNames.IsRunning(Windows, null!));
    }

    [Fact]
    public void The_system_process_list_contains_the_test_host_itself()
    {
        using System.Diagnostics.Process current = System.Diagnostics.Process.GetCurrentProcess();
        string self = current.ProcessName;

        IReadOnlySet<string> names = new SystemProcessNameSource().GetRunningProcessNames();

        Assert.NotEmpty(names);
        Assert.True(ProcessNames.IsRunning(names, self), $"The running process '{self}' is missing from the list.");
    }

    [Fact]
    public void The_system_process_list_does_not_contain_a_process_that_does_not_exist()
    {
        IReadOnlySet<string> names = new SystemProcessNameSource().GetRunningProcessNames();

        Assert.False(ProcessNames.IsRunning(names, "qoffeeautomat-no-such-process-" + Guid.NewGuid().ToString("N")));
    }
}
