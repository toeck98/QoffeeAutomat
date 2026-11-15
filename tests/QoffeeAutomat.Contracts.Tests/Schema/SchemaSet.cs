using System.Text.Json;
using System.Text.RegularExpressions;

namespace QoffeeAutomat.Contracts.Tests.Schema;

/// <summary>
/// A small JSON Schema (draft 2020-12) validator for the subset of keywords that the schemas in
/// <c>protocol/schema</c> use. Loading fails when a schema uses any other keyword, so the schemas
/// stay within what both test suites implement (this class and scripts/dev/validate_protocol.py).
/// </summary>
internal sealed class SchemaSet
{
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$comment", "$defs", "title", "description", "default", "examples", "format",
    };

    private static readonly HashSet<string> Applicators = new(StringComparer.Ordinal)
    {
        "$ref", "type", "enum", "const", "required", "properties", "additionalProperties", "propertyNames",
        "minProperties", "items", "minItems", "maxItems", "uniqueItems", "minLength", "maxLength", "pattern",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "allOf", "anyOf", "oneOf", "if", "then", "else",
    };

    private readonly Dictionary<string, JsonElement> _documents = new(StringComparer.Ordinal);

    private SchemaSet()
    {
    }

    /// <summary>The ids of all loaded schemas.</summary>
    public IReadOnlyCollection<string> Ids => _documents.Keys;

    /// <summary>Loads every <c>*.schema.json</c> below a directory.</summary>
    public static SchemaSet Load(string schemaDirectory)
    {
        var set = new SchemaSet();
        foreach (string file in Directory.EnumerateFiles(schemaDirectory, "*.schema.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = document.RootElement.Clone();
            string id = root.GetProperty("$id").GetString()!;
            if (!string.Equals(root.GetProperty("$schema").GetString(), "https://json-schema.org/draft/2020-12/schema", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{file}: $schema must be draft 2020-12.");
            }

            set._documents.Add(id, root);
            CheckKeywords(root, Path.GetFileName(file));
        }

        return set;
    }

    /// <summary>Validates an instance against the schema with the given id; returns the errors.</summary>
    public IReadOnlyList<string> Validate(JsonElement instance, string schemaId)
    {
        var errors = new List<string>();
        ValidateNode(instance, _documents[schemaId], schemaId, string.Empty, errors);
        return errors;
    }

    private static void CheckKeywords(JsonElement schema, string where)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty keyword in schema.EnumerateObject())
        {
            if (!Annotations.Contains(keyword.Name) && !Applicators.Contains(keyword.Name))
            {
                throw new InvalidOperationException($"{where}: unsupported keyword '{keyword.Name}'.");
            }

            switch (keyword.Name)
            {
                case "properties":
                case "$defs":
                    foreach (JsonProperty member in keyword.Value.EnumerateObject())
                    {
                        CheckKeywords(member.Value, $"{where}/{keyword.Name}/{member.Name}");
                    }

                    break;
                case "allOf":
                case "anyOf":
                case "oneOf":
                    foreach (JsonElement item in keyword.Value.EnumerateArray())
                    {
                        CheckKeywords(item, $"{where}/{keyword.Name}");
                    }

                    break;
                case "items":
                case "additionalProperties":
                case "propertyNames":
                case "if":
                case "then":
                case "else":
                    CheckKeywords(keyword.Value, $"{where}/{keyword.Name}");
                    break;
            }
        }
    }

    private (JsonElement Node, string BaseId) Resolve(string baseId, string reference)
    {
        var resolved = new Uri(new Uri(baseId), reference);
        string id = resolved.GetLeftPart(UriPartial.Path);
        string fragment = Uri.UnescapeDataString(resolved.Fragment);

        JsonElement node = _documents[id];
        if (fragment.Length > 1)
        {
            foreach (string part in fragment[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                node = node.GetProperty(part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            }
        }

        return (node, id);
    }

    private void ValidateNode(JsonElement value, JsonElement schema, string baseId, string path, List<string> errors)
    {
        if (schema.ValueKind == JsonValueKind.True)
        {
            return;
        }

        string here = path.Length == 0 ? "/" : path;
        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add($"{here}: not allowed");
            return;
        }

        foreach (JsonProperty keyword in schema.EnumerateObject())
        {
            JsonElement argument = keyword.Value;
            switch (keyword.Name)
            {
                case "$ref":
                    (JsonElement target, string targetBase) = Resolve(baseId, argument.GetString()!);
                    ValidateNode(value, target, targetBase, path, errors);
                    break;
                case "type":
                    CheckType(value, argument, here, errors);
                    break;
                case "enum":
                    if (!argument.EnumerateArray().Any(candidate => JsonEquals(value, candidate)))
                    {
                        errors.Add($"{here}: not one of the allowed values");
                    }

                    break;
                case "const":
                    if (!JsonEquals(value, argument))
                    {
                        errors.Add($"{here}: must be {argument.GetRawText()}");
                    }

                    break;
                case "required":
                    CheckRequired(value, argument, here, errors);
                    break;
                case "properties":
                    CheckProperties(value, argument, baseId, path, errors);
                    break;
                case "additionalProperties":
                    CheckAdditionalProperties(value, schema, argument, baseId, path, errors);
                    break;
                case "propertyNames":
                    CheckPropertyNames(value, argument, baseId, path, errors);
                    break;
                case "minProperties":
                    if (value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() < argument.GetInt32())
                    {
                        errors.Add($"{here}: too few properties");
                    }

                    break;
                case "items":
                    CheckItems(value, argument, baseId, path, errors);
                    break;
                case "minItems":
                    if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() < argument.GetInt32())
                    {
                        errors.Add($"{here}: too few items");
                    }

                    break;
                case "maxItems":
                    if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > argument.GetInt32())
                    {
                        errors.Add($"{here}: too many items");
                    }

                    break;
                case "uniqueItems":
                    if (argument.ValueKind == JsonValueKind.True && value.ValueKind == JsonValueKind.Array && !AreUnique(value))
                    {
                        errors.Add($"{here}: items are not unique");
                    }

                    break;
                case "minLength":
                    if (value.ValueKind == JsonValueKind.String && CountRunes(value.GetString()!) < argument.GetInt32())
                    {
                        errors.Add($"{here}: too short");
                    }

                    break;
                case "maxLength":
                    if (value.ValueKind == JsonValueKind.String && CountRunes(value.GetString()!) > argument.GetInt32())
                    {
                        errors.Add($"{here}: too long");
                    }

                    break;
                case "pattern":
                    if (value.ValueKind == JsonValueKind.String
                        && !Regex.IsMatch(value.GetString()!, argument.GetString()!, RegexOptions.None, TimeSpan.FromSeconds(1)))
                    {
                        errors.Add($"{here}: does not match the pattern");
                    }

                    break;
                case "minimum":
                    CheckNumber(value, here, errors, number => number >= argument.GetDouble(), "below the minimum");
                    break;
                case "maximum":
                    CheckNumber(value, here, errors, number => number <= argument.GetDouble(), "above the maximum");
                    break;
                case "exclusiveMinimum":
                    CheckNumber(value, here, errors, number => number > argument.GetDouble(), "not above the exclusive minimum");
                    break;
                case "exclusiveMaximum":
                    CheckNumber(value, here, errors, number => number < argument.GetDouble(), "not below the exclusive maximum");
                    break;
                case "allOf":
                    foreach (JsonElement sub in argument.EnumerateArray())
                    {
                        ValidateNode(value, sub, baseId, path, errors);
                    }

                    break;
                case "anyOf":
                    if (!argument.EnumerateArray().Any(sub => Matches(value, sub, baseId, path)))
                    {
                        errors.Add($"{here}: matches none of anyOf");
                    }

                    break;
                case "oneOf":
                    int matches = argument.EnumerateArray().Count(sub => Matches(value, sub, baseId, path));
                    if (matches != 1)
                    {
                        errors.Add($"{here}: matches {matches} schemas of oneOf, expected 1");
                    }

                    break;
                case "if":
                    string branch = Matches(value, argument, baseId, path) ? "then" : "else";
                    if (schema.TryGetProperty(branch, out JsonElement branchSchema))
                    {
                        ValidateNode(value, branchSchema, baseId, path, errors);
                    }

                    break;
            }
        }
    }

    private bool Matches(JsonElement value, JsonElement schema, string baseId, string path)
    {
        var errors = new List<string>();
        ValidateNode(value, schema, baseId, path, errors);
        return errors.Count == 0;
    }

    private static void CheckType(JsonElement value, JsonElement argument, string here, List<string> errors)
    {
        IEnumerable<string> names = argument.ValueKind == JsonValueKind.String
            ? [argument.GetString()!]
            : argument.EnumerateArray().Select(item => item.GetString()!);

        if (!names.Any(name => IsType(value, name)))
        {
            errors.Add($"{here}: expected type {argument.GetRawText()}");
        }
    }

    private static bool IsType(JsonElement value, string name)
    {
        return name switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number
                && (value.TryGetInt64(out _) || (value.TryGetDouble(out double number) && number == Math.Floor(number))),
            _ => throw new InvalidOperationException($"Unknown type '{name}'."),
        };
    }

    private static void CheckRequired(JsonElement value, JsonElement argument, string here, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonElement name in argument.EnumerateArray())
        {
            if (!value.TryGetProperty(name.GetString()!, out _))
            {
                errors.Add($"{here}: missing required property '{name.GetString()}'");
            }
        }
    }

    private void CheckProperties(JsonElement value, JsonElement properties, string baseId, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in properties.EnumerateObject())
        {
            if (value.TryGetProperty(property.Name, out JsonElement member))
            {
                ValidateNode(member, property.Value, baseId, $"{path}/{property.Name}", errors);
            }
        }
    }

    private void CheckAdditionalProperties(JsonElement value, JsonElement schema, JsonElement argument, string baseId, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        bool hasProperties = schema.TryGetProperty("properties", out JsonElement declared);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!hasProperties || !declared.TryGetProperty(property.Name, out _))
            {
                ValidateNode(property.Value, argument, baseId, $"{path}/{property.Name}", errors);
            }
        }
    }

    private void CheckPropertyNames(JsonElement value, JsonElement argument, string baseId, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            JsonElement name = JsonSerializer.SerializeToElement(property.Name);
            ValidateNode(name, argument, baseId, $"{path}/{property.Name}", errors);
        }
    }

    private void CheckItems(JsonElement value, JsonElement argument, string baseId, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            ValidateNode(item, argument, baseId, $"{path}/{index}", errors);
            index++;
        }
    }

    private static void CheckNumber(JsonElement value, string here, List<string> errors, Func<double, bool> accepts, string problem)
    {
        if (value.ValueKind == JsonValueKind.Number && !accepts(value.GetDouble()))
        {
            errors.Add($"{here}: {problem}");
        }
    }

    private static bool AreUnique(JsonElement array)
    {
        JsonElement[] items = array.EnumerateArray().ToArray();
        for (int index = 0; index < items.Length; index++)
        {
            for (int other = 0; other < index; other++)
            {
                if (JsonEquals(items[index], items[other]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int CountRunes(string text)
    {
        int count = 0;
        foreach (System.Text.Rune unused in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return left.GetDouble() == right.GetDouble();
            case JsonValueKind.Array:
                JsonElement[] leftItems = left.EnumerateArray().ToArray();
                JsonElement[] rightItems = right.EnumerateArray().ToArray();
                return leftItems.Length == rightItems.Length
                    && leftItems.Zip(rightItems).All(pair => JsonEquals(pair.First, pair.Second));
            case JsonValueKind.Object:
                JsonProperty[] leftMembers = left.EnumerateObject().ToArray();
                return leftMembers.Length == right.EnumerateObject().Count()
                    && leftMembers.All(member => right.TryGetProperty(member.Name, out JsonElement other) && JsonEquals(member.Value, other));
            default:
                return true;
        }
    }
}
