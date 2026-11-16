using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Config;

/// <summary>The outcome of reading one document file.</summary>
internal sealed record ParseOutcome(
    object? Document,
    string? ProblemCode,
    string? ProblemMessage,
    IReadOnlyList<ValidationError> Errors,
    byte[]? MigratedBytes,
    int FromVersion)
{
    public bool IsValid => Document is not null;

    public static ParseOutcome Fail(string code, string message) =>
        new(null, code, message, [], null, 0);

    public static ParseOutcome Invalid(IReadOnlyList<ValidationError> errors) =>
        new(null, ConfigProblemCodes.InvalidDocument, "The document is not valid: " + DescribeFirst(errors), errors, null, 0);

    private static string DescribeFirst(IReadOnlyList<ValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return "unknown error.";
        }

        ValidationError first = errors[0];
        string more = errors.Count > 1 ? $" (and {errors.Count - 1} more)" : string.Empty;
        return $"{first.Path}: {first.Message}{more}";
    }
}

/// <summary>Describes how one kind of document is stored, read, validated and stamped.</summary>
internal abstract class KindDescriptor
{
    private const string JsonExtension = ".json";

    protected KindDescriptor(ConfigKind kind, Type documentType, string folder, string? singletonId)
    {
        Kind = kind;
        DocumentType = documentType;
        Folder = folder;
        SingletonId = singletonId;
    }

    public ConfigKind Kind { get; }

    public Type DocumentType { get; }

    /// <summary>The directory under config/ for many-document kinds; also the name of the history folder.</summary>
    public string Folder { get; }

    /// <summary>The fixed id of a document that exists once (settings, metric overrides); null otherwise.</summary>
    public string? SingletonId { get; }

    public bool IsSingleton => SingletonId is not null;

    /// <summary>The file name of a singleton document.</summary>
    public string SingletonFileName => (SingletonId ?? Folder) + JsonExtension;

    public abstract string? IdOf(object document);

    public abstract ParseOutcome Parse(byte[] bytes, IReadOnlyList<IDocumentMigration> migrations);
}

/// <summary>The typed part of <see cref="KindDescriptor"/>.</summary>
internal sealed class KindDescriptor<T> : KindDescriptor
    where T : class, IConfigDocument
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly Func<T, string?> _idOf;
    private readonly Func<T, int, DateTimeOffset, T> _stamp;
    private readonly Func<T, Func<string, CommandDocument?>?, ValidationResult> _validate;

    public KindDescriptor(
        ConfigKind kind,
        string folder,
        string? singletonId,
        JsonTypeInfo<T> typeInfo,
        Func<T, string?> idOf,
        Func<T, int, DateTimeOffset, T> stamp,
        Func<T, Func<string, CommandDocument?>?, ValidationResult> validate)
        : base(kind, typeof(T), folder, singletonId)
    {
        _typeInfo = typeInfo;
        _idOf = idOf;
        _stamp = stamp;
        _validate = validate;
    }

    public override string? IdOf(object document) => _idOf((T)document);

    public string? IdOf(T document) => _idOf(document);

    /// <summary>Sets the header members that the store owns.</summary>
    public T Stamp(T document, int revision, DateTimeOffset updatedAt) => _stamp(document, revision, updatedAt);

    public ValidationResult Validate(T document, Func<string, CommandDocument?>? lookup) => _validate(document, lookup);

    /// <summary>The bytes of the file: indented JSON plus a final line feed.</summary>
    public byte[] Serialize(T document)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, _typeInfo);
        byte[] bytes = new byte[json.Length + 1];
        json.CopyTo(bytes, 0);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    public override ParseOutcome Parse(byte[] bytes, IReadOnlyList<IDocumentMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(migrations);

        JsonObject root;
        try
        {
            ReadOnlySpan<byte> span = bytes;
            if (span.StartsWith(new ReadOnlySpan<byte>(Utf8Bom)))
            {
                span = span[Utf8Bom.Length..];
            }

            if (JsonNode.Parse(span) is not JsonObject parsed)
            {
                return ParseOutcome.Fail(ConfigProblemCodes.InvalidJson, "The file must contain a JSON object.");
            }

            root = parsed;
        }
        catch (JsonException exception)
        {
            return ParseOutcome.Fail(ConfigProblemCodes.InvalidJson, exception.Message);
        }

        int fromVersion = 0;
        bool migrated = false;
        if (root["schemaVersion"] is JsonValue versionValue && versionValue.TryGetValue(out int version))
        {
            fromVersion = version;
            while (version >= 0 && version < DocumentSchema.CurrentVersion)
            {
                IDocumentMigration? step = FindMigration(migrations, version);
                if (step is null)
                {
                    return ParseOutcome.Fail(
                        ConfigProblemCodes.MigrationMissing,
                        $"No migration exists from schemaVersion {version} to {version + 1}.");
                }

                try
                {
                    root = step.Migrate(root);
                }
#pragma warning disable CA1031 // A faulty migration must skip the file, not stop the agent from starting.
                catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    return ParseOutcome.Fail(ConfigProblemCodes.MigrationFailed, $"The migration from schemaVersion {version} failed: {exception.Message}");
                }

                version++;
                root["schemaVersion"] = version;
                migrated = true;
            }
        }

        T? document;
        try
        {
            document = JsonSerializer.Deserialize(root.ToJsonString(), _typeInfo);
        }
        catch (JsonException exception)
        {
            return ParseOutcome.Fail(ConfigProblemCodes.InvalidJson, exception.Message);
        }

        if (document is null)
        {
            return ParseOutcome.Fail(ConfigProblemCodes.InvalidJson, "The file does not contain a document.");
        }

        ValidationResult validation = _validate(document, null);
        if (!validation.IsValid)
        {
            return ParseOutcome.Invalid(validation.Errors);
        }

        return new ParseOutcome(document, null, null, [], migrated ? Serialize(document) : null, fromVersion);
    }

    private IDocumentMigration? FindMigration(IReadOnlyList<IDocumentMigration> migrations, int fromVersion)
    {
        foreach (IDocumentMigration migration in migrations)
        {
            if (migration.FromVersion == fromVersion && migration.Kind == Kind)
            {
                return migration;
            }
        }

        return null;
    }
}

