using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Config.HubLayer;

/// <summary>A hub document that passed the validators of the agent, ready to be stored and served.</summary>
/// <param name="Kind">The kind in the wire vocabulary of <c>config_sync</c> (<see cref="DocumentKinds"/>).</param>
/// <param name="Id">The document id.</param>
/// <param name="Hash">The hash from the signed item list.</param>
/// <param name="Bytes">The bytes that are stored: the canonical JSON of the body, for scripts the UTF-8 text.</param>
/// <param name="Document">The typed document with <c>origin</c> <c>hub</c>; null for scripts and variables.</param>
/// <param name="Script">The script text; null for every other kind.</param>
/// <param name="Variables">The name to value pairs of a variables document; null for every other kind.</param>
internal sealed record HubParsedDocument(
    string Kind,
    string Id,
    string Hash,
    byte[] Bytes,
    object? Document,
    string? Script,
    IReadOnlyDictionary<string, string>? Variables);

/// <summary>What reading one hub document came to.</summary>
/// <param name="Document">The document; null unless <paramref name="State"/> is <see cref="ConfigItemStates.Applied"/>.</param>
/// <param name="State">One of <see cref="ConfigItemStates"/>.</param>
/// <param name="Errors">Why the document is invalid or unsupported, as <c>path: message</c> lines.</param>
internal sealed record HubReadOutcome(HubParsedDocument? Document, string State, IReadOnlyList<string> Errors)
{
    public static HubReadOutcome Invalid(params string[] errors) => new(null, ConfigItemStates.Invalid, errors);
}

/// <summary>
/// Turns the body of a hub document into a validated document with the validators the agent uses for its own files (PLAN.md S20, T908): a command is
/// validated like a command that somebody saves locally, and a document that the platform cannot run is skipped, not rejected. The agent never trusts
/// the hub to have validated.
/// </summary>
internal static class HubDocumentReader
{
    /// <summary>The longest script, as for local scripts.</summary>
    public const int MaxScriptBytes = 1024 * 1024;

    /// <summary>The most variables in a variables document (the limit of the hub).</summary>
    public const int MaxVariables = 256;

    /// <summary>The longest value of a variable, in characters.</summary>
    public const int MaxVariableValueLength = 4096;

