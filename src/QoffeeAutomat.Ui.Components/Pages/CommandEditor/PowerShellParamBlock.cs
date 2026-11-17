using System.Text;
using System.Text.Json;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Ui.Components.Pages.CommandEditor;

/// <summary>
/// Writes the <c>param()</c> block of a PowerShell command from its parameter table (PLAN.md S06, D-012: the wrapper binds
/// the variables to named parameters), and puts it into the code: it replaces the block that is there or adds one at the top.
/// </summary>
public static class PowerShellParamBlock
{
    /// <summary>Builds the block for the parameters.</summary>
    /// <param name="parameters">The parameters; one without a name is left out.</param>
    /// <returns>The block, without a trailing line break; empty when there is no named parameter.</returns>
    public static string Build(IEnumerable<ParameterDefinition> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        List<ParameterDefinition> named = [.. parameters.Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))];
        if (named.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder("param(\n");
        for (int i = 0; i < named.Count; i++)
        {
            builder.Append("    ").Append(Declare(named[i])).Append(i < named.Count - 1 ? ",\n" : "\n");
        }

        return builder.Append(')').ToString();
    }

    /// <summary>Puts the block into the code.</summary>
    /// <param name="code">The code as it is now.</param>
    /// <param name="block">The block, see <see cref="Build"/>.</param>
    /// <returns>The code with the block in place of the old one, or with the block at the top when there was none.</returns>
    public static string Apply(string? code, string block)
    {
        ArgumentNullException.ThrowIfNull(block);

        string text = code ?? string.Empty;
        if (block.Length == 0)
        {
            return text;
        }

        (int start, int end) = Find(text);
        if (start < 0)
        {
            return text.Trim().Length == 0 ? block + "\n" : block + "\n\n" + text.TrimStart('\r', '\n');
        }

        return string.Concat(text.AsSpan(0, start), block, text.AsSpan(end));
    }

    private static string Declare(ParameterDefinition parameter)
    {
        var builder = new StringBuilder();
        bool hasDefault = parameter.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
        if (parameter.Required && !hasDefault)
        {
            builder.Append("[Parameter(Mandatory)] ");
        }

        if (parameter.Type == ParameterTypes.Choice && parameter.Options is { Count: > 0 })
        {
            builder.Append("[ValidateSet(").Append(string.Join(", ", parameter.Options.Select(Quote))).Append(")] ");
        }

        builder.Append('[').Append(TypeName(parameter.Type)).Append("]$").Append(parameter.Name);
        if (hasDefault)
        {
            builder.Append(" = ").Append(Literal(parameter.Default!.Value));
        }

        return builder.ToString();
    }

    private static string TypeName(string type)
    {
        return type switch
        {
            ParameterTypes.WholeNumber => "int",
            ParameterTypes.Real => "double",
            ParameterTypes.Flag => "bool",
            _ => "string",
        };
    }

    private static string Literal(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => "$true",
            JsonValueKind.False => "$false",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => Quote(value.GetString() ?? string.Empty),
            _ => "$null",
        };
    }

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Finds the first <c>param(...)</c> that starts a line; the range includes the closing parenthesis.</summary>
    private static (int Start, int End) Find(string text)
    {
        int lineStart = 0;
        while (lineStart <= text.Length)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            int cursor = lineStart;
            while (cursor < lineEnd && (text[cursor] == ' ' || text[cursor] == '\t'))
            {
                cursor++;
            }

            if (string.Compare(text, cursor, "param", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
            {
                int open = cursor + 5;
                while (open < text.Length && char.IsWhiteSpace(text[open]))
                {
                    open++;
                }

                if (open < text.Length && text[open] == '(')
                {
                    int close = FindClosing(text, open);
                    return close < 0 ? (-1, -1) : (cursor, close + 1);
                }
            }

            if (lineEnd >= text.Length)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        return (-1, -1);
    }

    private static int FindClosing(string text, int open)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '\'':
                case '"':
                    quote = c;
                    break;
                case '#':
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }
}
