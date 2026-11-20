using System.Text.Json;
using MudBlazor;
using QoffeeAutomat.Hub.Fleet.Actions;
using QoffeeAutomat.Hub.Ui.Services;

namespace QoffeeAutomat.Hub.Ui.Pages.Actions;

/// <summary>What the fleet-action page needs to turn what a user typed into a request, and to show a state (T917). The hub validates everything again.</summary>
public static class ActionForms
{
    /// <summary>Reads tags that a user typed: separated by commas, semicolons or white space, trimmed, in lower case, each once.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The tags; empty for empty text.</returns>
    public static IReadOnlyList<string> ParseTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return
        [
            .. text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(tag => tag.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>Reads the variables of a command that a user typed as a JSON object.</summary>
    /// <param name="text">The text; empty means no variables.</param>
    /// <param name="vars">The variables; <c>null</c> for none.</param>
    /// <returns>True when the text is empty or a JSON object; false when it is anything else.</returns>
    public static bool TryParseVars(string? text, out IReadOnlyDictionary<string, JsonElement>? vars)
    {
        vars = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var parsed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                parsed[property.Name] = property.Value.Clone();
            }

            vars = parsed.Count == 0 ? null : parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The colour of the state of an action.</summary>
    /// <param name="state">One of <see cref="FleetActionStates"/>.</param>
    /// <returns>The colour.</returns>
    public static Color ActionColor(string state) => state switch
    {
        FleetActionStates.Running => Color.Info,
        FleetActionStates.Completed => Color.Success,
        FleetActionStates.Stopped => Color.Error,
        FleetActionStates.Cancelled => Color.Warning,
        _ => Color.Default,
    };

    /// <summary>The colour of the state of one agent in an action.</summary>
    /// <param name="state">One of <see cref="FleetActionItemStates"/>.</param>
    /// <returns>The colour.</returns>
    public static Color ItemColor(string state) => state switch
    {
        FleetActionItemStates.Success => Color.Success,
        FleetActionItemStates.Failure => Color.Error,
        FleetActionItemStates.Running => Color.Info,
        FleetActionItemStates.Offline or FleetActionItemStates.Cancelled => Color.Warning,
        _ => Color.Default,
    };

    /// <summary>The role that may cancel an action: an update is the administrator's, a bulk run the operator's.</summary>
    /// <param name="action">The action.</param>
    /// <returns>One of <see cref="HubRoleNames"/>.</returns>
    public static string CancelRole(FleetActionInfo action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return action.Kind == FleetActionKinds.Update ? HubRoleNames.Admin : HubRoleNames.Operator;
    }
}
