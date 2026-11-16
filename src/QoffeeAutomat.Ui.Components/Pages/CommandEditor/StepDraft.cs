using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Ui.Components.Pages.CommandEditor;

/// <summary>The kinds of a sequence step in the editor.</summary>
public static class StepKinds
{
    /// <summary>Runs another command.</summary>
    public const string Command = "command";

    /// <summary>Waits.</summary>
    public const string Delay = "delay";
}

/// <summary>One step of a sequence while it is edited.</summary>
public sealed class StepDraft
{
    /// <summary>The problem code for a text that is not a JSON object.</summary>
    public const string InvalidJsonCode = "invalid_json";

    /// <summary>A key that stays the same while the step moves, for the renderer.</summary>
    public Guid Key { get; } = Guid.NewGuid();

    /// <summary>One of <see cref="StepKinds"/>.</summary>
    public string Kind { get; set; } = StepKinds.Command;

    /// <summary>The id of the command to run.</summary>
    public string CommandId { get; set; } = string.Empty;

    /// <summary>The variables of the command as a JSON object, or empty.</summary>
    public string VarsText { get; set; } = string.Empty;

    /// <summary>The seconds to wait.</summary>
    public string DelayText { get; set; } = string.Empty;

    /// <summary>Creates the draft of a stored step.</summary>
    /// <param name="step">The step.</param>
    /// <returns>The draft.</returns>
    public static StepDraft From(SequenceStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var draft = new StepDraft();
        if (step.DelaySeconds is { } delay && step.Command is null)
        {
            draft.Kind = StepKinds.Delay;
            draft.DelayText = delay.ToString("R", CultureInfo.InvariantCulture);
            return draft;
        }

        draft.CommandId = step.Command ?? string.Empty;
        if (step.Vars is { Count: > 0 } vars)
        {
            draft.VarsText = WriteVars(vars);
        }

        return draft;
    }

    private static string WriteVars(IReadOnlyDictionary<string, JsonElement> vars)
    {
        // Written by hand: no reflection-based serialization in the UI (PLAN.md S14).
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, JsonElement> variable in vars)
            {
                writer.WritePropertyName(variable.Key);
                variable.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Builds the step.</summary>
    /// <param name="path">The JSON pointer of the step, for the problems.</param>
    /// <param name="problems">Receives what cannot be read.</param>
    /// <returns>The step.</returns>
    public SequenceStep ToStep(string path, List<ApiValidationError> problems)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(problems);

        if (Kind == StepKinds.Delay)
        {
            string text = DelayText.Trim();
            double? seconds = null;
            if (text.Length > 0)
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed))
                {
                    seconds = parsed;
                }
                else
                {
                    problems.Add(new ApiValidationError(path + "/delaySeconds", ParameterDraft.NotANumberCode, "This is not a number."));
                }
            }

            return new SequenceStep(DelaySeconds: seconds ?? 0);
        }

        return new SequenceStep(Command: ParameterDraft.Blank(CommandId) ?? string.Empty, Vars: ParseVars(path, problems));
    }

    private Dictionary<string, JsonElement>? ParseVars(string path, List<ApiValidationError> problems)
    {
        if (string.IsNullOrWhiteSpace(VarsText))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(VarsText);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var vars = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    vars[property.Name] = property.Value.Clone();
                }

                return vars;
            }
        }
        catch (JsonException)
        {
            // Reported below, like an object that is none.
        }

        problems.Add(new ApiValidationError(path + "/vars", InvalidJsonCode, "The variables must be a JSON object."));
        return null;
    }
}
