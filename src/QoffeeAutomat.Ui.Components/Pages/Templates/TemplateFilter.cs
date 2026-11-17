using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Ui.Components.Pages.Templates;

/// <summary>Filters the template gallery by the text that the user typed (T701).</summary>
public static class TemplateFilter
{
    /// <summary>Tells whether a template passes the search: the text must be part of the id, the name or the description, ignoring case.</summary>
    /// <param name="template">The template.</param>
    /// <param name="search">The search text; blank lets every template pass.</param>
    /// <returns>True when the template is shown.</returns>
    public static bool Matches(TemplateSummary template, string? search)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        string text = search.Trim();
        return template.Id.Contains(text, StringComparison.OrdinalIgnoreCase)
            || template.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (template.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>The address of the editor of a command or sensor that a template created.</summary>
    /// <param name="kind">See <see cref="TemplateKinds"/>.</param>
    /// <param name="id">The id of the command or sensor.</param>
    /// <returns>The relative address, or null for a script.</returns>
    public static string? EditorHref(string kind, string id)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(id);

        return kind switch
        {
            TemplateKinds.Command => "/commands/" + Uri.EscapeDataString(id),
            TemplateKinds.Sensor => "/sensors/" + Uri.EscapeDataString(id),
            TemplateKinds.Mode => "/modes/" + Uri.EscapeDataString(id),
            _ => null,
        };
    }
}
