using QoffeeAutomat.Platform.Linux.Service;

namespace QoffeeAutomat.Platform.Linux.Tests.Service;

/// <summary>How the verbs build their <c>systemctl</c> calls, with a runner in place of the real program (T802).</summary>
[Trait("Category", "Service")]
public sealed class SystemctlClientTests
{
    private const string Unit = "qoffeeautomat.service";

    [Fact]
    public async Task Each_operation_is_one_systemctl_call_with_one_argv_entry_per_word()
    {
        var runner = new FakeCommandRunner();
        var client = new SystemctlClient(runner);

        await client.DaemonReloadAsync(CancellationToken.None);
        await client.EnableAsync(Unit, CancellationToken.None);
        await client.StartAsync(Unit, CancellationToken.None);
        await client.StopAsync(Unit, CancellationToken.None);
        await client.DisableAsync(Unit, CancellationToken.None);

        Assert.All(runner.Calls, call => Assert.Equal("systemctl", call.FileName));
        string[][] expected =
        [
            ["daemon-reload"],
            ["enable", Unit],
            ["start", Unit],
            ["stop", Unit],
            ["disable", Unit],
        ];
        Assert.Equal(expected, runner.Calls.Select(call => call.Arguments));
    }

    [Fact]
    public async Task The_state_of_a_unit_is_read_with_show_and_a_fixed_list_of_properties()
    {
        var runner = new FakeCommandRunner
        {
            Result = new CommandResult(
                0,
                "LoadState=loaded\nActiveState=active\nSubState=running\nUnitFileState=enabled\nMainPID=4711\nFragmentPath=/etc/systemd/system/qoffeeautomat.service\n",
                string.Empty),
        };

        UnitStatus status = await new SystemctlClient(runner).QueryAsync(Unit, CancellationToken.None);

        Assert.Equal(
            ["show", Unit, "--no-pager", "--property=LoadState,ActiveState,SubState,UnitFileState,MainPID,FragmentPath"],
            runner.Calls.Single().Arguments);
        Assert.True(status.IsInstalled);
        Assert.True(status.IsActive);
        Assert.Equal("running", status.SubState);
        Assert.Equal("enabled", status.UnitFileState);
        Assert.Equal(4711, status.MainPid);
        Assert.Equal("/etc/systemd/system/qoffeeautomat.service", status.FragmentPath);
    }

    [Fact]
    public void A_unit_that_systemd_does_not_know_is_not_installed()
    {
        UnitStatus status = SystemctlClient.ParseShow(Unit, "LoadState=not-found\nActiveState=inactive\nSubState=dead\nUnitFileState=\nMainPID=0\nFragmentPath=\n");

        Assert.False(status.IsInstalled);
        Assert.False(status.IsActive);
        Assert.Equal(0, status.MainPid);
        Assert.Equal(string.Empty, status.FragmentPath);
    }

    [Fact]
    public async Task A_failing_call_reports_what_systemd_said()
    {
        var runner = new FakeCommandRunner { Result = new CommandResult(1, string.Empty, "Job for qoffeeautomat.service failed.\n") };

        SystemctlException exception = await Assert.ThrowsAsync<SystemctlException>(() => new SystemctlClient(runner).StartAsync(Unit, CancellationToken.None));

        Assert.Contains("systemctl start qoffeeautomat.service", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exit code 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Job for qoffeeautomat.service failed.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_system_without_systemctl_is_an_error_with_a_hint()
    {
        var runner = new FakeCommandRunner { Result = null };

        SystemctlException exception = await Assert.ThrowsAsync<SystemctlException>(() => new SystemctlClient(runner).QueryAsync(Unit, CancellationToken.None));

        Assert.Contains("Is systemd running", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qoffeeautomat.service", true)]
    [InlineData("qoffee-automat_2.service", true)]
    [InlineData("getty@tty1.service", true)]
    [InlineData("qoffeeautomat", false)]
    [InlineData("qoffeeautomat.socket", false)]
    [InlineData("-rf.service", false)]
    [InlineData("--now.service", false)]
    [InlineData("a b.service", false)]
    [InlineData("../x.service", false)]
    [InlineData("a;b.service", false)]
    [InlineData("a\nb.service", false)]
    [InlineData("", false)]
    public void Only_plain_service_unit_names_reach_the_command_line(string name, bool valid)
    {
        Assert.Equal(valid, SystemctlClient.IsValidUnitName(name));
    }

    [Fact]
    public async Task An_invalid_unit_name_never_starts_a_program()
    {
        var runner = new FakeCommandRunner();
        var client = new SystemctlClient(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => client.StartAsync("--now.service", CancellationToken.None));

        Assert.Empty(runner.Calls);
    }
}
