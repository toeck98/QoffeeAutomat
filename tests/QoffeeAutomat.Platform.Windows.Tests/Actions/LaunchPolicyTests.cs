using QoffeeAutomat.SessionHost.Actions;

namespace QoffeeAutomat.Platform.Windows.Tests.Actions;

/// <summary>
/// The rules that keep <c>app.launch</c>, <c>url.open</c> and <c>process.kill</c> from doing more than their name says
/// (PLAN.md S09, D-012). They run in the SessionHost whatever the agent validated.
/// </summary>
[Trait("Category", "Actions")]
public sealed class LaunchPolicyTests
{
    [Theory]
    [InlineData("C:\\Program Files\\App\\app.exe", null)]
    [InlineData("C:/Tools/app.exe", "")]
    [InlineData("D:\\x.bat", "plain arguments")]
    [InlineData("\\\\server\\share\\tool.exe", "--flag")]
    [InlineData("C:\\Tools\\app.exe", "--name \"a b\" & more | less")]
    public void A_full_path_with_harmless_arguments_is_accepted(string path, string? arguments)
    {
        Assert.Null(LaunchPolicy.CheckLaunch(path, arguments));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_path_is_refused(string? path)
    {
        Assert.Equal("A path is required.", LaunchPolicy.CheckLaunch(path, null));
    }

    [Theory]
    [InlineData("C:\\Tools\\a\"b.exe")]
    [InlineData("C:\\Tools\\a<b.exe")]
    [InlineData("C:\\Tools\\a>b.exe")]
    [InlineData("C:\\Tools\\a|b.exe")]
    [InlineData("C:\\Tools\\a*.exe")]
    [InlineData("C:\\Tools\\a?.exe")]
    [InlineData("C:\\Tools\\a\tb.exe")]
    public void A_path_with_characters_that_a_file_name_cannot_contain_is_refused(string path)
    {
        string? problem = LaunchPolicy.CheckLaunch(path, null);

        Assert.NotNull(problem);
        Assert.Contains("characters", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app.exe")]
    [InlineData("..\\app.exe")]
    [InlineData("\\app.exe")]
    [InlineData("C:app.exe")]
    [InlineData("\\\\.\\pipe\\x")]
    [InlineData("\\\\server")]
    public void A_path_that_is_not_a_full_path_is_refused(string path)
    {
        string? problem = LaunchPolicy.CheckLaunch(path, null);

        Assert.NotNull(problem);
        Assert.Contains("full path", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void Arguments_with_line_breaks_are_refused(string arguments)
    {
        string? problem = LaunchPolicy.CheckLaunch("C:\\Tools\\app.exe", arguments);

        Assert.NotNull(problem);
        Assert.Contains("line breaks", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\Scripts\\run.bat", "a & b")]
    [InlineData("C:\\Scripts\\run.CMD", "a | b")]
    [InlineData("C:\\Scripts\\run.cmd", "a > out.txt")]
    [InlineData("C:\\Scripts\\run.cmd", "a < in.txt")]
    [InlineData("C:\\Scripts\\run.cmd", "^a")]
    [InlineData("C:\\Scripts\\run.cmd", "%PATH%")]
    [InlineData("C:\\Scripts\\run.cmd", "\"quoted\"")]
    public void A_batch_file_is_not_given_characters_that_the_command_interpreter_acts_on(string path, string arguments)
    {
        string? problem = LaunchPolicy.CheckLaunch(path, arguments);

        Assert.NotNull(problem);
        Assert.Contains("command interpreter", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_batch_file_may_have_plain_arguments()
    {
        Assert.Null(LaunchPolicy.CheckLaunch("C:\\Scripts\\run.cmd", "one two-three"));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path?q=1#top")]
    [InlineData("HTTPS://EXAMPLE.COM:8443/")]
    public void An_http_or_https_address_is_accepted_and_parsed(string url)
    {
        string? problem = LaunchPolicy.CheckUrl(url, out Uri? uri);

        Assert.Null(problem);
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData(null, "An address is required.")]
    [InlineData("  ", "An address is required.")]
    [InlineData("https://example.com/a b", "spaces or control characters")]
    [InlineData("https://example.com/\t", "spaces or control characters")]
    [InlineData("example.com", "not a complete web address")]
    [InlineData("ftp://example.com/", "Only http and https")]
    [InlineData("file:///C:/Windows/System32/cmd.exe", "Only http and https")]
    [InlineData("javascript:alert(1)", "Only http and https")]
    [InlineData("https://user:secret@example.com/", "user name or password")]
    public void Anything_else_is_refused_with_a_reason_and_no_parsed_address(string? url, string reason)
    {
        string? problem = LaunchPolicy.CheckUrl(url, out Uri? uri);

        Assert.NotNull(problem);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
        Assert.Null(uri);
    }

    [Theory]
    [InlineData("notepad", "notepad")]
    [InlineData("notepad.exe", "notepad")]
    [InlineData("  Notepad.EXE  ", "Notepad")]
    [InlineData("My Program", "My Program")]
    public void A_process_name_is_trimmed_and_loses_a_trailing_exe(string name, string expected)
    {
        string? problem = LaunchPolicy.CheckProcessName(name, out string normalized);

        Assert.Null(problem);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".exe")]
    public void A_missing_process_name_is_refused(string? name)
    {
        string? problem = LaunchPolicy.CheckProcessName(name, out string normalized);

        Assert.Equal("A process name is required.", problem);
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("dir\\notepad")]
    [InlineData("dir/notepad")]
    [InlineData("C:notepad")]
    [InlineData("note*")]
    [InlineData("note?")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\tb")]
    public void A_process_name_with_a_path_a_wildcard_or_a_control_character_is_refused(string name)
    {
        string? problem = LaunchPolicy.CheckProcessName(name, out string normalized);

        Assert.NotNull(problem);
        Assert.Contains("without a path", problem, StringComparison.Ordinal);
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("Registry")]
    [InlineData("smss.exe")]
    [InlineData("csrss")]
    [InlineData("wininit")]
    [InlineData("WINLOGON.EXE")]
    [InlineData("services")]
    [InlineData("lsass")]
    [InlineData("lsm")]
    [InlineData("svchost")]
    [InlineData("qoffeeautomat")]
    [InlineData("QoffeeAutomat.Agent")]
    [InlineData("qoffeeautomat.sessionhost.exe")]
    public void The_processes_that_keep_windows_and_this_product_running_cannot_be_ended(string name)
    {
        string? problem = LaunchPolicy.CheckProcessName(name, out string normalized);

        Assert.NotNull(problem);
        Assert.Contains("cannot be ended", problem, StringComparison.Ordinal);
        Assert.Equal(string.Empty, normalized);
    }
}
