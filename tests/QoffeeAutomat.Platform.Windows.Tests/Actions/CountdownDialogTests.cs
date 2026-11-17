using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Actions;
using QoffeeAutomat.SessionHost.Actions;
using QoffeeAutomat.SessionHost.Dialogs;

namespace QoffeeAutomat.Platform.Windows.Tests.Actions;

/// <summary>
/// The SessionHost's half of the countdown (PLAN.md S11, T715): the internal action <c>power.countdown</c> that shows the dialog and the
/// texts of the dialog. The window itself (<see cref="WinFormsCountdownDialog"/>) needs a desktop and is checked by hand; everything here
/// runs against <see cref="FakeCountdownDialog"/>.
/// </summary>
[Trait("Category", "PowerDelay")]
public sealed class CountdownDialogTests
{
    private readonly FakeCountdownDialog _dialog = new();
    private readonly FakeDesktopNativeApi _desktop = new();

    private UserBuiltinActions NewActions(ICountdownDialog? dialog) =>
        new(_desktop, new FakeAudioEndpoint(), new FakeProcessControl(), countdown: dialog);

    private static Dictionary<string, object> Countdown(long seconds = 30, string title = "Shut down", string? message = null)
    {
        var variables = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [PowerActionSpec.CountdownSeconds] = seconds,
            [PowerActionSpec.CountdownTitle] = title,
        };
        if (message is not null)
        {
            variables[PowerActionSpec.Message] = message;
        }

