using System.ComponentModel;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Actions;
using QoffeeAutomat.SessionHost.Actions;

namespace QoffeeAutomat.Platform.Windows.Tests.Actions;

/// <summary>
/// The actions that run in the session of the user (PLAN.md S11, T306): lock, log off, displays, volume, mute, media keys,
/// launching programs, opening addresses and ending processes. Log off and <c>process.kill</c> are destructive; they run
/// against <see cref="FakeDesktopNativeApi"/> and <see cref="FakeProcessControl"/> and cannot end a session or a process of
/// the machine that runs the tests.
/// </summary>
[Trait("Category", "Actions")]
public sealed class DesktopActionTests
{
    private readonly FakeDesktopNativeApi _desktop = new();
    private readonly FakeAudioEndpoint _audio = new();
    private readonly FakeProcessControl _processes = new();
    private readonly UserBuiltinActions _actions;

    public DesktopActionTests()
    {
        _actions = new UserBuiltinActions(_desktop, _audio, _processes);
    }

    private async Task<ExecutionResult> RunAsync(string action, params (string Name, object Value)[] variables)
    {
        return await _actions.ExecuteAsync(action, ActionTestData.Vars(variables), CancellationToken.None);
    }

    [Fact]
    public async Task Lock_locks_the_session()
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.LockSession);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(_desktop.Calls, "lock");
    }

    [Fact]
    public async Task Log_off_waits_for_programs_by_default_and_forces_only_on_request()
    {
        ExecutionResult plain = await RunAsync(WindowsActionNames.Logoff);
        ExecutionResult forced = await RunAsync(WindowsActionNames.Logoff, (WindowsActionParameters.Force, true));

        Assert.True(plain.Succeeded);
        Assert.True(forced.Succeeded);
        ActionTestData.AssertCalls(_desktop.Calls, "logoff", "logoff:force");
    }

    [Fact]
    public async Task A_force_value_that_is_not_a_boolean_never_forces_the_log_off()
    {
        await RunAsync(WindowsActionNames.Logoff, (WindowsActionParameters.Force, "true"));

        ActionTestData.AssertCalls(_desktop.Calls, "logoff");
    }

    [Fact]
    public async Task Turning_the_displays_off_is_requested_from_the_desktop()
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.DisplayOff);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(_desktop.Calls, "display_off");
    }

    [Theory]
    [InlineData(WindowsActionNames.LockSession)]
    [InlineData(WindowsActionNames.Logoff)]
    [InlineData(WindowsActionNames.DisplayOff)]
    public async Task A_failing_desktop_call_becomes_a_failed_run_with_the_title_of_the_action(string action)
    {
        _desktop.Failure = new Win32Exception(5, "Access is denied.");

        ExecutionResult result = await RunAsync(action);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("failed: Access is denied.", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_volume_is_set_on_the_default_device()
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.SetVolume, (WindowsActionParameters.Level, 55L));

        Assert.True(result.Succeeded);
        Assert.Equal(55, _audio.Volume);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(101L)]
    public async Task A_volume_outside_the_range_is_refused_again_by_the_session_host(long level)
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.SetVolume, (WindowsActionParameters.Level, level));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Equal(30, _audio.Volume);
    }

    [Fact]
    public async Task A_missing_volume_is_refused()
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.SetVolume);

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
    }

    [Fact]
    public async Task Without_an_audio_device_the_volume_and_mute_runs_fail_with_an_explanation()
    {
        _audio.HasDevice = false;

        ExecutionResult volume = await RunAsync(WindowsActionNames.SetVolume, (WindowsActionParameters.Level, 10L));
        ExecutionResult mute = await RunAsync(WindowsActionNames.SetMute, (WindowsActionParameters.Muted, true));

        Assert.Equal(1, volume.ExitCode);
        Assert.Contains("no audio", volume.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, mute.ExitCode);
    }

    [Fact]
    public async Task Mute_mutes_by_default_and_unmutes_on_request()
    {
        ExecutionResult byDefault = await RunAsync(WindowsActionNames.SetMute);
        Assert.True(byDefault.Succeeded);
        Assert.True(_audio.Muted);

        ExecutionResult unmute = await RunAsync(WindowsActionNames.SetMute, (WindowsActionParameters.Muted, false));
        Assert.True(unmute.Succeeded);
        Assert.False(_audio.Muted);
    }

    [Theory]
    [InlineData("play_pause")]
    [InlineData("next")]
    [InlineData("previous")]
    [InlineData("stop")]
    public async Task A_media_key_is_pressed(string key)
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.MediaKey, (WindowsActionParameters.Key, key));

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(_desktop.MediaKeys, key);
    }

    [Theory]
    [InlineData("rewind")]
    [InlineData("")]
    public async Task An_unknown_media_key_is_refused(string key)
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.MediaKey, (WindowsActionParameters.Key, key));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_desktop.MediaKeys);
    }

    [Fact]
    public async Task A_program_is_launched_with_its_arguments_as_given()
    {
        ExecutionResult result = await RunAsync(
            WindowsActionNames.AppLaunch,
            (WindowsActionParameters.Path, "C:\\Tools\\app.exe"),
            (WindowsActionParameters.Arguments, "--name \"a b\" & more"));

        Assert.True(result.Succeeded);
        (string path, string? arguments) = Assert.Single(_desktop.Launches);
        Assert.Equal("C:\\Tools\\app.exe", path);
        Assert.Equal("--name \"a b\" & more", arguments);
    }

    [Fact]
    public async Task A_launch_without_arguments_passes_null()
    {
        await RunAsync(WindowsActionNames.AppLaunch, (WindowsActionParameters.Path, "C:\\Tools\\app.exe"), (WindowsActionParameters.Arguments, string.Empty));

        Assert.Null(Assert.Single(_desktop.Launches).Arguments);
    }

    [Theory]
    [InlineData("app.exe")]
    [InlineData("..\\app.exe")]
    [InlineData("C:app.exe")]
    [InlineData("\\\\.\\pipe\\x")]
    [InlineData("C:\\Tools\\app?.exe")]
    public async Task A_launch_of_something_that_is_not_a_full_path_is_refused(string path)
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.AppLaunch, (WindowsActionParameters.Path, path));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_desktop.Launches);
    }

    [Fact]
    public async Task A_batch_file_is_not_given_arguments_that_the_command_interpreter_would_act_on()
    {
        ExecutionResult result = await RunAsync(
            WindowsActionNames.AppLaunch,
            (WindowsActionParameters.Path, "C:\\Scripts\\run.cmd"),
            (WindowsActionParameters.Arguments, "x & del *.*"));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_desktop.Launches);
    }

    [Fact]
    public async Task A_web_address_is_opened_in_the_browser()
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.UrlOpen, (WindowsActionParameters.Url, "https://example.com/path?q=1"));

        Assert.True(result.Succeeded);
        Assert.Equal("https://example.com/path?q=1", Assert.Single(_desktop.OpenedUrls).AbsoluteUri);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/")]
    [InlineData("https://user:secret@example.com/")]
    [InlineData("https://example.com/a b")]
    [InlineData("example.com")]
    [InlineData("")]
    public async Task Only_plain_http_and_https_addresses_are_opened(string url)
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.UrlOpen, (WindowsActionParameters.Url, url));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_desktop.OpenedUrls);
    }

    [Fact]
    public async Task Process_kill_ends_every_process_of_that_name_and_strips_exe()
    {
        _processes.Add("notepad", 101, 102);

        ExecutionResult result = await RunAsync(WindowsActionNames.ProcessKill, (WindowsActionParameters.Name, "Notepad.exe"));

        Assert.True(result.Succeeded);
        Assert.Contains("2", result.StandardOutput, StringComparison.Ordinal);
        ActionTestData.AssertCalls(_processes.Lookups, "Notepad");
        ActionTestData.AssertIds(_processes.Ended, 101, 102);
    }

    [Fact]
    public async Task Process_kill_never_ends_the_session_host_itself()
    {
        _processes.Add("worker", _processes.CurrentProcessId, 7);

        ExecutionResult result = await RunAsync(WindowsActionNames.ProcessKill, (WindowsActionParameters.Name, "worker"));

        Assert.True(result.Succeeded);
        ActionTestData.AssertIds(_processes.Ended, 7);
    }

    [Fact]
    public async Task Process_kill_without_a_matching_process_fails_with_an_explanation()
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.ProcessKill, (WindowsActionParameters.Name, "ghost"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ghost", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(_processes.Ended);
    }

    [Fact]
    public async Task Process_kill_reports_the_processes_that_could_not_be_ended()
    {
        _processes.Add("stuck", 1, 2);
        _processes.Stubborn[2] = "access denied";

        ExecutionResult result = await RunAsync(WindowsActionNames.ProcessKill, (WindowsActionParameters.Name, "stuck"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("access denied", result.StandardError, StringComparison.Ordinal);
        ActionTestData.AssertIds(_processes.Ended, 1);
    }

    [Theory]
    [InlineData("lsass")]
    [InlineData("CSRSS.exe")]
    [InlineData("winlogon")]
    [InlineData("svchost")]
    [InlineData("QoffeeAutomat.Agent.exe")]
    [InlineData("qoffeeautomat.sessionhost")]
    public async Task Process_kill_refuses_the_processes_that_keep_windows_and_this_product_running(string name)
    {
        _processes.Add(name.Replace(".exe", string.Empty, StringComparison.OrdinalIgnoreCase), 9);

        ExecutionResult result = await RunAsync(WindowsActionNames.ProcessKill, (WindowsActionParameters.Name, name));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_processes.Lookups);
        Assert.Empty(_processes.Ended);
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("C:\\Windows\\notepad.exe")]
    [InlineData("note*")]
    [InlineData("")]
    [InlineData(".exe")]
    public async Task Process_kill_refuses_a_name_that_is_not_a_plain_image_name(string name)
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.ProcessKill, (WindowsActionParameters.Name, name));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_processes.Lookups);
    }

    [Fact]
    public async Task An_unknown_action_is_a_bad_request()
    {
        ExecutionResult result = await RunAsync("desktop.explode");

        Assert.Equal(ErrorCodes.BadRequest, result.ErrorCode);
        Assert.Empty(_desktop.Calls);
    }

    [Fact]
    public async Task The_power_actions_do_not_exist_in_the_session_host()
    {
        ExecutionResult result = await RunAsync(WindowsActionNames.Shutdown);

        Assert.Equal(ErrorCodes.BadRequest, result.ErrorCode);
        Assert.Empty(_desktop.Calls);
    }
}
