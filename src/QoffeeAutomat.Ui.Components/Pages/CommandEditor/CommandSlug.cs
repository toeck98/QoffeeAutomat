using System.Text;

namespace QoffeeAutomat.Ui.Components.Pages.CommandEditor;

/// <summary>Derives a command id from a name (PLAN.md S06: ids match <c>^[a-z][a-z0-9_]{0,62}$</c>).</summary>
public static class CommandSlug
{
    /// <summary>The longest id.</summary>
    public const int MaxLength = 63;

    /// <summary>Makes an id from a name: lower case, umlauts spelled out, every other run of characters as one underscore.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The id; empty when the name has no letters or digits.</returns>
    public static string FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        bool separator = false;
        foreach (char raw in name.Trim())
        {
            string piece = Translate(char.ToLowerInvariant(raw));
            if (piece.Length == 0)
            {
                separator = builder.Length > 0;
                continue;
            }

            if (separator)
            {
                builder.Append('_');
                separator = false;
            }

            builder.Append(piece);
        }

        string id = builder.ToString();
        if (id.Length == 0)
        {
            return string.Empty;
        }

        if (!char.IsAsciiLetterLower(id[0]))
        {
            id = "cmd_" + id;
        }

        return id.Length > MaxLength ? id[..MaxLength].TrimEnd('_') : id;
    }

    /// <summary>Tells whether a text is a valid command id.</summary>
    /// <param name="id">The text.</param>
    /// <returns>True when it matches the pattern of ids.</returns>
    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength || !char.IsAsciiLetterLower(id[0]))
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Translate(char c)
    {
        return c switch
        {
            'ä' => "ae",
            'ö' => "oe",
            'ü' => "ue",
            'ß' => "ss",
            'é' or 'è' or 'ê' => "e",
            'á' or 'à' or 'â' => "a",
            'ó' or 'ò' or 'ô' => "o",
            'ú' or 'ù' or 'û' => "u",
            'í' or 'ì' or 'î' => "i",
            _ when char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) => c.ToString(),
            _ => string.Empty,
        };
    }
}
