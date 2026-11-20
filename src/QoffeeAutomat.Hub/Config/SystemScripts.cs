using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Hub.Config;

/// <summary>
/// Finds what makes a profile a profile with system scripts (PLAN.md S20 "Hub security" item 7): publishing it needs the role admin, because the
/// code runs as <c>SYSTEM</c> on every PC the profile reaches. That is a command whose <c>runAs</c> is <c>system</c> and whose action runs code
/// of the profile author (<c>powershell</c>, <c>bash</c> or <c>process</c>; a <c>builtin</c> or a <c>sequence</c> runs nothing of its own), and,
/// because a command refers to a script file by name, also a script document that a command running as <c>system</c> uses. Without the second
/// rule a script of an unrelated profile could be replaced by a publisher who is not an administrator.
/// </summary>
public static class SystemScripts
{
    /// <summary>Tells whether a command document runs code of its author as <c>system</c>.</summary>
    /// <param name="command">The command body.</param>
    /// <returns>True for <c>runAs</c> <c>system</c> with a <c>powershell</c>, <c>bash</c> or <c>process</c> action.</returns>
    public static bool RunsCodeAsSystem(JsonElement command)
    {
        if (!string.Equals(ConfigJson.GetString(command, "runAs"), RunAsValues.System, StringComparison.Ordinal))
        {
            return false;
        }

        string? type = ActionType(command);
        return type is ActionTypes.PowerShell or ActionTypes.Bash or ActionTypes.Process;
    }

    /// <summary>The name of the script file that a command runs, if it runs one.</summary>
    /// <param name="command">The command body.</param>
    /// <returns>The script name for a <c>powershell</c> or <c>bash</c> action with <c>source</c> <c>file</c>; otherwise null.</returns>
    public static string? ScriptFileOf(JsonElement command)
    {
        string? type = ActionType(command);
        if (type is not (ActionTypes.PowerShell or ActionTypes.Bash)
            || command.ValueKind != JsonValueKind.Object
            || !command.TryGetProperty("action", out JsonElement action))
        {
            return null;
        }

        return string.Equals(ConfigJson.GetString(action, "source"), PowerShellSources.File, StringComparison.Ordinal)
            ? ConfigJson.GetString(action, "script")
            : null;
    }

    /// <summary>The names of the script files that commands running as <c>system</c> use.</summary>
    /// <param name="commands">The bodies of command documents.</param>
    /// <returns>The script names.</returns>
    public static HashSet<string> SystemScriptNames(IEnumerable<JsonElement> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement command in commands)
        {
            if (string.Equals(ConfigJson.GetString(command, "runAs"), RunAsValues.System, StringComparison.Ordinal)
                && ScriptFileOf(command) is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Finds what makes a set of documents contain system scripts.</summary>
    /// <param name="documents">The documents of a profile.</param>
    /// <param name="knownSystemScripts">Names of script files that commands running as <c>system</c> elsewhere (the library, other profiles) use.</param>
    /// <returns>The reasons, <c>command:&lt;id&gt;</c> and <c>script:&lt;name&gt;</c>, sorted; empty when there are none.</returns>
    public static List<string> Find(IEnumerable<ResolvedDocument> documents, IReadOnlySet<string> knownSystemScripts)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(knownSystemScripts);

        ResolvedDocument[] all = [.. documents];
        HashSet<string> systemScripts = SystemScriptNames(all.Where(static document => document.Kind == DocumentKinds.Command).Select(static document => document.Body));
        systemScripts.UnionWith(knownSystemScripts);

        var reasons = new List<string>();
        foreach (ResolvedDocument document in all)
        {
            if (document.Kind == DocumentKinds.Command && RunsCodeAsSystem(document.Body))
            {
                reasons.Add("command:" + document.Id);
            }
            else if (document.Kind == DocumentKinds.Script && systemScripts.Contains(document.Id))
            {
                reasons.Add("script:" + document.Id);
            }
        }

        reasons.Sort(StringComparer.Ordinal);
        return reasons;
    }

    private static string? ActionType(JsonElement command)
    {
        return command.ValueKind == JsonValueKind.Object && command.TryGetProperty("action", out JsonElement action)
            ? ConfigJson.GetString(action, "type")
            : null;
    }
}
