using System.Globalization;

namespace QoffeeAutomat.Platform.Linux.Metrics.Session;

/// <summary>
/// Turns the text that logind produces into <see cref="LogindSession"/> values (PLAN.md S17, T801). Pure functions on text, so that
/// tests feed them the output of <c>loginctl</c> and the content of the session files of a real system.
/// </summary>
public static class LogindSessionParser
{
    private static readonly char[] ColumnSeparators = [' ', '\t'];

    /// <summary>Reads the session ids from <c>loginctl list-sessions --no-legend</c>: one session per line, the id is the first column.</summary>
    /// <param name="output">The standard output of the command.</param>
    /// <returns>The ids; entries that are not a plain session id (letters and digits) are skipped.</returns>
    public static IReadOnlyList<string> ParseSessionIds(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var ids = new List<string>();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] columns = line.Split(ColumnSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length > 0 && IsSessionId(columns[0]))
            {
                ids.Add(columns[0]);
            }
        }

        return ids;
    }

    /// <summary>Tells whether text can be a session id; ids reach a command line, so only letters and digits pass.</summary>
    /// <param name="text">The text.</param>
    /// <returns>True for a non-empty text of at most 32 ASCII letters and digits.</returns>
    public static bool IsSessionId(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        const int longest = 32;
        return text.Length is > 0 and <= longest && text.All(char.IsAsciiLetterOrDigit);
    }

    /// <summary>Reads the output of <c>loginctl show-session</c> (<c>Name=value</c> lines, properties in PascalCase).</summary>
    /// <param name="id">The session id that was asked for; used when the output carries no <c>Id</c>.</param>
    /// <param name="output">The standard output of the command.</param>
    /// <returns>The session; null when the output does not name a user.</returns>
    public static LogindSession? ParseShowSession(string id, string output)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(output);

        Dictionary<string, string> values = ParseKeyValues(output);
        return Build(
            values.GetValueOrDefault("Id") ?? id,
            values.GetValueOrDefault("Name"),
            values.GetValueOrDefault("User"),
            values.GetValueOrDefault("Seat"),
            values.GetValueOrDefault("Class"),
            values.GetValueOrDefault("Type"),
            values.GetValueOrDefault("State"),
            values.GetValueOrDefault("Active"),
            values.GetValueOrDefault("Remote"),
            values.GetValueOrDefault("LockedHint"));
    }

    /// <summary>Reads a session file of logind, <c>/run/systemd/sessions/&lt;id&gt;</c> (<c>KEY=value</c> lines in upper case).</summary>
    /// <param name="id">The session id, the name of the file.</param>
    /// <param name="content">The content of the file.</param>
    /// <returns>The session; null when the file does not name a user. <c>IsLocked</c> is null unless the file carries <c>LOCKED_HINT</c>.</returns>
    public static LogindSession? ParseSessionFile(string id, string content)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(content);

        Dictionary<string, string> values = ParseKeyValues(content);
        return Build(
            id,
            values.GetValueOrDefault("USER"),
            values.GetValueOrDefault("UID"),
            values.GetValueOrDefault("SEAT"),
            values.GetValueOrDefault("CLASS"),
            values.GetValueOrDefault("TYPE"),
            values.GetValueOrDefault("STATE"),
            values.GetValueOrDefault("ACTIVE"),
            values.GetValueOrDefault("REMOTE"),
            values.GetValueOrDefault("LOCKED_HINT"));
    }

    private static Dictionary<string, string> ParseKeyValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The session files start with a comment ("This is private data. Do not parse."); the format is not a promise of
            // logind, which is why this source is only the fallback behind loginctl.
            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && !line.StartsWith('#'))
            {
                values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return values;
    }

    private static LogindSession? Build(
        string id,
        string? user,
        string? uid,
        string? seat,
        string? sessionClass,
        string? sessionType,
        string? state,
        string? active,
        string? remote,
        string? locked)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            return null;
        }

        // The user id of show-session is "1000" or, in older versions, "1000 (name)".
        long? numericUid = null;
        string[] uidFields = (uid ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (uidFields.Length > 0 && long.TryParse(uidFields[0], NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
        {
            numericUid = parsed;
        }

        string normalizedState = string.IsNullOrWhiteSpace(state) ? "online" : state.Trim().ToLowerInvariant();
        return new LogindSession(
            id,
            user.Trim(),
            numericUid,
            NonEmpty(seat),
            string.IsNullOrWhiteSpace(sessionClass) ? "user" : sessionClass.Trim().ToLowerInvariant(),
            NonEmpty(sessionType)?.ToLowerInvariant(),
            normalizedState,
            ParseBool(active) ?? string.Equals(normalizedState, "active", StringComparison.Ordinal),
            ParseBool(remote) ?? false,
            ParseBool(locked));
    }

    private static string? NonEmpty(string? text)
    {
        return string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), "n/a", StringComparison.OrdinalIgnoreCase) ? null : text.Trim();
    }

    private static bool? ParseBool(string? text)
    {
        return text?.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "1" or "on" => true,
            "no" or "false" or "0" or "off" => false,
            _ => null,
        };
    }
}
