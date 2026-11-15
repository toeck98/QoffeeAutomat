using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Validation;

/// <summary>Builders and helpers shared by the validation tests.</summary>
internal static class TestDocuments
{
    /// <summary>A valid command with a builtin action; tests change one thing with <c>with</c>.</summary>
    public static CommandDocument Command() => new(
        DocumentSchema.CurrentVersion,
        "test_command",
        "Test command",
        true,
        false,
        RunAsValues.System,
        new BuiltinAction("power.shutdown"));

    public static CommandDocument WithParameters(params ParameterDefinition[] parameters) =>
        Command() with { Parameters = parameters };

    public static ParameterDefinition Parameter(string name, string type) => new(name, type);

    public static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement Text(string value) => Json(JsonSerializer.Serialize(value));

    public static JsonElement Number(double value) =>
        Json(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    public static JsonElement Flag(bool value) => Json(value ? "true" : "false");

    public static IReadOnlyDictionary<string, JsonElement> Vars(params (string Name, JsonElement Value)[] values) =>
        values.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);

    public static string Describe(ValidationResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Path} [{error.Code}] {error.Message}"));

    public static string Describe(BindResult result) =>
        string.Join("; ", result.Errors.Select(error => $"{error.Path} [{error.Code}] {error.Message}"));

    public static void AssertValid(ValidationResult result) => Assert.True(result.IsValid, Describe(result));

    public static void AssertError(ValidationResult result, string path, string code) =>
        Assert.Contains(result.Errors, error => error.Path == path && error.Code == code);

    public static void AssertError(BindResult result, string path, string code) =>
        Assert.Contains(result.Errors, error => error.Path == path && error.Code == code);
}
