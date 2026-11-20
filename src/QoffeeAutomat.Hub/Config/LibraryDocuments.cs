using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Hub.Config;

/// <summary>The outcome of checking a library document.</summary>
/// <param name="Errors">The errors with JSON pointers; empty when the document is valid.</param>
/// <param name="Body">The normalized body without revision and update time (the form that is compared and stamped); only meaningful when valid.</param>
/// <param name="Name">The display name of the document, if it has one.</param>
public sealed record LibraryValidation(IReadOnlyList<ValidationError> Errors, JsonElement Body, string? Name)
{
    /// <summary>True when there are no errors.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validation and normalization of library documents (PLAN.md S20, T907). Commands, sensors, modes and metric overrides go through the validators
/// of the agent (<see cref="DocumentValidator"/>), scripts through the rules for script names, so the hub accepts exactly what an agent accepts.
/// A valid document is read into its typed form and written again with <c>origin</c> <c>hub</c>, which drops members that the format does not
/// know. The hub never trusts its own validation: the agent validates every document again (D-027).
/// </summary>
public static class LibraryDocuments
{
    /// <summary>The longest script, in UTF-8 bytes.</summary>
    public const int MaxScriptBytes = 256 * 1024;

    /// <summary>The most variables in a variables document.</summary>
    public const int MaxVariables = 256;

    /// <summary>The longest value of a variable, in characters.</summary>
    public const int MaxVariableValueLength = 4096;

    private static readonly Regex VariableNamePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]{0,63}\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Tells whether a text is a valid name of a hub variable (<c>^[A-Za-z][A-Za-z0-9_]{0,63}$</c>).</summary>
    /// <param name="name">The text.</param>
    /// <returns>True when valid.</returns>
    public static bool IsValidVariableName(string? name) => name is not null && VariableNamePattern.IsMatch(name);

    /// <summary>Checks a document and brings it into its normalized form.</summary>
    /// <param name="kind">One of <see cref="DocumentKinds"/>.</param>
    /// <param name="id">The library id from the request path; it must equal the id inside the document.</param>
    /// <param name="body">The document as the client sent it.</param>
    /// <param name="commandLookup">Finds the current library command of an id; used for cycles of sequences and for the steps of modes.</param>
    /// <returns>The result.</returns>
    public static LibraryValidation Normalize(string kind, string id, JsonElement body, Func<string, CommandDocument?>? commandLookup)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(id);

