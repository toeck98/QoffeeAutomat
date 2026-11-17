using System.Text.Json;

namespace QoffeeAutomat.Contracts.Config;

/// <summary>
/// The built-in actions of the power management (PLAN.md S11, S16, T715): <c>power.keep_awake</c>, <c>power.cancel</c> and the
/// optional variables <c>DelaySeconds</c> and <c>Message</c> of <c>power.shutdown</c>, <c>power.restart</c>, <c>power.sleep</c>,
/// <c>power.hibernate</c> and <c>session.logoff</c> (a countdown dialog with a cancel button). Defined once for the same reason as
/// <see cref="NotifyToastSpec"/>: the Windows action catalog of the agent and the provider of the SessionHost, which shows the
/// dialog and runs <c>session.logoff</c>, cannot reference each other.
/// </summary>
public static class PowerActionSpec
{
    /// <summary>The name of the action that keeps the computer awake.</summary>
    public const string KeepAwake = "power.keep_awake";

    /// <summary>The name of the action that cancels the pending delayed power action.</summary>
    public const string Cancel = "power.cancel";

    /// <summary>
    /// The name of the internal action that shows the countdown dialog in the SessionHost. The agent sends it as a <c>run</c> of kind
    /// <c>builtin</c> over the session pipe and cancels the run (<c>cancel</c>) when the countdown ends for another reason. It is
    /// <b>not</b> in any catalog and a command cannot name it: only the agent can reach it.
    /// </summary>
    public const string Countdown = "power.countdown";

    /// <summary>The id of the metric that reports whether the agent keeps the computer awake.</summary>
    public const string KeepAwakeMetric = "power.keep_awake";

    /// <summary>The variable of <see cref="KeepAwake"/> that switches it on (true) or off (false).</summary>
    public const string On = "On";

    /// <summary>The variable of <see cref="KeepAwake"/> that also keeps the displays on.</summary>
    public const string Display = "Display";

    /// <summary>The variable of <see cref="KeepAwake"/> that ends it after some minutes; 0 means until it is switched off.</summary>
    public const string Minutes = "Minutes";

    /// <summary>The variable that delays a power action; 0 (the default) acts at once.</summary>
    public const string DelaySeconds = "DelaySeconds";

    /// <summary>The variable that is shown in the countdown dialog.</summary>
    public const string Message = "Message";

    /// <summary>The variable of <see cref="Countdown"/> with the headline of the dialog (the title of the action).</summary>
    public const string CountdownTitle = "Title";

    /// <summary>The variable of <see cref="Countdown"/> with the remaining seconds when the dialog opens.</summary>
    public const string CountdownSeconds = "Seconds";

    /// <summary>The longest delay, one hour.</summary>
    public const int MaxDelaySeconds = 3600;

    /// <summary>The longest message of the countdown dialog.</summary>
    public const int MaxMessageLength = 256;

    /// <summary>The longest time that <see cref="KeepAwake"/> accepts, one week.</summary>
    public const int MaxKeepAwakeMinutes = 10080;

    /// <summary>The title of <see cref="KeepAwake"/> in the editor.</summary>
    public const string KeepAwakeTitle = "Keep awake";

    /// <summary>The description of <see cref="KeepAwake"/>.</summary>
    public const string KeepAwakeDescription =
        "Keeps the computer from going to sleep (and with Display also keeps the screen on) through a power request that the agent owns. The request ends when it is switched off, when the time is over, and with the agent; it is not restored after a restart.";

    /// <summary>The title of <see cref="Cancel"/> in the editor.</summary>
    public const string CancelTitle = "Cancel pending power action";

    /// <summary>The description of <see cref="Cancel"/>.</summary>
    public const string CancelDescription =
        "Cancels the delayed power action that is waiting (shutdown, restart, sleep, hibernate or log off). Its run ends with the error cancelled and the event power_action_cancelled is raised. Does nothing when nothing is pending.";

    /// <summary>The variables of <see cref="KeepAwake"/>.</summary>
    public static IReadOnlyList<ParameterDefinition> KeepAwakeParameters { get; } =
    [
        new ParameterDefinition(
            On,
            ParameterTypes.Flag,
            Default: Json("true"),
            Description: "True keeps the computer awake, false lets it sleep again."),
        new ParameterDefinition(
            Display,
            ParameterTypes.Flag,
            Default: Json("false"),
            Description: "Also keep the screen on (otherwise only the computer is kept from sleeping)."),
        new ParameterDefinition(
            Minutes,
            ParameterTypes.WholeNumber,
            Default: Json("0"),
            Min: 0,
            Max: MaxKeepAwakeMinutes,
            Step: 1,
            Unit: "min",
            Description: "How long to keep the computer awake; 0 means until it is switched off."),
    ];

    /// <summary>The variables of <see cref="Cancel"/>: none.</summary>
    public static IReadOnlyList<ParameterDefinition> CancelParameters { get; } = [];

    /// <summary>The optional variables that the five delayable actions share, after their own ones.</summary>
    public static IReadOnlyList<ParameterDefinition> DelayParameters { get; } =
    [
        new ParameterDefinition(
            DelaySeconds,
            ParameterTypes.WholeNumber,
            Default: Json("0"),
            Min: 0,
            Max: MaxDelaySeconds,
            Step: 1,
            Unit: "s",
            Description: "Wait this long before acting. With a user logged in a countdown dialog with a cancel button is shown; 0 acts at once. Only one delayed power action can be pending."),
        new ParameterDefinition(
            Message,
            ParameterTypes.Text,
            MaxLength: MaxMessageLength,
            Description: "A text for the countdown dialog."),
    ];

    private static JsonElement Json(string raw)
    {
        using JsonDocument document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}