    private static readonly Regex VariableNamePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]{0,63}\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The document kind of the config store that a wire kind is stored as; null for scripts, variables and unknown kinds.</summary>
    public static ConfigKind? ConfigKindOf(string kind)
    {
        return kind switch
        {
            DocumentKinds.Command => ConfigKind.Command,
            DocumentKinds.Sensor => ConfigKind.Sensor,
            DocumentKinds.Mode => ConfigKind.Mode,
            DocumentKinds.MetricOverrides => ConfigKind.MetricOverrides,
            _ => null,
        };
    }

    /// <summary>Reads a document from stored bytes (the layer's own files); same rules as <see cref="Read"/>.</summary>
    public static HubReadOutcome ReadStored(
        string kind,
        string id,
        string hash,
        byte[] bytes,
        IReadOnlyList<IDocumentMigration> migrations,
        CommandPlatformSupport? support)
    {
        if (string.Equals(kind, DocumentKinds.Script, StringComparison.Ordinal))
        {
            try
            {
                return ReadScriptText(id, hash, StrictUtf8.GetString(bytes));
            }
            catch (DecoderFallbackException)
            {
                return HubReadOutcome.Invalid("The stored script is not valid UTF-8 text.");
            }
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes);
            return Read(kind, id, hash, json.RootElement.Clone(), migrations, support);
        }
        catch (JsonException exception)
        {
            return HubReadOutcome.Invalid("The stored document is not valid JSON: " + exception.Message);
        }
    }

    /// <summary>Validates one document of a generation.</summary>
    /// <param name="kind">The wire kind.</param>
    /// <param name="id">The id from the item list.</param>
    /// <param name="hash">The hash from the item list.</param>
    /// <param name="body">The body; a JSON object, for scripts a JSON string.</param>
    /// <param name="migrations">Forward migrations of older schema versions.</param>
    /// <param name="support">What the platform can run; null supports everything.</param>
    /// <returns>The outcome.</returns>
    public static HubReadOutcome Read(
        string kind,
        string id,
        string hash,
        JsonElement body,
        IReadOnlyList<IDocumentMigration> migrations,
        CommandPlatformSupport? support)
    {
        return kind switch
        {
            DocumentKinds.Script => ReadScript(id, hash, body),
            DocumentKinds.Variables => ReadVariables(id, hash, body),
            DocumentKinds.Command or DocumentKinds.Sensor or DocumentKinds.Mode or DocumentKinds.MetricOverrides
                => ReadDocument(kind, id, hash, body, migrations, support),
            _ => HubReadOutcome.Invalid("/kind: the kind '" + kind + "' is not known."),
        };
    }

    private static HubReadOutcome ReadDocument(
        string kind,
        string id,
        string hash,
        JsonElement body,
        IReadOnlyList<IDocumentMigration> migrations,
        CommandPlatformSupport? support)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return HubReadOutcome.Invalid("A " + kind + " document must be a JSON object.");
        }

        ConfigKind configKind = ConfigKindOf(kind)!.Value;
        if (configKind == ConfigKind.MetricOverrides)
        {
            if (!string.Equals(id, DocumentKinds.MetricOverrides, StringComparison.Ordinal))
            {
                return HubReadOutcome.Invalid("/id: the metric overrides document has the id 'metric_overrides'.");
            }
        }
        else if (!ScriptNames.IsValidDocumentId(id))
        {
            return HubReadOutcome.Invalid("/id: the id '" + id + "' is not a valid document id.");
        }

        byte[] bytes = CanonicalJson.SerializeToUtf8(body);
        KindDescriptor descriptor = KindDescriptors.For(configKind)!;
        ParseOutcome outcome = descriptor.Parse(bytes, migrations);
        if (!outcome.IsValid)
        {
            return new HubReadOutcome(null, ConfigItemStates.Invalid, Describe(outcome));
        }

        object document = outcome.Document!;
        if (configKind != ConfigKind.MetricOverrides
            && !string.Equals(descriptor.IdOf(document), id, StringComparison.Ordinal))
        {
            return HubReadOutcome.Invalid("/id: the id inside the document differs from the id in the item list ('" + id + "').");
        }

        IReadOnlyList<ValidationError> unsupported = CheckPlatform(document, support);
        if (unsupported.Count > 0)
        {
            return new HubReadOutcome(null, ConfigItemStates.SkippedUnsupported, [.. unsupported.Select(Format)]);
        }

        object stamped = document switch
        {
            CommandDocument command => command with { Origin = DocumentOrigins.Hub },
            SensorDocument sensor => sensor with { Origin = DocumentOrigins.Hub },
            ModeGroupDocument mode => mode with { Origin = DocumentOrigins.Hub },
            MetricOverridesDocument overrides => overrides with { Origin = DocumentOrigins.Hub },
            _ => document,
        };
        return new HubReadOutcome(new HubParsedDocument(kind, id, hash, bytes, stamped, null, null), ConfigItemStates.Applied, []);
    }

    private static HubReadOutcome ReadScript(string id, string hash, JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.String)
        {
            return HubReadOutcome.Invalid("The body of a script must be a JSON string.");
        }

        return ReadScriptText(id, hash, body.GetString()!);
    }

    private static HubReadOutcome ReadScriptText(string id, string hash, string text)
    {
        IReadOnlyList<ValidationError> nameErrors = ScriptNames.Validate(id, "/id");
        if (nameErrors.Count > 0)
        {
            return new HubReadOutcome(null, ConfigItemStates.Invalid, [.. nameErrors.Select(Format)]);
        }

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            return HubReadOutcome.Invalid("The script contains characters that cannot be stored as UTF-8.");
        }

        if (bytes.Length > MaxScriptBytes)
        {
            return HubReadOutcome.Invalid("A script must not be larger than 1 MiB.");
        }

        return new HubReadOutcome(new HubParsedDocument(DocumentKinds.Script, id, hash, bytes, null, text, null), ConfigItemStates.Applied, []);
    }

    private static HubReadOutcome ReadVariables(string id, string hash, JsonElement body)
    {
        if (!ScriptNames.IsValidDocumentId(id))
        {
            return HubReadOutcome.Invalid("/id: the id '" + id + "' is not a valid document id.");
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            return HubReadOutcome.Invalid("A variables document must be a JSON object of names and string values.");
        }

        var errors = new List<string>();
        var variables = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var upper = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in body.EnumerateObject())
        {
            string path = "/" + property.Name;
            if (!VariableNamePattern.IsMatch(property.Name))
            {
                errors.Add(path + ": a variable name must match ^[A-Za-z][A-Za-z0-9_]{0,63}$.");
            }
            else if (property.Value.ValueKind != JsonValueKind.String)
            {
                errors.Add(path + ": the value of a variable must be a string.");
            }
            else if (property.Value.GetString()!.Length > MaxVariableValueLength)
            {
                errors.Add(path + ": the value of a variable must not be longer than 4096 characters.");
            }
            else if (!upper.Add(property.Name.ToUpperInvariant()))
            {
                errors.Add(path + ": the name collides with another variable that differs only in case.");
            }
            else
            {
                variables[property.Name] = property.Value.GetString()!;
            }
        }

        if (variables.Count > MaxVariables)
        {
            errors.Add("A variables document has at most 256 variables.");
        }

        return errors.Count > 0
            ? new HubReadOutcome(null, ConfigItemStates.Invalid, errors)
            : new HubReadOutcome(
                new HubParsedDocument(DocumentKinds.Variables, id, hash, CanonicalJson.SerializeToUtf8(body), null, null, variables),
                ConfigItemStates.Applied,
                []);
    }

    private static IReadOnlyList<ValidationError> CheckPlatform(object document, CommandPlatformSupport? support)
    {
        if (support is null)
        {
            return [];
        }

        return document switch
        {
            CommandDocument command => support.Check(command),
            SensorDocument sensor => CheckSensor(sensor, support.Os),
            _ => [],
        };
    }

    private static IReadOnlyList<ValidationError> CheckSensor(SensorDocument sensor, string? os)
    {
        SensorSource? source = sensor.Source;
        if (source is null)
        {
            return [];
        }

        string type = source.Type;

        bool supported = type switch
        {
            // The same rule as for actions: PowerShell is Windows-only here; performance counters and CIM queries only exist on Windows.
            SensorSourceTypes.PowerShell => PlatformActionSupport.IsActionTypeSupported(os, ActionTypes.PowerShell),
            SensorSourceTypes.PerfCounter or SensorSourceTypes.CimQuery => string.IsNullOrEmpty(os)
                || string.Equals(os, PlatformActionSupport.WindowsOs, StringComparison.OrdinalIgnoreCase)
                || !IsKnownOs(os),
            _ => true,
        };
        return supported
            ? []
            : [new ValidationError("/source/type", ValidationCodes.Unsupported, "The sensor type '" + type + "' is not supported on " + os + ".")];
    }

    private static bool IsKnownOs(string os)
    {
        return string.Equals(os, PlatformActionSupport.WindowsOs, StringComparison.OrdinalIgnoreCase)
            || string.Equals(os, PlatformActionSupport.LinuxOs, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] Describe(ParseOutcome outcome)
    {
        return outcome.Errors.Count > 0
            ? [.. outcome.Errors.Select(Format)]
            : [outcome.ProblemMessage ?? "The document is not valid."];
    }

    private static string Format(ValidationError error)
    {
        return string.IsNullOrEmpty(error.Path) ? error.Message : error.Path + ": " + error.Message;
    }
}