        return kind switch
        {
            DocumentKinds.Command => NormalizeCommand(id, body, commandLookup),
            DocumentKinds.Sensor => NormalizeSensor(id, body),
            DocumentKinds.Mode => NormalizeMode(id, body, commandLookup),
            DocumentKinds.MetricOverrides => NormalizeMetricOverrides(id, body),
            DocumentKinds.Script => NormalizeScript(id, body),
            DocumentKinds.Variables => NormalizeVariables(id, body),
            _ => Fail("/kind", ValidationCodes.Invalid, "The kind is not one of command, sensor, script, mode, metric_overrides and variables."),
        };
    }

    /// <summary>Adds the revision and the update time that the hub owns to a normalized body. Scripts and variables have no header and stay as they are.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="normalized">The normalized body.</param>
    /// <param name="revision">The revision number.</param>
    /// <param name="now">The time of the save.</param>
    /// <returns>The body that is stored and sent to agents.</returns>
    public static JsonElement Stamp(string kind, JsonElement normalized, int revision, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(kind);

        if (kind is DocumentKinds.Script or DocumentKinds.Variables)
        {
            return normalized;
        }

        JsonObject node = JsonNode.Parse(normalized.GetRawText())!.AsObject();
        node["revision"] = revision;
        node["updatedAt"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return ConfigJson.Parse(node.ToJsonString());
    }

    private static LibraryValidation Fail(string path, string code, string message)
    {
        return new LibraryValidation([new ValidationError(path, code, message)], default, null);
    }

    private static LibraryValidation Failed(List<ValidationError> errors)
    {
        return new LibraryValidation(errors, default, null);
    }

    private static List<ValidationError> IdErrors(string expected, string? actual)
    {
        var errors = new List<ValidationError>();
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            errors.Add(new ValidationError("/id", ValidationCodes.Conflict, "The id inside the document must equal the id in the path."));
        }

        return errors;
    }

    private static LibraryValidation NormalizeCommand(string id, JsonElement body, Func<string, CommandDocument?>? lookup)
    {
        CommandDocument? document;
        try
        {
            document = body.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize(body, ConfigJsonContext.Default.CommandDocument) : null;
        }
        catch (JsonException exception)
        {
            return Fail(string.Empty, ValidationCodes.Invalid, "The document is not a valid command: " + exception.Message);
        }

        if (document is null)
        {
            return Fail(string.Empty, ValidationCodes.TypeMismatch, "A command document must be a JSON object.");
        }

        List<ValidationError> errors = IdErrors(id, document.Id);
        errors.AddRange(DocumentValidator.ValidateCommand(document, lookup).Errors);
        if (errors.Count > 0)
        {
            return Failed(errors);
        }

        CommandDocument normalized = document with { Revision = null, UpdatedAt = null, Origin = DocumentOrigins.Hub };
        return new LibraryValidation([], JsonSerializer.SerializeToElement(normalized, ConfigJsonContext.Default.CommandDocument), normalized.Name);
    }

    private static LibraryValidation NormalizeSensor(string id, JsonElement body)
    {
        SensorDocument? document;
        try
        {
            document = body.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize(body, ConfigJsonContext.Default.SensorDocument) : null;
        }
        catch (JsonException exception)
        {
            return Fail(string.Empty, ValidationCodes.Invalid, "The document is not a valid sensor: " + exception.Message);
        }

        if (document is null)
        {
            return Fail(string.Empty, ValidationCodes.TypeMismatch, "A sensor document must be a JSON object.");
        }

        List<ValidationError> errors = IdErrors(id, document.Id);
        errors.AddRange(DocumentValidator.ValidateSensor(document).Errors);
        if (errors.Count > 0)
        {
            return Failed(errors);
        }

        SensorDocument normalized = document with { Revision = null, UpdatedAt = null, Origin = DocumentOrigins.Hub };
        return new LibraryValidation([], JsonSerializer.SerializeToElement(normalized, ConfigJsonContext.Default.SensorDocument), normalized.Name);
    }

    private static LibraryValidation NormalizeMode(string id, JsonElement body, Func<string, CommandDocument?>? lookup)
    {
        ModeGroupDocument? document;
        try
        {
            document = body.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize(body, ConfigJsonContext.Default.ModeGroupDocument) : null;
        }
        catch (JsonException exception)
        {
            return Fail(string.Empty, ValidationCodes.Invalid, "The document is not a valid mode group: " + exception.Message);
        }

        if (document is null)
        {
            return Fail(string.Empty, ValidationCodes.TypeMismatch, "A mode group document must be a JSON object.");
        }

        List<ValidationError> errors = IdErrors(id, document.Id);
        errors.AddRange(DocumentValidator.ValidateModeGroup(document, lookup).Errors);
        if (errors.Count > 0)
        {
            return Failed(errors);
        }

        ModeGroupDocument normalized = document with { Revision = null, UpdatedAt = null, Origin = DocumentOrigins.Hub };
        return new LibraryValidation([], JsonSerializer.SerializeToElement(normalized, ConfigJsonContext.Default.ModeGroupDocument), normalized.Name);
    }

    private static LibraryValidation NormalizeMetricOverrides(string id, JsonElement body)
    {
        MetricOverridesDocument? document;
        try
        {
            document = body.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize(body, ConfigJsonContext.Default.MetricOverridesDocument) : null;
        }
        catch (JsonException exception)
        {
            return Fail(string.Empty, ValidationCodes.Invalid, "The document is not a valid metric override document: " + exception.Message);
        }

        if (document is null)
        {
            return Fail(string.Empty, ValidationCodes.TypeMismatch, "A metric override document must be a JSON object.");
        }

        var errors = new List<ValidationError>();
        if (!ScriptNames.IsValidDocumentId(id))
        {
            errors.Add(new ValidationError("/id", ValidationCodes.Invalid, "The id must match ^[a-z][a-z0-9_]{0,62}$."));
        }

        errors.AddRange(DocumentValidator.ValidateMetricOverrides(document).Errors);
        if (errors.Count > 0)
        {
            return Failed(errors);
        }

        MetricOverridesDocument normalized = document with { Revision = null, UpdatedAt = null, Origin = DocumentOrigins.Hub };
        return new LibraryValidation([], JsonSerializer.SerializeToElement(normalized, ConfigJsonContext.Default.MetricOverridesDocument), null);
    }

    private static LibraryValidation NormalizeScript(string id, JsonElement body)
    {
        var errors = new List<ValidationError>(ScriptNames.Validate(id, "/id"));
        if (body.ValueKind != JsonValueKind.String)
        {
            errors.Add(new ValidationError(string.Empty, ValidationCodes.TypeMismatch, "The body of a script must be a JSON string with the script text."));
            return Failed(errors);
        }

        string text = body.GetString() ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(text) > MaxScriptBytes)
        {
            errors.Add(new ValidationError(string.Empty, ValidationCodes.Invalid, "A script must not be larger than 256 KiB."));
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            errors.Add(new ValidationError(string.Empty, ValidationCodes.Invalid, "A script must not contain a NUL character."));
        }

        return errors.Count > 0 ? Failed(errors) : new LibraryValidation([], body, null);
    }

    private static LibraryValidation NormalizeVariables(string id, JsonElement body)
    {
        var errors = new List<ValidationError>();
        if (!ScriptNames.IsValidDocumentId(id))
        {
            errors.Add(new ValidationError("/id", ValidationCodes.Invalid, "The id must match ^[a-z][a-z0-9_]{0,62}$."));
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ValidationError(string.Empty, ValidationCodes.TypeMismatch, "A variables document must be a JSON object of names and string values."));
            return Failed(errors);
        }

        var variables = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in body.EnumerateObject())
        {
            string path = JsonPointer.Member(string.Empty, property.Name);
            if (!IsValidVariableName(property.Name))
            {
                errors.Add(new ValidationError(path, ValidationCodes.Invalid, "A variable name must match ^[A-Za-z][A-Za-z0-9_]{0,63}$."));
            }
            else if (property.Value.ValueKind != JsonValueKind.String)
            {
                errors.Add(new ValidationError(path, ValidationCodes.TypeMismatch, "The value of a variable must be a string."));
            }
            else if (property.Value.GetString()!.Length > MaxVariableValueLength)
            {
                errors.Add(new ValidationError(path, ValidationCodes.MaxLength, "The value of a variable must not be longer than 4096 characters."));
            }
            else
            {
                variables[property.Name] = property.Value.GetString()!;
            }
        }

        if (variables.Count > MaxVariables)
        {
            errors.Add(new ValidationError(string.Empty, ValidationCodes.Invalid, "A variables document has at most 256 variables."));
        }

        return errors.Count > 0 ? Failed(errors) : new LibraryValidation([], ConfigJson.StringObject(variables), null);
    }
}
