using QoffeeAutomat.Platform.Linux.Service;

namespace QoffeeAutomat.Platform.Linux.Tests.Service;

/// <summary>The systemd unit that <c>service install</c> writes and the package ships (T802).</summary>
[Trait("Category", "Service")]
public sealed class SystemdUnitFileTests
{
    [LinuxHostFact]
    public void The_unit_that_the_package_ships_is_what_the_installer_writes_for_the_packaged_layout()
    {
        string shipped = File.ReadAllText(RepositoryFiles.Find("packaging/linux/qoffeeautomat.service")).ReplaceLineEndings("\n");

        Assert.Equal(shipped, SystemdUnitFile.Render(SystemdUnitOptions.Packaged));
    }

    [LinuxHostFact]
    public void The_unit_is_a_notify_service_with_a_watchdog_that_starts_at_boot()
    {
        string unit = SystemdUnitFile.Render(SystemdUnitOptions.Packaged);

        Assert.Contains("\nType=notify\n", unit, StringComparison.Ordinal);
        Assert.Contains("\nNotifyAccess=main\n", unit, StringComparison.Ordinal);
        Assert.Contains("\nWatchdogSec=60\n", unit, StringComparison.Ordinal);
        Assert.Contains("\nRestart=on-failure\n", unit, StringComparison.Ordinal);
        Assert.Contains("\nExecStart=/usr/lib/qoffeeautomat/qoffeeautomat run\n", unit, StringComparison.Ordinal);
        Assert.Contains("\nWorkingDirectory=/usr/lib/qoffeeautomat\n", unit, StringComparison.Ordinal);
        Assert.Contains("\nWantedBy=multi-user.target\n", unit, StringComparison.Ordinal);
        Assert.Contains("\nAfter=network-online.target systemd-logind.service\n", unit, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', unit);
        Assert.EndsWith("\n", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void The_packaged_unit_lets_systemd_create_the_directories_of_the_plan_as_0700()
    {
        string unit = SystemdUnitFile.Render(SystemdUnitOptions.Packaged);

        Assert.Contains("\nUMask=0077\n", unit, StringComparison.Ordinal);
        foreach (string directory in new[] { "Configuration", "State", "Logs" })
        {
            Assert.Contains($"\n{directory}Directory=qoffeeautomat\n", unit, StringComparison.Ordinal);
            Assert.Contains($"\n{directory}DirectoryMode=0700\n", unit, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_data_directory_of_the_users_choice_is_passed_on_and_systemd_creates_no_other_directories()
    {
        var options = new SystemdUnitOptions("/opt/qoffee/qoffeeautomat", [], DataDirectory: "/srv/qoffee data", UiPort: 9000, LinkPort: 9001);

        string unit = SystemdUnitFile.Render(options);

        Assert.Contains("\nExecStart=/opt/qoffee/qoffeeautomat run --data-dir \"/srv/qoffee data\" --ui-port 9000 --link-port 9001\n", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigurationDirectory", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("StateDirectory", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("LogsDirectory", unit, StringComparison.Ordinal);
    }

    [LinuxHostFact]
    public void Started_through_the_dotnet_host_the_assembly_is_an_argument_and_decides_the_working_directory()
    {
        var options = new SystemdUnitOptions("/usr/bin/dotnet", ["/opt/qoffee/QoffeeAutomat.Agent.dll"]);

        string unit = SystemdUnitFile.Render(options);

        Assert.Contains("\nExecStart=/usr/bin/dotnet /opt/qoffee/QoffeeAutomat.Agent.dll run\n", unit, StringComparison.Ordinal);
        Assert.Contains("\nWorkingDirectory=/opt/qoffee\n", unit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("100%", "100%%")]
    [InlineData("$HOME", "$$HOME")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\b", "\"a\\\\b\"")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("", "\"\"")]
    public void A_word_of_exec_start_is_quoted_and_cannot_expand_a_variable_or_specifier(string word, string expected)
    {
        Assert.Equal(expected, SystemdUnitFile.QuoteWord(word));
    }

    [Theory]
    [InlineData("a\nExecStartPost=/bin/evil")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void A_word_with_a_line_break_or_control_character_is_refused_so_it_cannot_add_a_setting(string word)
    {
        Assert.Throws<ArgumentException>(() => SystemdUnitFile.QuoteWord(word));
    }

    [Fact]
    public void The_executable_must_be_an_absolute_path()
    {
        Assert.Throws<ArgumentException>(() => SystemdUnitFile.Render(new SystemdUnitOptions("qoffeeautomat", [])));
    }

    [Fact]
    public void A_data_directory_with_a_line_break_cannot_reach_the_unit()
    {
        var options = new SystemdUnitOptions("/usr/lib/qoffeeautomat/qoffeeautomat", [], DataDirectory: "/data\nExecStartPre=/bin/evil");

        Assert.Throws<ArgumentException>(() => SystemdUnitFile.Render(options));
    }
}
