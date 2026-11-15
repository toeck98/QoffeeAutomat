using System.Text.Json;

namespace QoffeeAutomat.Contracts.Tests;

/// <summary>Semantic comparison of JSON values: member order and number formatting do not matter.</summary>
internal static class JsonCompare
{
    /// <summary>
    /// Returns null when the values are equal, otherwise a description of the first difference.
    /// </summary>
    /// <param name="expected">The expected value.</param>
    /// <param name="actual">The actual value.</param>
    /// <param name="ignoreNulls">Treat members whose value is null like absent members.</param>
    /// <param name="subset">Only require that every member of <paramref name="expected"/> is in <paramref name="actual"/>.</param>
    public static string? Difference(JsonElement expected, JsonElement actual, bool ignoreNulls = false, bool subset = false)
    {
        return Compare(expected, actual, ignoreNulls, subset, string.Empty);
    }

    private static string? Compare(JsonElement expected, JsonElement actual, bool ignoreNulls, bool subset, string path)
    {
        string here = path.Length == 0 ? "/" : path;
        if (expected.ValueKind != actual.ValueKind)
        {
            return $"{here}: expected {expected.ValueKind} but was {actual.ValueKind}";
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.String:
                return string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal)
                    ? null
                    : $"{here}: expected \"{expected.GetString()}\" but was \"{actual.GetString()}\"";
            case JsonValueKind.Number:
                return expected.GetDouble() == actual.GetDouble()
                    ? null
                    : $"{here}: expected {expected.GetRawText()} but was {actual.GetRawText()}";
            case JsonValueKind.Array:
                return CompareArrays(expected, actual, ignoreNulls, subset, path);
            case JsonValueKind.Object:
                return CompareObjects(expected, actual, ignoreNulls, subset, path);
            default:
                return null;
        }
    }

    private static string? CompareArrays(JsonElement expected, JsonElement actual, bool ignoreNulls, bool subset, string path)
    {
        JsonElement[] expectedItems = expected.EnumerateArray().ToArray();
        JsonElement[] actualItems = actual.EnumerateArray().ToArray();
        if (expectedItems.Length != actualItems.Length)
        {
            return $"{path}: expected {expectedItems.Length} items but was {actualItems.Length}";
        }

        for (int index = 0; index < expectedItems.Length; index++)
        {
            string? difference = Compare(expectedItems[index], actualItems[index], ignoreNulls, subset, $"{path}/{index}");
            if (difference is not null)
            {
                return difference;
            }
        }

        return null;
    }

    private static string? CompareObjects(JsonElement expected, JsonElement actual, bool ignoreNulls, bool subset, string path)
    {
        Dictionary<string, JsonElement> expectedMembers = Members(expected, ignoreNulls);
        Dictionary<string, JsonElement> actualMembers = Members(actual, ignoreNulls);

        foreach ((string name, JsonElement value) in expectedMembers)
        {
            if (!actualMembers.TryGetValue(name, out JsonElement other))
            {
                return $"{path}/{name}: missing";
            }

            string? difference = Compare(value, other, ignoreNulls, subset, $"{path}/{name}");
            if (difference is not null)
            {
                return difference;
            }
        }

        if (!subset)
        {
            foreach (string name in actualMembers.Keys)
            {
                if (!expectedMembers.ContainsKey(name))
                {
                    return $"{path}/{name}: unexpected";
                }
            }
        }

        return null;
    }

    private static Dictionary<string, JsonElement> Members(JsonElement element, bool ignoreNulls)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (ignoreNulls && property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            members[property.Name] = property.Value;
        }

        return members;
    }
}
