using System.Buffers;
using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.IO;

namespace QoffeeAutomat.Core.Metrics.Custom;

/// <summary>The machine-readable reasons why a state value cannot be read or set (PLAN.md S06, T714).</summary>
public static class StateSensorErrors
{
    /// <summary>There is no sensor with the id.</summary>
    public const string UnknownSensor = "unknown_sensor";

    /// <summary>The sensor exists but is not of type <c>state</c>.</summary>
    public const string NotStateSensor = "not_state_sensor";

    /// <summary>The value does not fit the value type or the options of the sensor.</summary>
    public const string InvalidValue = "invalid_value";

    /// <summary>The value was set and published, but could not be written to disk.</summary>
    public const string PersistFailed = "persist_failed";
}

/// <summary>The value of a state sensor as it is published.</summary>
/// <param name="Value">A <see cref="double"/>, <see cref="bool"/>, <see cref="string"/> or <see cref="DateTimeOffset"/> (UTC); null when the sensor has no value (nothing was set and it has no initial value).</param>
/// <param name="UpdatedAt">When the value was set; null while the sensor shows its initial value or none.</param>
public sealed record StateReading(object? Value, DateTimeOffset? UpdatedAt);

/// <summary>The outcome of <see cref="StateSensorStore.SetAsync"/> and <see cref="StateSensorStore.SetTextAsync"/>.</summary>
public sealed class StateSetResult
{
    private StateSetResult(string sensorId, object? value, DateTimeOffset? updatedAt, string? errorCode, string? message)
    {
        SensorId = sensorId;
        Value = value;
        UpdatedAt = updatedAt;
        ErrorCode = errorCode;
        Message = message;
    }

    /// <summary>The sensor id that was addressed.</summary>
    public string SensorId { get; }

    /// <summary>True when the value was accepted and published.</summary>
    public bool IsSuccess => ErrorCode is null;

    /// <summary>The published value (rounded to the precision of the sensor); null on failure.</summary>
    public object? Value { get; }

    /// <summary>When the value was set; null on failure.</summary>
    public DateTimeOffset? UpdatedAt { get; }

    /// <summary>One of <see cref="StateSensorErrors"/>; null on success.</summary>
    public string? ErrorCode { get; }

    /// <summary>What is wrong, in words that a user can act on; null on success. It never contains values other than the rejected one.</summary>
    public string? Message { get; }

    internal static StateSetResult Success(string sensorId, object value, DateTimeOffset updatedAt) => new(sensorId, value, updatedAt, null, null);

    internal static StateSetResult Fail(string sensorId, string errorCode, string message) => new(sensorId, null, null, errorCode, message);
}

/// <summary>The outcome of <see cref="StateSensorStore.Get"/>.</summary>
public sealed class StateGetResult
{
    private StateGetResult(string sensorId, StateReading? reading, string? errorCode, string? message)
    {
        SensorId = sensorId;
        Reading = reading;
        ErrorCode = errorCode;
        Message = message;
    }

    /// <summary>The sensor id that was addressed.</summary>
    public string SensorId { get; }

    /// <summary>True when the sensor exists and is a state sensor (the value may still be absent).</summary>
    public bool IsSuccess => ErrorCode is null;

    /// <summary>The current value; null on failure.</summary>
    public StateReading? Reading { get; }

    /// <summary>One of <see cref="StateSensorErrors"/>; null on success.</summary>
    public string? ErrorCode { get; }

    /// <summary>What is wrong; null on success.</summary>
    public string? Message { get; }

    internal static StateGetResult Success(string sensorId, StateReading reading) => new(sensorId, reading, null, null);

    internal static StateGetResult Fail(string sensorId, string errorCode, string message) => new(sensorId, null, errorCode, message);
}

/// <summary>Raised after a state value was set.</summary>
public sealed class StateChangedEventArgs : EventArgs
{
    /// <summary>Creates the arguments.</summary>
    /// <param name="sensorId">The sensor document id.</param>
    public StateChangedEventArgs(string sensorId)
    {
        SensorId = sensorId;
        MetricId = StateSensorStore.MetricIdOf(sensorId);
    }

    /// <summary>The sensor document id.</summary>
    public string SensorId { get; }

    /// <summary>The metric id, <c>custom.&lt;id&gt;</c>; pass it to <see cref="MetricEngine.RequestSample"/> to publish the value.</summary>
    public string MetricId { get; }
}

