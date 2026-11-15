using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace QoffeeAutomat.Contracts.Config;

/// <summary>
/// Reads and writes <see cref="CommandAction"/> polymorphically on <c>type</c>. An unknown or
/// missing type becomes an <see cref="UnknownAction"/> so that the validator can report it.
/// Only the metadata of <see cref="ConfigJsonContext"/> is used.
/// </summary>
public sealed class CommandActionConverter : JsonConverter<CommandAction>
{
    /// <inheritdoc />
    public override CommandAction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An action must be a JSON object.");
        }

        string type = root.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()!
            : string.Empty;
        ConfigJsonContext context = ConfigJsonContext.Default;

        CommandAction action = type switch
        {
            ActionTypes.PowerShell => Parse(root, context.PowerShellAction),
            ActionTypes.Process => Parse(root, context.ProcessAction),
            ActionTypes.Builtin => Parse(root, context.BuiltinAction),
            ActionTypes.Sequence => Parse(root, context.SequenceAction),
            _ => new UnknownAction(type, root.Clone()),
        };

        return action;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, CommandAction value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        ConfigJsonContext context = ConfigJsonContext.Default;

        switch (value)
        {
            case PowerShellAction powerShell:
                JsonSerializer.Serialize(writer, powerShell, context.PowerShellAction);
                break;
            case ProcessAction process:
                JsonSerializer.Serialize(writer, process, context.ProcessAction);
                break;
            case BuiltinAction builtin:
                JsonSerializer.Serialize(writer, builtin, context.BuiltinAction);
                break;
            case SequenceAction sequence:
                JsonSerializer.Serialize(writer, sequence, context.SequenceAction);
                break;
            case UnknownAction unknown:
                unknown.Raw.WriteTo(writer);
                break;
            default:
                throw new NotSupportedException($"The action type {value.GetType().Name} is not supported.");
        }
    }

    private static T Parse<T>(JsonElement root, JsonTypeInfo<T> typeInfo)
        where T : CommandAction
    {
        return root.Deserialize(typeInfo) ?? throw new JsonException("The action is not valid.");
    }
}

/// <summary>
/// Reads and writes <see cref="SensorSource"/> polymorphically on <c>type</c>. An unknown or
/// missing type becomes an <see cref="UnknownSensorSource"/> so that the validator can report it.
/// Only the metadata of <see cref="ConfigJsonContext"/> is used.
/// </summary>
public sealed class SensorSourceConverter : JsonConverter<SensorSource>
{
    /// <inheritdoc />
    public override SensorSource? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A sensor source must be a JSON object.");
        }

        string type = root.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()!
            : string.Empty;
        ConfigJsonContext context = ConfigJsonContext.Default;

        SensorSource source = type switch
        {
            SensorSourceTypes.PowerShell => Parse(root, context.PowerShellSensorSource),
            SensorSourceTypes.ProcessRunning => Parse(root, context.ProcessRunningSensorSource),
            SensorSourceTypes.PerfCounter => Parse(root, context.PerfCounterSensorSource),
            SensorSourceTypes.CimQuery => Parse(root, context.CimQuerySensorSource),
            SensorSourceTypes.ServiceState => Parse(root, context.ServiceStateSensorSource),
            SensorSourceTypes.State => Parse(root, context.StateSensorSource),
            _ => new UnknownSensorSource(type, root.Clone()),
        };

        return source;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SensorSource value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        ConfigJsonContext context = ConfigJsonContext.Default;

        switch (value)
        {
            case PowerShellSensorSource powerShell:
                JsonSerializer.Serialize(writer, powerShell, context.PowerShellSensorSource);
                break;
            case ProcessRunningSensorSource process:
                JsonSerializer.Serialize(writer, process, context.ProcessRunningSensorSource);
                break;
            case PerfCounterSensorSource counter:
                JsonSerializer.Serialize(writer, counter, context.PerfCounterSensorSource);
                break;
            case CimQuerySensorSource cim:
                JsonSerializer.Serialize(writer, cim, context.CimQuerySensorSource);
                break;
            case ServiceStateSensorSource service:
                JsonSerializer.Serialize(writer, service, context.ServiceStateSensorSource);
                break;
            case StateSensorSource state:
                JsonSerializer.Serialize(writer, state, context.StateSensorSource);
                break;
            case UnknownSensorSource unknown:
                unknown.Raw.WriteTo(writer);
                break;
            default:
                throw new NotSupportedException($"The sensor source type {value.GetType().Name} is not supported.");
        }
    }

    private static T Parse<T>(JsonElement root, JsonTypeInfo<T> typeInfo)
        where T : SensorSource
    {
        return root.Deserialize(typeInfo) ?? throw new JsonException("The sensor source is not valid.");
    }
}
