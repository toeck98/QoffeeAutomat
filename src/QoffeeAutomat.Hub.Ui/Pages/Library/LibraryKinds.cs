using System.Text;
using System.Text.Json;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Hub.Ui.Pages.Library;

/// <summary>
/// What the library pages know about the kinds of library documents (PLAN.md S20, T916): which editor a kind opens, and how the body of the kinds that
/// have no form editor (scripts, modes, metric overrides, variables) is turned into text and back. Commands and sensors are edited with the
/// editors of the component library (<c>LibraryAgentApi</c>), so no form is written twice (D-033).
/// </summary>
public static class LibraryKinds
{
    /// <summary>All kinds in the order the pages show them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        DocumentKinds.Command,
        DocumentKinds.Sensor,
        DocumentKinds.Script,
        DocumentKinds.Mode,
        DocumentKinds.MetricOverrides,
        DocumentKinds.Variables,
    ];

    /// <summary>Tells whether the document of the kind is edited as text on the page of the library rather than with a form of the component library.</summary>
    /// <param name="kind">One of <see cref="DocumentKinds"/>.</param>
    /// <returns>True for scripts, modes, metric overrides and variables.</returns>
    public static bool UsesTextEditor(string kind) => kind is not (DocumentKinds.Command or DocumentKinds.Sensor);

    /// <summary>The address of the editor of a document, relative to the base address of the app.</summary>
    /// <param name="kind">One of <see cref="DocumentKinds"/>.</param>
    /// <param name="id">The library id.</param>
    /// <returns>For example <c>library/command/lock-screen</c>.</returns>
    public static string EditHref(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(id);

        return Base(kind) + "/" + Uri.EscapeDataString(id);
    }

    /// <summary>The address of the editor of a new document of the kind.</summary>
    /// <param name="kind">One of <see cref="DocumentKinds"/>.</param>
    /// <returns>For example <c>library/sensor/new</c>.</returns>
    public static string NewHref(string kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);

        return Base(kind) + "/new";
    }

    /// <summary>The text that the text editor starts with for a new document.</summary>
    /// <param name="kind">One of <see cref="DocumentKinds"/>.</param>
    /// <returns>An empty text for a script, an empty JSON object for the others.</returns>
    public static string NewText(string kind) => kind == DocumentKinds.Script ? string.Empty : "{}";

    /// <summary>The text that the text editor shows for the body of a document.</summary>
    /// <param name="kind">One of <see cref="DocumentKinds"/>.</param>
    /// <param name="body">The body from the hub.</param>
    /// <returns>The script text, or the JSON of the document indented.</returns>
    public static string ToText(string kind, JsonElement body)
    {
        if (kind == DocumentKinds.Script)
        {
            return body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : string.Empty;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            body.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Turns the text of the editor into the body that the hub expects.</summary>
    /// <param name="kind">One of <see cref="DocumentKinds"/>.</param>
    /// <param name="text">The text.</param>
    /// <param name="body">The body when the text is usable.</param>
    /// <param name="error">Why the text is not usable (the message of the JSON parser); <c>null</c> when it is.</param>
    /// <returns>True when <paramref name="body"/> can be sent. The hub validates the content as well; this only catches what cannot be sent at all.</returns>
    public static bool TryParse(string kind, string text, out JsonElement body, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);

        error = null;
        if (kind == DocumentKinds.Script)
        {
            // The body of a script is a JSON string that holds the script text.
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStringValue(text);
            }

            using JsonDocument script = JsonDocument.Parse(stream.ToArray());
            body = script.RootElement.Clone();
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                body = default;
                error = "The document must be a JSON object.";
                return false;
            }

            body = document.RootElement.Clone();
            return true;
        }
        catch (JsonException exception)
        {
            body = default;
            error = exception.Message;
            return false;
        }
    }

    /// <summary>The id rules of the hub for a library document that is not a script: lower-case letters, digits and underscores, starting with a letter.</summary>
    /// <param name="id">The id.</param>
    /// <returns>True when the id is one the hub accepts for a document, a variables document or a profile.</returns>
    public static bool IsValidDocumentId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 63 || id[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (char character in id)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Base(string kind) => kind switch
    {
        DocumentKinds.Command => "library/command",
        DocumentKinds.Sensor => "library/sensor",
        _ => "library/document/" + Uri.EscapeDataString(kind),
    };
}