/// <summary>
/// The values of the custom sensors of type <c>state</c> (PLAN.md S06, T714): metrics that nobody samples but that a
/// script, the UI or the API sets from outside, so that a Home Assistant control can show the real state of what a
/// script did. A value is validated against the <c>valueType</c> and the <c>options</c> of the sensor; it is held in
/// memory, and kept in a file when the sensor has <c>persist</c> set, so that it survives a restart of the agent and of the
/// PC. Without <c>persist</c> a value is gone after a restart and the sensor shows its <c>initialValue</c> again. A sensor
/// that has neither a value nor an initial value is unavailable.
/// <para>
/// The store does not publish anything itself: it raises <see cref="Changed"/>, and the host asks the metric engine for a
/// fresh sample of the metric, which makes the new value an event-driven metric update.
/// </para>
/// </summary>
public sealed class StateSensorStore : IDisposable
{
    /// <summary>The longest text value. Home Assistant refuses states longer than this.</summary>
    public const int MaxTextLength = 255;

    private const int FileSchemaVersion = 1;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly IConfigStore _config;
    private readonly TimeProvider _time;
    private readonly string? _path;
    private readonly IFileOperations _files;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _loaded;

    /// <summary>Creates the store; the file is read on first use.</summary>
    /// <param name="config">The configuration store, to find the sensor documents.</param>
    /// <param name="time">The clock for the time of a change.</param>
    /// <param name="persistencePath">The file that keeps the values of sensors with <c>persist</c>; null keeps everything in memory only.</param>
    /// <param name="files">The file system operations; null uses the real ones.</param>
    public StateSensorStore(IConfigStore config, TimeProvider time, string? persistencePath = null, IFileOperations? files = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);

