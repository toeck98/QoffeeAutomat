using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Core.History;

/// <summary>One run of a command as kept in the history (PLAN.md S06 T207, S09 item 8).</summary>
/// <param name="Id">The run id.</param>
/// <param name="Command">The command id.</param>
/// <param name="Source">Where the run came from: <c>ha</c>, <c>ui</c>, <c>hub</c>, <c>cli</c> or <c>sequence</c>.</param>
/// <param name="ClientId">The link client or session that asked, when known.</param>
/// <param name="UserId">The Home Assistant user on whose behalf the command ran, when supplied.</param>
/// <param name="Variables">The variables as a JSON object; values of parameters marked <c>secret</c> are replaced by <see cref="RunVariables.RedactedValue"/>.</param>
/// <param name="StartedAt">When the run started (for a rejected request: when it was rejected).</param>
/// <param name="Duration">How long the run took; zero for a rejected request.</param>
/// <param name="ExitCode">The exit code; null when nothing ran.</param>
/// <param name="ErrorCode">A QoffeeLink error code (PLAN.md S07) when the run failed before or instead of producing an exit code; null otherwise.</param>
/// <param name="StandardOutput">Standard output, truncated to the history limit.</param>
/// <param name="StandardError">Standard error (or the error message), truncated to the history limit.</param>
/// <param name="OutputTruncated">Output was cut, by the capture cap or by the history limit.</param>
/// <param name="ParentRunId">The run of the sequence that started this run, or null.</param>
public sealed record RunRecord(
    string Id,
    string Command,
    string Source,
    string? ClientId,
    string? UserId,
    JsonElement Variables,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int? ExitCode,
    string? ErrorCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated,
    string? ParentRunId = null)
{
    /// <summary>True when the run ended with exit code zero and no error code.</summary>
    public bool Succeeded => ErrorCode is null && ExitCode == 0;
}

/// <summary>Filter and paging of <see cref="IRunHistory.QueryAsync"/>.</summary>
/// <param name="Command">Only runs of this command.</param>
/// <param name="Source">Only runs from this source.</param>
/// <param name="FailedOnly">Only runs that did not succeed.</param>
/// <param name="Limit">The most runs to return (1 to 1000).</param>
/// <param name="Offset">How many of the newest matching runs to skip.</param>
public sealed record RunQuery(string? Command = null, string? Source = null, bool FailedOnly = false, int Limit = 50, int Offset = 0);

/// <summary>The history of command runs (PLAN.md D-013, S08 <c>/runs</c>).</summary>
public interface IRunHistory
{
    /// <summary>Stores a run and applies the retention rules now and then.</summary>
    /// <param name="record">The run; it must already be redacted.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the run is stored.</returns>
    Task AddAsync(RunRecord record, CancellationToken cancellationToken);

    /// <summary>Reads one run.</summary>
    /// <param name="id">The run id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The run, or null when it is unknown or was pruned.</returns>
    Task<RunRecord?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>Lists runs, newest first.</summary>
    /// <param name="query">Filter and paging.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The runs.</returns>
    Task<IReadOnlyList<RunRecord>> QueryAsync(RunQuery query, CancellationToken cancellationToken);

    /// <summary>Applies the retention rules (age and count) now.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of runs that were removed.</returns>
    Task<int> PruneAsync(CancellationToken cancellationToken);
}

/// <summary>Redaction of variable values for history, logs and audit entries (PLAN.md S06 <c>secret</c>, S09 item 8).</summary>
public static class RunVariables
{
    /// <summary>What replaces the value of a secret variable.</summary>
    public const string RedactedValue = "***";

    /// <summary>The shortest secret that is also removed from stored output; shorter values would garble ordinary text.</summary>
    public const int MinimumRedactedOutputLength = 4;

    /// <summary>Describes bound values; secrets are redacted.</summary>
    /// <param name="parameters">The parameter definitions of the command.</param>
    /// <param name="values">The bound values by name (string, long, double, bool).</param>
    /// <returns>A JSON object.</returns>
    public static JsonElement FromBound(IReadOnlyList<ParameterDefinition>? parameters, IReadOnlyDictionary<string, object> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        HashSet<string> secrets = SecretNames(parameters);
        return EventData.Create([.. values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, (object?)(secrets.Contains(pair.Key) ? RedactedValue : pair.Value)))]);
    }

    /// <summary>
    /// Describes values as they were sent, before they were validated. Secrets are redacted, and so are
    /// names that the command does not declare, because nothing says whether they are secret.
    /// </summary>
    /// <param name="parameters">The parameter definitions of the command.</param>
    /// <param name="values">The values by name; null means none.</param>
    /// <returns>A JSON object.</returns>
    public static JsonElement FromRaw(IReadOnlyList<ParameterDefinition>? parameters, IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (values is null || values.Count == 0)
        {
            return EventData.Create();
        }

        HashSet<string> secrets = SecretNames(parameters);
        HashSet<string> declared = [.. parameters?.Select(parameter => parameter.Name) ?? []];
        return EventData.Create([.. values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, secrets.Contains(pair.Key) || !declared.Contains(pair.Key) ? (object?)RedactedValue : pair.Value))]);
    }

    /// <summary>Removes the values of secret variables from output text.</summary>
    /// <param name="text">Output of a run.</param>
    /// <param name="parameters">The parameter definitions of the command.</param>
    /// <param name="values">The bound values.</param>
    /// <returns>The text with every occurrence of a secret value (at least <see cref="MinimumRedactedOutputLength"/> characters) replaced.</returns>
    public static string RedactOutput(string text, IReadOnlyList<ParameterDefinition>? parameters, IReadOnlyDictionary<string, object>? values)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0 || values is null)
        {
            return text;
        }

        string result = text;
        foreach (string name in SecretNames(parameters))
        {
            if (values.TryGetValue(name, out object? value)
                && Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) is { Length: >= MinimumRedactedOutputLength } secret)
            {
                result = result.Replace(secret, RedactedValue, StringComparison.Ordinal);
            }
        }

        return result;
    }

    private static HashSet<string> SecretNames(IReadOnlyList<ParameterDefinition>? parameters)
    {
        return [.. parameters?.Where(parameter => parameter.Secret).Select(parameter => parameter.Name) ?? []];
    }
}