        return variables;
    }

    private static Task<ExecutionResult> Run(UserBuiltinActions actions, IReadOnlyDictionary<string, object> variables, CancellationToken cancellationToken = default)
    {
        return actions.ExecuteAsync(PowerActionSpec.Countdown, variables, cancellationToken).AsTask();
    }

    [Fact]
    public async Task The_dialog_gets_the_title_the_message_and_the_time()
    {
        UserBuiltinActions actions = NewActions(_dialog);

        Task<ExecutionResult> run = Run(actions, Countdown(120, "Restart", "Updates are installed."));
        CountdownDialogRequest request = Assert.Single(_dialog.Requests);

        Assert.Equal("Restart", request.Title);
        Assert.Equal("Updates are installed.", request.Message);
        Assert.Equal(TimeSpan.FromSeconds(120), request.Duration);

        _dialog.PressCancel();
        await run.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_press_of_the_cancel_button_ends_the_run_with_the_error_cancelled()
    {
        UserBuiltinActions actions = NewActions(_dialog);
        Task<ExecutionResult> run = Run(actions, Countdown());

        _dialog.PressCancel();
        ExecutionResult result = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ErrorCodes.Cancelled, result.ErrorCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_dialog_whose_time_is_over_ends_the_run_with_success()
    {
        UserBuiltinActions actions = NewActions(new ScriptedDialog(CountdownDialogOutcome.Elapsed));

        ExecutionResult result = await Run(actions, Countdown());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task The_agent_closing_the_run_closes_the_dialog_and_the_run_is_cancelled()
    {
        UserBuiltinActions actions = NewActions(_dialog);
        using var cancellation = new CancellationTokenSource();
        Task<ExecutionResult> run = Run(actions, Countdown(), cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(_dialog.ClosedByAgent);
    }

    [Fact]
    public async Task Without_a_dialog_the_action_is_refused_with_a_message_and_nothing_throws()
    {
        UserBuiltinActions actions = NewActions(dialog: null);

        ExecutionResult result = await Run(actions, Countdown());

        Assert.False(result.Succeeded);
        Assert.Contains("not available", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dialog_that_cannot_be_shown_is_reported_and_does_not_end_the_session_helper()
    {
        UserBuiltinActions actions = NewActions(new ThrowingDialog());

        ExecutionResult result = await Run(actions, Countdown());

        Assert.False(result.Succeeded);
        Assert.Contains("could not be shown", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(3601L)]
    public async Task A_time_outside_the_range_is_invalid_vars_and_shows_no_dialog(long seconds)
    {
        UserBuiltinActions actions = NewActions(_dialog);

        ExecutionResult result = await Run(actions, Countdown(seconds));

        Assert.Equal(ErrorCodes.InvalidVars, result.ErrorCode);
        Assert.Empty(_dialog.Requests);
    }

    [Fact]
    public async Task A_missing_or_too_long_title_or_a_too_long_message_is_invalid_vars()
    {
        UserBuiltinActions actions = NewActions(_dialog);

        ExecutionResult noTitle = await Run(actions, new Dictionary<string, object> { [PowerActionSpec.CountdownSeconds] = 10L });
        ExecutionResult longTitle = await Run(actions, Countdown(title: new string('t', 65)));
        ExecutionResult longMessage = await Run(actions, Countdown(message: new string('m', PowerActionSpec.MaxMessageLength + 1)));

        Assert.Equal(ErrorCodes.InvalidVars, noTitle.ErrorCode);
        Assert.Equal(ErrorCodes.InvalidVars, longTitle.ErrorCode);
        Assert.Equal(ErrorCodes.InvalidVars, longMessage.ErrorCode);
        Assert.Empty(_dialog.Requests);
    }

    [Fact]
    public void The_countdown_is_an_internal_action_that_no_catalog_lists_and_a_command_cannot_name()
    {
        UserBuiltinActions actions = NewActions(_dialog);

        Assert.DoesNotContain(actions.Actions, action => action.Name == PowerActionSpec.Countdown);
        Assert.DoesNotContain(WindowsActionCatalog.All, action => action.Name == PowerActionSpec.Countdown);
    }

    [Fact]
    public async Task The_logoff_in_the_session_ignores_the_delay_because_the_agent_already_waited()
    {
        UserBuiltinActions actions = NewActions(_dialog);
        var variables = new Dictionary<string, object>
        {
            [PowerActionSpec.DelaySeconds] = 600L,
            [PowerActionSpec.Message] = "bye",
        };

        ExecutionResult result = await actions.ExecuteAsync(WindowsActionNames.Logoff, variables, CancellationToken.None);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(_desktop.Calls, "logoff");
        Assert.Empty(_dialog.Requests);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(-3, "0:00")]
    [InlineData(1, "0:01")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(61, "1:01")]
    [InlineData(300, "5:00")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3661, "1:01:01")]
    public void The_remaining_time_is_shown_as_minutes_and_seconds_and_from_an_hour_with_hours(int seconds, string expected)
    {
        Assert.Equal(expected, CountdownText.Remaining(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void A_part_of_a_second_counts_as_a_whole_one_so_the_clock_never_shows_zero_before_the_end()
    {
        Assert.Equal("0:01", CountdownText.Remaining(TimeSpan.FromMilliseconds(100)));
        Assert.Equal("1:00", CountdownText.Remaining(TimeSpan.FromSeconds(59.2)));
        Assert.Equal("0:00", CountdownText.Remaining(TimeSpan.Zero));
    }

    [Fact]
    public void The_body_is_the_message_of_the_caller_or_a_sentence_that_says_what_is_going_to_happen()
    {
        Assert.Equal("Back in five.", CountdownText.Body("Sleep", "  Back in five.  "));
        Assert.Contains("Sleep", CountdownText.Body("Sleep", null), StringComparison.Ordinal);
        Assert.Contains("Cancel", CountdownText.Body("Sleep", "   "), StringComparison.Ordinal);
        Assert.Equal("QoffeeAutomat - Sleep", CountdownText.Caption("Sleep"));
        Assert.Equal("Cancel", CountdownText.CancelButton);
    }

    /// <summary>A dialog that ends at once with a fixed outcome.</summary>
    private sealed class ScriptedDialog(CountdownDialogOutcome outcome) : ICountdownDialog
    {
        public Task<CountdownDialogOutcome> ShowAsync(CountdownDialogRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(outcome);
        }
    }

    /// <summary>A dialog that cannot be shown (no desktop).</summary>
    private sealed class ThrowingDialog : ICountdownDialog
    {
        public Task<CountdownDialogOutcome> ShowAsync(CountdownDialogRequest request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("There is no desktop.");
        }
    }
}