        _config = config;
        _time = time;
        _path = persistencePath;
        _files = files ?? FileOperations.Instance;
    }

    /// <summary>Raised after a value was set, on the thread of the caller. Handlers return quickly and must not throw.</summary>
    public event EventHandler<StateChangedEventArgs>? Changed;

    /// <summary>Releases the lock that serialises writes to the file.</summary>
    public void Dispose()
    {
        _writeGate.Dispose();
    }

    /// <summary>The metric id of a sensor.</summary>
    /// <param name="sensorId">The sensor document id.</param>
    /// <returns><c>custom.&lt;id&gt;</c>.</returns>
    public static string MetricIdOf(string sensorId)
    {
        ArgumentNullException.ThrowIfNull(sensorId);
        return MetricCatalogBuilder.CustomPrefix + sensorId;
    }

    /// <summary>Converts text (what the command line gives) to a value of the sensor's type.</summary>
    /// <param name="sensor">The sensor document; its value type and options decide.</param>
    /// <param name="text">The text: a number in invariant notation, <c>true</c>/<c>false</c> (also 1, 0, yes, no, on, off), an option, a date and time or any text.</param>
    /// <param name="value">The value; null when the text does not fit.</param>
    /// <param name="error">Why it does not fit; null when it does.</param>
    /// <returns>True when the text is a valid value.</returns>
    public static bool TryParseText(SensorDocument sensor, string text, out object? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentNullException.ThrowIfNull(text);

        value = null;
        error = null;
        switch (sensor.ValueType)
        {
            case "number":
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
                {
                    value = number;
                    return true;
                }

                error = $"'{Quote(text)}' is not a number.";
                return false;
            case "bool":
                if (SensorOutputParser.TryParseBool(text.Trim(), out bool flag))
                {
                    value = flag;
                    return true;
                }

                error = $"'{Quote(text)}' is not true or false.";
                return false;
            case "string":
                if (text.Length > MaxTextLength)
                {
                    error = $"The text is longer than {MaxTextLength.ToString(CultureInfo.InvariantCulture)} characters.";
                    return false;
                }

                value = text;
                return true;
            case "timestamp":
                if (DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset moment))
                {
                    value = moment.ToUniversalTime();
                    return true;
                }

                error = $"'{Quote(text)}' is not a date and time.";
                return false;
            case "enum":
                return TryMatchOption(sensor, text.Trim(), out value, out error);
            default:
                error = $"The value type '{sensor.ValueType}' is not supported.";
                return false;
        }
    }

    /// <summary>Converts a JSON value (what the API gives) to a value of the sensor's type. A JSON string is read like command-line text, so <c>"42"</c> is accepted for a number.</summary>
    /// <param name="sensor">The sensor document.</param>
    /// <param name="json">The JSON value.</param>
    /// <param name="value">The value; null when it does not fit.</param>
    /// <param name="error">Why it does not fit; null when it does.</param>
    /// <returns>True when the value is valid.</returns>
    public static bool TryParseJson(SensorDocument sensor, JsonElement json, out object? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        value = null;
        error = null;
        switch (json.ValueKind)
        {
            case JsonValueKind.String:
                return TryParseText(sensor, json.GetString() ?? string.Empty, out value, out error);
            case JsonValueKind.Number when sensor.ValueType == "number":
                if (json.TryGetDouble(out double number) && double.IsFinite(number))
                {
                    value = number;
                    return true;
                }

                error = "The number is out of range.";
                return false;
            case JsonValueKind.True or JsonValueKind.False when sensor.ValueType == "bool":
                value = json.GetBoolean();
                return true;
            case JsonValueKind.Null or JsonValueKind.Undefined:
                error = "A value is required.";
                return false;
            default:
                error = $"A {JsonKindName(json.ValueKind)} does not fit a sensor of value type '{sensor.ValueType}'.";
                return false;
        }
    }

    /// <summary>Reads the value that a sensor shows now: the value that was set, otherwise its initial value.</summary>
    /// <param name="sensor">The sensor document; its source must be a <see cref="StateSensorSource"/>.</param>
    /// <param name="includeSetValue">False ignores a value that was set and returns the initial value (the dry run of a draft).</param>
    /// <returns>The reading.</returns>
    public StateReading Resolve(SensorDocument sensor, bool includeSetValue = true)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        if (sensor.Source is not StateSensorSource source)
        {
            return new StateReading(null, null);
        }

        if (includeSetValue)
        {
            Entry? entry;
            lock (_gate)
            {
                EnsureLoaded();
                _entries.TryGetValue(sensor.Id, out entry);
            }

            // A value that was kept on disk belongs to the sensor only while the sensor still asks for persistence, and a value
            // that no longer fits the sensor (its type or options were edited) is dropped in favour of the initial value.
            if (entry is not null
                && (!entry.Persisted || source.Persist)
                && TryParseJson(sensor, entry.Json, out object? stored, out _))
            {
                return new StateReading(stored, entry.UpdatedAt);
            }
        }

        if (source.InitialValue is { } initial
            && initial.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && TryParseJson(sensor, initial, out object? initialValue, out _))
        {
            return new StateReading(initialValue, null);
        }

        return new StateReading(null, null);
    }

    /// <summary>Reads the value of a sensor by id.</summary>
    /// <param name="sensorId">The sensor document id.</param>
    /// <returns>The value, or the reason that the sensor cannot be read.</returns>
    public StateGetResult Get(string sensorId)
    {
        ArgumentNullException.ThrowIfNull(sensorId);

        SensorDocument? sensor = _config.Effective.GetDocument<SensorDocument>(sensorId);
        if (sensor is null)
        {
            return StateGetResult.Fail(sensorId, StateSensorErrors.UnknownSensor, UnknownMessage(sensorId));
        }

        return sensor.Source is StateSensorSource
            ? StateGetResult.Success(sensorId, Normalize(sensor, Resolve(sensor)))
            : StateGetResult.Fail(sensorId, StateSensorErrors.NotStateSensor, NotStateMessage(sensor));
    }

    /// <summary>Sets a value given as text (the CLI verb <c>state set</c>).</summary>
    /// <param name="sensorId">The sensor document id.</param>
    /// <param name="text">The text, see <see cref="TryParseText"/>.</param>
    /// <param name="cancellationToken">Cancels the write to disk.</param>
    /// <returns>The outcome.</returns>
    public Task<StateSetResult> SetTextAsync(string sensorId, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        using JsonDocument document = JsonDocument.Parse(WriteString(text));
        return SetAsync(sensorId, document.RootElement.Clone(), cancellationToken);
    }

    /// <summary>Sets a value given as JSON (<c>PUT /sensors/{id}/value</c>).</summary>
    /// <param name="sensorId">The sensor document id.</param>
    /// <param name="value">The JSON value.</param>
    /// <param name="cancellationToken">Cancels the write to disk.</param>
    /// <returns>The outcome; on success the value is already published to the subscribers of the metric once the host handled <see cref="Changed"/>.</returns>
    public async Task<StateSetResult> SetAsync(string sensorId, JsonElement value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sensorId);

        SensorDocument? sensor = _config.Effective.GetDocument<SensorDocument>(sensorId);
        if (sensor is null)
        {
            return StateSetResult.Fail(sensorId, StateSensorErrors.UnknownSensor, UnknownMessage(sensorId));
        }

        if (sensor.Source is not StateSensorSource source)
        {
            return StateSetResult.Fail(sensorId, StateSensorErrors.NotStateSensor, NotStateMessage(sensor));
        }

        if (!TryParseJson(sensor, value, out object? parsed, out string? error))
        {
            return StateSetResult.Fail(sensorId, StateSensorErrors.InvalidValue, $"Sensor '{sensorId}': {error}");
        }

        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            EnsureLoaded();
            _entries[sensorId] = new Entry(Canonical(parsed!), now, source.Persist);
        }

        Changed?.Invoke(this, new StateChangedEventArgs(sensorId));

        object? published = Normalize(sensor, new StateReading(parsed, now)).Value;
        if (source.Persist && _path is not null)
        {
            try
            {
                await PersistAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return StateSetResult.Fail(
                    sensorId,
                    StateSensorErrors.PersistFailed,
                    $"The value of sensor '{sensorId}' was set, but it could not be saved and will be lost when the agent restarts: {exception.Message}");
            }
        }

        return StateSetResult.Success(sensorId, published!, now);
    }

    private static StateReading Normalize(SensorDocument sensor, StateReading reading)
    {
        return reading.Value is null ? reading : reading with { Value = MetricValues.Normalize(reading.Value, sensor.Precision) };
    }

    private static string UnknownMessage(string sensorId) => $"There is no sensor with the id '{sensorId}'. Create it as a sensor of type 'state' first.";

    private static string NotStateMessage(SensorDocument sensor) =>
        $"The sensor '{sensor.Id}' is of type '{sensor.Source.Type}'; only sensors of type 'state' can be set.";

    private static bool TryMatchOption(SensorDocument sensor, string text, out object? value, out string? error)
    {
        value = null;
        if (sensor.Options is null || sensor.Options.Count == 0)
        {
            error = "The sensor declares no options for its enum value.";
            return false;
        }

        string? match = sensor.Options.FirstOrDefault(option => string.Equals(option, text, StringComparison.Ordinal))
            ?? sensor.Options.FirstOrDefault(option => string.Equals(option, text, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            error = $"'{Quote(text)}' is not one of the options ({string.Join(", ", sensor.Options)}).";
            return false;
        }

        value = match;
        error = null;
        return true;
    }

    private static string Quote(string text) => text.Length <= 80 ? text : text[..80] + "...";

    private static string JsonKindName(JsonValueKind kind)
    {
        return kind switch
        {
            JsonValueKind.Object => "JSON object",
            JsonValueKind.Array => "JSON array",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "value of this kind",
        };
    }

    private static ReadOnlyMemory<byte> WriteString(string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(text);
        }

        return buffer.WrittenMemory;
    }

    /// <summary>The JSON form that is stored and written to disk; a timestamp becomes an ISO 8601 string in UTC.</summary>
    private static JsonElement Canonical(object value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            switch (value)
            {
                case double number:
                    writer.WriteNumberValue(number);
                    break;
                case bool flag:
                    writer.WriteBooleanValue(flag);
                    break;
                case DateTimeOffset moment:
                    writer.WriteStringValue(moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                    break;
                default:
                    writer.WriteStringValue((string)value);
                    break;
            }
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Reads the file once. A missing or damaged file means that no value was kept; the sensors show their initial values. The caller holds the lock.</summary>
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
                || !document.RootElement.TryGetProperty("values", out JsonElement values)
                || values.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (JsonProperty property in values.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("value", out JsonElement json)
                    && property.Value.TryGetProperty("updatedAt", out JsonElement updated)
                    && updated.ValueKind == JsonValueKind.String
                    && updated.TryGetDateTimeOffset(out DateTimeOffset at))
                {
                    _entries[property.Name] = new Entry(json.Clone(), at, Persisted: true);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Nothing was kept that can be trusted; the sensors start from their initial values.
        }
    }

    /// <summary>Writes the values of all sensors that still have <c>persist</c> set and still exist.</summary>
    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var kept = new List<(string Id, Entry Entry)>();
            lock (_gate)
            {
                foreach ((string id, Entry entry) in _entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (entry.Persisted
                        && _config.Effective.GetDocument<SensorDocument>(id) is { Source: StateSensorSource { Persist: true } })
                    {
                        kept.Add((id, entry));
                    }
                }
            }

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", FileSchemaVersion);
                writer.WriteStartObject("values");
                foreach ((string id, Entry entry) in kept)
                {
                    writer.WriteStartObject(id);
                    writer.WritePropertyName("value");
                    entry.Json.WriteTo(writer);
                    writer.WriteString("updatedAt", entry.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            await AtomicFile.WriteAsync(_files, _path!, buffer.WrittenMemory, ownerOnly: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private sealed record Entry(JsonElement Json, DateTimeOffset UpdatedAt, bool Persisted);
}
