using System.Globalization;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Core.Commands.Conditions;

/// <summary>The outcome of checking the conditions of a command.</summary>
/// <param name="IsMet">True when the command may run.</param>
/// <param name="Reason">Why it may not run; null when <paramref name="IsMet"/> is true. Never contains variable values.</param>
public sealed record ConditionVerdict(bool IsMet, string? Reason = null)
{
    /// <summary>The conditions hold.</summary>
    public static ConditionVerdict Met { get; } = new(true);

    /// <summary>Creates a verdict that blocks the run.</summary>
    /// <param name="reason">The explanation.</param>
    /// <returns>The verdict.</returns>
    public static ConditionVerdict Blocked(string reason) => new(false, reason);
}

/// <summary>
/// Checks the <see cref="CommandConditions"/> of a command (PLAN.md S06, T705): the state of the interactive session and a
/// window of the local day. Pure: the caller supplies the session state and the local time, so the check can be tested
/// without a clock or a session.
/// </summary>
public static class CommandConditionEvaluator
{
    /// <summary>Checks the conditions.</summary>
    /// <param name="conditions">The conditions; null means there are none.</param>
    /// <param name="session">The current session state.</param>
    /// <param name="localNow">The current local time of the PC.</param>
    /// <returns>Whether the command may run, and if not, why.</returns>
    public static ConditionVerdict Evaluate(CommandConditions? conditions, SessionState session, DateTimeOffset localNow)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (conditions is null)
        {
            return ConditionVerdict.Met;
        }

        ConditionVerdict sessionVerdict = EvaluateSession(conditions.Session, session);
        if (!sessionVerdict.IsMet)
        {
            return sessionVerdict;
        }

        if (conditions.TimeWindow is { } window && !IsInside(window, localNow))
        {
            return ConditionVerdict.Blocked(
                $"The command may only run between {window.From} and {window.To}{DescribeDays(window)}.");
        }

        return ConditionVerdict.Met;
    }

    /// <summary>Tells whether a time of day is inside a window.</summary>
    /// <param name="window">The window; a window that cannot be read is never inside.</param>
    /// <param name="localNow">The current local time.</param>
    /// <returns>True when the window is open.</returns>
    public static bool IsInside(TimeWindow window, DateTimeOffset localNow)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!TryParseTime(window.From, out TimeSpan from) || !TryParseTime(window.To, out TimeSpan to))
        {
            return false;
        }

        TimeSpan time = localNow.TimeOfDay;
        string today = WeekdayNames.Of(localNow.DayOfWeek);
        string yesterday = WeekdayNames.Of(localNow.AddDays(-1).DayOfWeek);

        if (from < to)
        {
            return time >= from && time < to && DayAllowed(window, today);
        }

        // The window crosses midnight (or is exactly one day long): the part before midnight belongs to today's
        // start, the part after midnight to the start of the day before.
        if (time >= from)
        {
            return DayAllowed(window, today);
        }

        return time < to && DayAllowed(window, yesterday);
    }

    /// <summary>Reads a time of day in the form <c>HH:mm</c> (24-hour clock).</summary>
    /// <param name="text">The text.</param>
    /// <param name="time">The time since midnight.</param>
    /// <returns>True when the text is valid.</returns>
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (text is not { Length: 5 } || text[2] != ':')
        {
            return false;
        }

        if (!int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || hours > 23
            || minutes > 59)
        {
            return false;
        }

        time = new TimeSpan(hours, minutes, 0);
        return true;
    }

    private static ConditionVerdict EvaluateSession(string? requirement, SessionState session)
    {
        bool loggedIn = session.UserName is not null;
        switch (requirement)
        {
            case SessionRequirements.LoggedIn:
                return loggedIn
                    ? ConditionVerdict.Met
                    : ConditionVerdict.Blocked("The command needs a logged-in user and nobody is logged in.");
            case SessionRequirements.Unlocked:
                if (!loggedIn)
                {
                    return ConditionVerdict.Blocked("The command needs an unlocked session and nobody is logged in.");
                }

                return session.IsLocked
                    ? ConditionVerdict.Blocked("The command needs an unlocked session, but the session is locked.")
                    : ConditionVerdict.Met;
            case SessionRequirements.Locked:
                if (!loggedIn)
                {
                    return ConditionVerdict.Blocked("The command needs a locked session and nobody is logged in.");
                }

                return session.IsLocked
                    ? ConditionVerdict.Met
                    : ConditionVerdict.Blocked("The command needs a locked session, but the session is not locked.");
            default:
                // Absent and "any" impose nothing; an unknown value was rejected when the document was saved.
                return ConditionVerdict.Met;
        }
    }

    private static bool DayAllowed(TimeWindow window, string day)
    {
        return window.Days is not { Count: > 0 } days || days.Contains(day, StringComparer.Ordinal);
    }

    private static string DescribeDays(TimeWindow window)
    {
        return window.Days is { Count: > 0 } days ? " on " + string.Join(", ", days) : string.Empty;
    }
}