/// <summary>The registry of the four document kinds.</summary>
internal static class KindDescriptors
{
    public static readonly KindDescriptor<CommandDocument> Command = new(
        ConfigKind.Command,
        "commands",
        null,
        ConfigJsonContext.Default.CommandDocument,
        document => document.Id,
        (document, revision, at) => document with
        {
            SchemaVersion = DocumentSchema.CurrentVersion,
            Revision = revision,
            UpdatedAt = at,
            Origin = DocumentOrigins.Local,
        },
        DocumentValidator.ValidateCommand);

    public static readonly KindDescriptor<SensorDocument> Sensor = new(
        ConfigKind.Sensor,
        "sensors",
        null,
        ConfigJsonContext.Default.SensorDocument,
        document => document.Id,
        (document, revision, at) => document with
        {
            SchemaVersion = DocumentSchema.CurrentVersion,
            Revision = revision,
            UpdatedAt = at,
            Origin = DocumentOrigins.Local,
        },
        (document, _) => DocumentValidator.ValidateSensor(document));

    public static readonly KindDescriptor<MetricOverridesDocument> MetricOverrides = new(
        ConfigKind.MetricOverrides,
        "metrics",
        ConfigIds.MetricOverrides,
        ConfigJsonContext.Default.MetricOverridesDocument,
        _ => ConfigIds.MetricOverrides,
        (document, revision, at) => document with
        {
            SchemaVersion = DocumentSchema.CurrentVersion,
            Revision = revision,
            UpdatedAt = at,
            Origin = DocumentOrigins.Local,
        },
        (document, _) => DocumentValidator.ValidateMetricOverrides(document));

    public static readonly KindDescriptor<AgentSettingsDocument> Settings = new(
        ConfigKind.Settings,
        "settings",
        ConfigIds.Settings,
        ConfigJsonContext.Default.AgentSettingsDocument,
        _ => ConfigIds.Settings,
        (document, revision, at) => document with
        {
            SchemaVersion = DocumentSchema.CurrentVersion,
            Revision = revision,
            UpdatedAt = at,
            Origin = DocumentOrigins.Local,
        },
        (document, _) => DocumentValidator.ValidateSettings(document));

    private static readonly KindDescriptor[] All = [Command, Sensor, MetricOverrides, Settings];

    /// <summary>The many-document and singleton kinds that are stored as JSON documents.</summary>
    public static IReadOnlyList<KindDescriptor> Documents => All;

    public static KindDescriptor<T> For<T>()
        where T : class, IConfigDocument
    {
        return (KindDescriptor<T>)For(typeof(T));
    }

    public static KindDescriptor For(Type documentType)
    {
        foreach (KindDescriptor descriptor in All)
        {
            if (descriptor.DocumentType == documentType)
            {
                return descriptor;
            }
        }

        throw new ArgumentException($"{documentType.Name} is not a configuration document type.", nameof(documentType));
    }

    public static KindDescriptor? For(ConfigKind kind)
    {
        foreach (KindDescriptor descriptor in All)
        {
            if (descriptor.Kind == kind)
            {
                return descriptor;
            }
        }

        return null;
    }
}
