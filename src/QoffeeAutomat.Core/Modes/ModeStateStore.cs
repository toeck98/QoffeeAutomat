using System.Buffers;
using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Core.IO;

namespace QoffeeAutomat.Core.Modes;

/// <summary>What the agent remembers about one mode group.</summary>
/// <param name="Current">The id of the current mode; null while the state is unknown.</param>
/// <param name="ChangedAt">When <paramref name="Current"/> was set.</param>
/// <param name="BootId">The boot id of the operating system start in which the entry was last reconciled with <c>onBoot</c> (PLAN.md S06).</param>
public sealed record ModeStateEntry(string? Current, DateTimeOffset? ChangedAt, string? BootId);

/// <summary>
/// The current mode of every group, kept in memory and in <c>data/mode-state.json</c> (owner-only, written atomically) so that it
/// survives an agent restart and an operating-system restart (PLAN.md S06, T717). The file holds, per group, the current mode, the
/// time it was set and the boot id in which the entry was last reconciled; the engine compares that with the boot id of the running
/// system to tell an operating-system start from an agent restart.
/// </summary>
public sealed class ModeStateStore : IDisposable
{
    private const int FileSchemaVersion = 1;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string? _path;
    private readonly IFileOperations _files;
    private readonly Dictionary<string, ModeStateEntry> _entries = new(StringComparer.Ordinal);
    private bool _loaded;

    /// <summary>Creates the store; the file is read on first use.</summary>
    /// <param name="persistencePath">The file; null keeps everything in memory only.</param>
    /// <param name="files">The file system operations; null uses the real ones.</param>
    public ModeStateStore(string? persistencePath = null, IFileOperations? files = null)
    {
        _path = persistencePath;
        _files = files ?? FileOperations.Instance;
    }

    /// <summary>Releases the lock that serialises writes to the file.</summary>
    public void Dispose()
    {
        _writeGate.Dispose();
    }

    /// <summary>Reads the entry of a group.</summary>
    /// <param name="groupId">The group id.</param>
    /// <returns>The entry, or null when nothing is remembered about the group.</returns>
    public ModeStateEntry? Get(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        lock (_gate)
        {
            EnsureLoaded();
            return _entries.GetValueOrDefault(groupId);
        }
    }

    /// <summary>Stores the entry of a group in memory and writes the file.</summary>
    /// <param name="groupId">The group id.</param>
    /// <param name="entry">The new entry.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written; the memory is already updated, so a failing write is only reported.</returns>
    public Task SetAsync(string groupId, ModeStateEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            EnsureLoaded();
            _entries[groupId] = entry;
        }

        return PersistAsync(cancellationToken);
    }

    /// <summary>Forgets a group (it was deleted) and writes the file.</summary>
    /// <param name="groupId">The group id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public Task RemoveAsync(string groupId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupId);

        lock (_gate)
        {
            EnsureLoaded();
            if (!_entries.Remove(groupId))
            {
                return Task.CompletedTask;
            }
        }

        return PersistAsync(cancellationToken);
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(_path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("groups", out JsonElement groups)
                || groups.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (JsonProperty property in groups.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    _entries[property.Name] = ReadEntry(property.Value);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Nothing was kept that can be trusted; every group starts without a state.
        }
    }

    private static ModeStateEntry ReadEntry(JsonElement element)
    {
        string? current = element.TryGetProperty("current", out JsonElement currentElement) && currentElement.ValueKind == JsonValueKind.String
            ? currentElement.GetString()
            : null;
        string? bootId = element.TryGetProperty("bootId", out JsonElement bootElement) && bootElement.ValueKind == JsonValueKind.String
            ? bootElement.GetString()
            : null;
        DateTimeOffset? changedAt = element.TryGetProperty("changedAt", out JsonElement changedElement)
            && changedElement.ValueKind == JsonValueKind.String
            && changedElement.TryGetDateTimeOffset(out DateTimeOffset at)
                ? at
                : null;
        return new ModeStateEntry(current, changedAt, bootId);
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            KeyValuePair<string, ModeStateEntry>[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _entries.OrderBy(pair => pair.Key, StringComparer.Ordinal)];
            }

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", FileSchemaVersion);
                writer.WriteStartObject("groups");
                foreach ((string id, ModeStateEntry entry) in snapshot)
                {
                    writer.WriteStartObject(id);
                    if (entry.Current is not null)
                    {
                        writer.WriteString("current", entry.Current);
                    }

                    if (entry.ChangedAt is { } changedAt)
                    {
                        writer.WriteString("changedAt", changedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                    }

                    if (entry.BootId is not null)
                    {
                        writer.WriteString("bootId", entry.BootId);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            await AtomicFile.WriteAsync(_files, _path, buffer.WrittenMemory, ownerOnly: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
