namespace QoffeeAutomat.Contracts.Config;

/// <summary>Schema version of the configuration documents.</summary>
public static class DocumentSchema
{
    /// <summary>The schema version of sensor documents, metric overrides and the settings.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// The schema version of command documents. Version 2 (T705, T706) adds the optional members <c>conditions</c> and
    /// <c>secrets</c>; documents of version 1 are migrated forward when they are read (PLAN.md S13).
    /// </summary>
    public const int CommandVersion = 2;
}

/// <summary>The values of <c>conditions.session</c>: the state of the interactive session that a command needs (PLAN.md S06, T705).</summary>
public static class SessionRequirements
{
    /// <summary>No requirement.</summary>
    public const string Any = "any";

    /// <summary>A user is logged in; the session may be locked.</summary>
    public const string LoggedIn = "logged_in";

    /// <summary>A user is logged in and the session is not locked.</summary>
    public const string Unlocked = "unlocked";

    /// <summary>A user is logged in and the session is locked.</summary>
    public const string Locked = "locked";
}

/// <summary>The day names of <c>conditions.timeWindow.days</c>.</summary>
public static class WeekdayNames
{
    /// <summary>The valid names, Monday first.</summary>
    public static IReadOnlyList<string> All { get; } = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    /// <summary>Returns the name of a day of the week.</summary>
    /// <param name="day">The day.</param>
    /// <returns>The three-letter lower-case name.</returns>
    public static string Of(DayOfWeek day)
    {
        return day switch
        {
            DayOfWeek.Monday => "mon",
            DayOfWeek.Tuesday => "tue",
            DayOfWeek.Wednesday => "wed",
            DayOfWeek.Thursday => "thu",
            DayOfWeek.Friday => "fri",
            DayOfWeek.Saturday => "sat",
            _ => "sun",
        };
    }
}

/// <summary>The values of <c>origin</c> in the common document header.</summary>
public static class DocumentOrigins
{
    /// <summary>Authored on this agent.</summary>
    public const string Local = "local";

    /// <summary>Pushed by a hub.</summary>
    public const string Hub = "hub";
}

/// <summary>The values of <c>runAs</c> (D-010, D-036).</summary>
public static class RunAsValues
{
    /// <summary>Runs as the service account.</summary>
    public const string System = "system";

    /// <summary>Runs in the session of the interactive user.</summary>
    public const string User = "user";

    /// <summary>Runs in the user's session with the elevated token of an administrator.</summary>
    public const string UserElevated = "user_elevated";
}

/// <summary>The values of <c>concurrency</c>; the semantics are those of Home Assistant script modes.</summary>
public static class ConcurrencyModes
{
    /// <summary>A new run is rejected while one is running.</summary>
    public const string Exclusive = "single";

    /// <summary>A new run waits for the running one.</summary>
    public const string Queue = "queue";

    /// <summary>Runs may overlap.</summary>
    public const string Parallel = "parallel";

    /// <summary>A new run cancels the running one.</summary>
    public const string Restart = "restart";
}

/// <summary>The values of <c>action.type</c>.</summary>
public static class ActionTypes
{
    /// <summary>PowerShell code or script file.</summary>
    public const string PowerShell = "powershell";

    /// <summary>A bash script, inline or a file in the scripts directory (Linux, PLAN.md S17, T803).</summary>
    public const string Bash = "bash";

    /// <summary>An executable started without a shell.</summary>
    public const string Process = "process";

    /// <summary>A platform-provided action.</summary>
    public const string Builtin = "builtin";

    /// <summary>Other commands run in order.</summary>
    public const string Sequence = "sequence";
}

/// <summary>The values of <c>source</c> of PowerShell code.</summary>
public static class PowerShellSources
{
    /// <summary>The code is stored in the document.</summary>
    public const string Inline = "inline";

    /// <summary>The code is a file in the scripts directory.</summary>
    public const string File = "file";
}

/// <summary>The values of <c>source</c> of bash code (T803).</summary>
public static class BashSources
{
    /// <summary>The script is stored in the document.</summary>
    public const string Inline = "inline";

    /// <summary>The script is a file in the scripts directory.</summary>
    public const string File = "file";
}

