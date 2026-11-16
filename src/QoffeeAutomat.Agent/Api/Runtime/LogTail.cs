using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Agent.Api.Runtime;

/// <summary>
/// Reads the end of the rolling log files that <c>RollingFileLoggerProvider</c> writes (one JSON object per line,
/// <c>agent-yyyyMMdd.log</c>, and <c>agent-yyyyMMdd.N.log</c> for the files that were rolled because they were full).
/// The files are read while the agent may be writing to them, so they are opened with sharing.
/// </summary>
public static class LogTail
{
    private const string FilePrefix = "agent-";
    private const string FileExtension = ".log";

    /// <summary>Reads the newest entries.</summary>
    /// <param name="directory">The log directory; a missing directory yields no entries.</param>
    /// <param name="minimum">The lowest level that is returned.</param>
    /// <param name="limit">The most entries to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The newest <paramref name="limit"/> entries at or above the level, oldest first.</returns>
    public static async Task<IReadOnlyList<LogEntry>> ReadAsync(string directory, LogLevel minimum, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var newestFirst = new List<LogEntry>();
        foreach (string file in LogFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<LogEntry> entries = await ReadFileAsync(file, minimum, cancellationToken).ConfigureAwait(false);
            int missing = limit - newestFirst.Count;
            for (int index = entries.Count - 1; index >= 0 && missing > 0; index--, missing--)
            {
                newestFirst.Add(entries[index]);
            }

            if (newestFirst.Count >= limit)
            {
                break;
            }
        }

        newestFirst.Reverse();
        return newestFirst;
    }

    /// <summary>
    /// Reads the raw lines of the newest log files, newest last, for the diagnostics bundle.
    /// </summary>
    /// <param name="directory">The log directory.</param>
    /// <param name="maxLines">The most lines to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The lines, oldest first.</returns>
    public static async Task<IReadOnlyList<string>> ReadLinesAsync(string directory, int maxLines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLines, 1);

        var newestFirst = new List<string>();
        foreach (string file in LogFiles(directory))
        {
            List<string> lines = await ReadAllLinesSharedAsync(file, cancellationToken).ConfigureAwait(false);
            int missing = maxLines - newestFirst.Count;
            for (int index = lines.Count - 1; index >= 0 && missing > 0; index--, missing--)
            {
                newestFirst.Add(lines[index]);
            }

            if (newestFirst.Count >= maxLines)
            {
                break;
            }
        }

        newestFirst.Reverse();
        return newestFirst;
    }

    /// <summary>The log files, newest first: by day, and within a day the live file before the rolled ones, the most recently rolled first.</summary>
    private static List<string> LogFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(directory, FilePrefix + "*" + FileExtension)
                .Select(path => (Full: path, Key: SortKey(Path.GetFileName(path))))
                .Where(file => file.Key is not null)
                .OrderByDescending(file => file.Key.GetValueOrDefault().Day, StringComparer.Ordinal)
                .ThenByDescending(file => file.Key.GetValueOrDefault().Rank)
                .Select(file => file.Full),
        ];
    }

    private static (string Day, int Rank)? SortKey(string fileName)
    {
        string stem = fileName[FilePrefix.Length..^FileExtension.Length];
        int dot = stem.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return (stem, int.MaxValue);
        }

        return int.TryParse(stem[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int rolled)
            ? (stem[..dot], rolled)
            : null;
    }

    private static async Task<List<string>> ReadAllLinesSharedAsync(string path, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read (deleted by the retention rule just now) contributes nothing.
        }

        return lines;
    }

    private static async Task<List<LogEntry>> ReadFileAsync(string path, LogLevel minimum, CancellationToken cancellationToken)
    {
        var entries = new List<LogEntry>();
        foreach (string line in await ReadAllLinesSharedAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (TryParse(line, out LogEntry? entry) && Enum.TryParse(entry.Level, ignoreCase: true, out LogLevel level) && level >= minimum)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static bool TryParse(string line, [NotNullWhen(true)] out LogEntry? entry)
    {
        entry = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ts", out JsonElement ts)
                || !DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset timestamp))
            {
                return false;
            }

            entry = new LogEntry(
                timestamp,
                Text(root, "level") ?? "Information",
                Text(root, "category") ?? string.Empty,
                Text(root, "message") ?? string.Empty,
                Text(root, "exception"));
            return true;
        }
        catch (JsonException)
        {
            // A half-written line at the end of the file.
            return false;
        }
    }

    private static string? Text(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
