using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QoffeeAutomat.Hub.Tests.Metrics;

/// <summary>One parsed sample of the Prometheus text format.</summary>
internal sealed record ParsedSample(string Name, IReadOnlyDictionary<string, string> Labels, double Value);

/// <summary>The result of parsing an exposition: the types and help texts by family name and the samples in order.</summary>
internal sealed record ParsedExposition(
    IReadOnlyDictionary<string, string> Types,
    IReadOnlyDictionary<string, string> Help,
    IReadOnlyList<ParsedSample> Samples)
{
    public ParsedSample Single(string name, params (string Label, string Value)[] labels)
    {
        return Samples.Single(sample => sample.Name == name
            && labels.All(label => sample.Labels.TryGetValue(label.Label, out string? value) && value == label.Value));
    }

    public IEnumerable<ParsedSample> Of(string name) => Samples.Where(sample => sample.Name == name);
}

/// <summary>
/// A strict parser of the Prometheus text exposition format 0.0.4, as the acceptance criterion of T911 asks ("passes an exposition format parser"). It follows the grammar of
/// the format description: <c>HELP</c> and <c>TYPE</c> comment lines (each at most once per family, before its samples), samples <c>name{label="value",...} value [timestamp]</c> with
/// the escapes <c>\\</c>, <c>\"</c> and <c>\n</c> in label values, valid names, a value that is a float or <c>NaN</c>, <c>+Inf</c>, <c>-Inf</c>, the samples of a family together, no series
/// twice, and a final line feed. Anything else throws a <see cref="FormatException"/> that names the line.
/// </summary>
internal static partial class PrometheusTextParser
{
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal) { "counter", "gauge", "histogram", "summary", "untyped" };

    [GeneratedRegex("^[a-zA-Z_:][a-zA-Z0-9_:]*$")]
    private static partial Regex MetricNameRegex();

    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex LabelNameRegex();

    public static ParsedExposition Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[^1] != '\n')
        {
            throw new FormatException("The exposition must end with a line feed.");
        }

        if (text.Contains('\r', StringComparison.Ordinal))
        {
            throw new FormatException("The exposition must use line feeds only.");
        }

        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var help = new Dictionary<string, string>(StringComparer.Ordinal);
        var samples = new List<ParsedSample>();
        var finished = new HashSet<string>(StringComparer.Ordinal);
        var series = new HashSet<string>(StringComparer.Ordinal);
        string? current = null;

        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length - 1; index++)
        {
            string line = lines[index];
            int number = index + 1;
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                ParseComment(line, number, types, help, samples);
                continue;
            }

            ParsedSample sample = ParseSample(line, number);
            if (current != sample.Name)
            {
                if (current is not null)
                {
                    _ = finished.Add(current);
                }

                if (finished.Contains(sample.Name))
                {
                    throw new FormatException($"Line {number}: the samples of {sample.Name} are not together.");
                }

                current = sample.Name;
            }

            string key = sample.Name + "{" + string.Join(',', sample.Labels.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)) + "}";
            if (!series.Add(key))
            {
                throw new FormatException($"Line {number}: the series {key} appears twice.");
            }

            samples.Add(sample);
        }

        return new ParsedExposition(types, help, samples);
    }

    private static void ParseComment(string line, int number, Dictionary<string, string> types, Dictionary<string, string> help, List<ParsedSample> samples)
    {
        string[] parts = line.Split(' ', 4);
        if (parts.Length < 3 || (parts[1] != "HELP" && parts[1] != "TYPE"))
        {
            return; // A plain comment.
        }

        string name = parts[2];
        if (!MetricNameRegex().IsMatch(name))
        {
            throw new FormatException($"Line {number}: invalid metric name '{name}'.");
        }

        if (samples.Any(sample => sample.Name == name))
        {
            throw new FormatException($"Line {number}: {parts[1]} of {name} comes after its samples.");
        }

        if (parts[1] == "TYPE")
        {
            if (parts.Length != 4 || !KnownTypes.Contains(parts[3]))
            {
                throw new FormatException($"Line {number}: invalid type.");
            }

            if (!types.TryAdd(name, parts[3]))
            {
                throw new FormatException($"Line {number}: TYPE of {name} appears twice.");
            }

            return;
        }

        string text = parts.Length == 4 ? parts[3] : string.Empty;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && (index + 1 >= text.Length || (text[index + 1] != '\\' && text[index + 1] != 'n')))
            {
                throw new FormatException($"Line {number}: invalid escape in the help text.");
            }

            if (text[index] == '\\')
            {
                index++;
            }
        }

        if (!help.TryAdd(name, text))
        {
            throw new FormatException($"Line {number}: HELP of {name} appears twice.");
        }
    }

    private static ParsedSample ParseSample(string line, int number)
    {
        int position = 0;
        while (position < line.Length && line[position] != '{' && line[position] != ' ')
        {
            position++;
        }

        string name = line[..position];
        if (!MetricNameRegex().IsMatch(name))
        {
            throw new FormatException($"Line {number}: invalid metric name '{name}'.");
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (position < line.Length && line[position] == '{')
        {
            position++;
            while (true)
            {
                if (position >= line.Length)
                {
                    throw new FormatException($"Line {number}: the label set is not closed.");
                }

                if (line[position] == '}')
                {
                    position++;
                    break;
                }

                int equals = line.IndexOf('=', position);
                if (equals < 0)
                {
                    throw new FormatException($"Line {number}: a label has no value.");
                }

                string label = line[position..equals];
                if (!LabelNameRegex().IsMatch(label) || label.StartsWith("__", StringComparison.Ordinal))
                {
                    throw new FormatException($"Line {number}: invalid label name '{label}'.");
                }

                position = equals + 1;
                if (position >= line.Length || line[position] != '"')
                {
                    throw new FormatException($"Line {number}: a label value must be quoted.");
                }

                position++;
                var value = new StringBuilder();
                while (true)
                {
                    if (position >= line.Length)
                    {
                        throw new FormatException($"Line {number}: a label value is not closed.");
                    }

                    char character = line[position++];
                    if (character == '"')
                    {
                        break;
                    }

                    if (character == '\\')
                    {
                        if (position >= line.Length)
                        {
                            throw new FormatException($"Line {number}: a dangling escape.");
                        }

                        char escaped = line[position++];
                        _ = value.Append(escaped switch
                        {
                            '\\' => '\\',
                            '"' => '"',
                            'n' => '\n',
                            _ => throw new FormatException($"Line {number}: invalid escape \\{escaped}."),
                        });
                    }
                    else
                    {
                        _ = value.Append(character);
                    }
                }

                if (!labels.TryAdd(label, value.ToString()))
                {
                    throw new FormatException($"Line {number}: the label {label} appears twice.");
                }

                if (position < line.Length && line[position] == ',')
                {
                    position++;
                }
                else if (position >= line.Length || line[position] != '}')
                {
                    throw new FormatException($"Line {number}: expected , or }}.");
                }
            }
        }

        if (position >= line.Length || line[position] != ' ')
        {
            throw new FormatException($"Line {number}: a space must separate the name and the value.");
        }

        string[] rest = line[(position + 1)..].Split(' ');
        if (rest.Length is < 1 or > 2)
        {
            throw new FormatException($"Line {number}: expected a value and an optional timestamp.");
        }

        if (rest.Length == 2 && !long.TryParse(rest[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        {
            throw new FormatException($"Line {number}: invalid timestamp.");
        }

        return new ParsedSample(name, labels, ParseValue(rest[0], number));
    }

    private static double ParseValue(string text, int number)
    {
        switch (text)
        {
            case "NaN":
                return double.NaN;
            case "+Inf":
                return double.PositiveInfinity;
            case "-Inf":
                return double.NegativeInfinity;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            throw new FormatException($"Line {number}: invalid value '{text}'.");
        }

        return value;
    }
}