/// <summary>
/// Which action types an operating system can run (PLAN.md S17, T803). The rule is shared by the agent, which refuses to run or
/// save such a command, and the editor, which flags it: a command document with an action type that the platform lacks is kept
/// as it is, never deleted or rewritten, so that it works again on a platform that supports it.
/// </summary>
public static class PlatformActionSupport
{
    /// <summary>The value of <c>os.name</c> on Windows.</summary>
    public const string WindowsOs = "windows";

    /// <summary>The value of <c>os.name</c> on Linux.</summary>
    public const string LinuxOs = "linux";

    /// <summary>Tells whether an operating system can run an action type.</summary>
    /// <param name="os">The operating system family: <c>windows</c>, <c>linux</c> or <c>simulated</c>; anything else supports every type.</param>
    /// <param name="actionType">The <c>action.type</c>.</param>
    /// <returns>
    /// False for <c>powershell</c> on Linux (the display profiles, the registry and the other scripts of Windows do not exist there)
    /// and for <c>bash</c> on Windows; true otherwise, including every type on the simulated platform.
    /// </returns>
    public static bool IsActionTypeSupported(string? os, string? actionType)
    {
        if (string.Equals(os, LinuxOs, StringComparison.OrdinalIgnoreCase))
        {
            return !string.Equals(actionType, ActionTypes.PowerShell, StringComparison.Ordinal);
        }

        if (string.Equals(os, WindowsOs, StringComparison.OrdinalIgnoreCase))
        {
            return !string.Equals(actionType, ActionTypes.Bash, StringComparison.Ordinal);
        }

        return true;
    }

    /// <summary>The explanation that goes with an unsupported action type.</summary>
    /// <param name="os">The operating system family.</param>
    /// <param name="actionType">The <c>action.type</c>.</param>
    /// <returns>A sentence that says what the platform lacks and what to use instead.</returns>
    public static string Explain(string? os, string? actionType)
    {
        return string.Equals(os, LinuxOs, StringComparison.OrdinalIgnoreCase)
            ? $"The '{actionType}' action is available on Windows only and is not supported on Linux; use a bash or process action instead. The command is kept."
            : $"The '{actionType}' action is available on Linux only and is not supported on Windows; use a powershell or process action instead. The command is kept.";
    }
}

/// <summary>The values of <c>engine</c> of PowerShell code.</summary>
public static class PowerShellEngines
{
    /// <summary>pwsh if installed, otherwise Windows PowerShell.</summary>
    public const string Auto = "auto";

    /// <summary>PowerShell 7 or newer.</summary>
    public const string Pwsh = "pwsh";

    /// <summary>Windows PowerShell 5.1.</summary>
    public const string PowerShell = "powershell";
}

/// <summary>The values of <c>onError</c> of a sequence.</summary>
public static class SequenceErrorModes
{
    /// <summary>The first failing step ends the sequence.</summary>
    public const string Stop = "stop";

    /// <summary>The sequence continues after a failing step.</summary>
    public const string Continue = "continue";
}

/// <summary>The values of the parameter <c>type</c> (the wire names are string, int, float, bool, enum).</summary>
public static class ParameterTypes
{
    /// <summary>A string.</summary>
    public const string Text = "string";

    /// <summary>A whole number.</summary>
    public const string WholeNumber = "int";

    /// <summary>A floating-point number.</summary>
    public const string Real = "float";

    /// <summary>A boolean.</summary>
    public const string Flag = "bool";

    /// <summary>One of a list of strings.</summary>
    public const string Choice = "enum";
}

/// <summary>The values of <c>haControl.type</c>.</summary>
public static class HaControlTypes
{
    /// <summary>No entity; the command is only callable through the run_command action.</summary>
    public const string None = "none";

    /// <summary>A Home Assistant button.</summary>
    public const string Button = "button";

    /// <summary>A Home Assistant switch (bool parameter).</summary>
    public const string Switch = "switch";

    /// <summary>A Home Assistant select (enum parameter).</summary>
    public const string Select = "select";

    /// <summary>A Home Assistant number (int or float parameter).</summary>
    public const string Number = "number";

    /// <summary>A Home Assistant text (string parameter).</summary>
    public const string Text = "text";
}
