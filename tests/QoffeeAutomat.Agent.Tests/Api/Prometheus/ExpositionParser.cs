using System.Globalization;
using System.Text;

namespace QoffeeAutomat.Agent.Tests.Api.Prometheus;

/// <summary>One sample line of the Prometheus text format.</summary>
internal sealed record ExpositionSample(string Name, IReadOnlyDictionary<string, string> Labels, double Value, long? Timestamp);

/// <summary>The samples of one metric name, with its <c>TYPE</c> and <c>HELP</c>.</summary>
internal sealed record ExpositionFamily(string Name, string Type, string? Help, IReadOnlyList<ExpositionSample> Samples);

/// <summary>
/// A strict reader of the Prometheus text exposition format, version 0.0.4, for the tests of the export. It reads what a Prometheus
/// server reads and refuses what a server refuses: a line without the final line feed, an invalid metric or label name, an escape
/// that the format does not know, a value that is not a float, a second <c>TYPE</c> or <c>HELP</c> line for a metric, a <c>TYPE</c>
/// line after the first sample of its metric, the samples of one metric spread over the output, and two samples of the same series.
/// </summary>
internal static class ExpositionParser
{
    private static readonly string[] MetricTypes = ["counter", "gauge", "histogram", "summary", "untyped"];

    /// <summary>Reads an exposition.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The metric families in the order of their first line.</returns>
    /// <exception cref="FormatException">The text is not valid; the message names the line.</exception>
    public static IReadOnlyList<ExpositionFamily> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length > 0 && !text.EndsWith('\n'))
        {
            throw new FormatException("The last line must end with a line feed.");
        }

        var families = new List<Builder>();
        var closed = new HashSet<string>(StringComparer.Ordinal);
        var series = new HashSet<string>(StringComparer.Ordinal);
        Builder? current = null;

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
                string[] parts = line.Split(' ', 4, StringSplitOptions.None);
                if (parts.Length >= 3 && parts[0] == "#" && parts[1] is "HELP" or "TYPE")
                {
                    string name = parts[2];
                    RequireName(name, number);
                    Builder family = Enter(name, families, closed, ref current, number);
                    if (parts[1] == "HELP")
                    {
                        if (family.Help is not null)
                        {
                            throw Error(number, $"second HELP line for '{name}'");
                        }

                        family.Help = UnescapeHelp(parts.Length == 4 ? parts[3] : string.Empty, number);
                    }
                    else
                    {
                        if (parts.Length != 4 || !MetricTypes.Contains(parts[3], StringComparer.Ordinal))
                        {
                            throw Error(number, "the type must be counter, gauge, histogram, summary or untyped");
                        }

                        if (family.Type is not null)
                        {
                            throw Error(number, $"second TYPE line for '{name}'");
                        }

                        if (family.Samples.Count > 0)
                        {
                            throw Error(number, $"the TYPE line of '{name}' comes after its first sample");
                        }

                        family.Type = parts[3];
                    }
                }

                // Any other line that starts with # is a comment.
                continue;
            }

            ExpositionSample sample = ParseSample(line, number);
            Builder owner = Enter(sample.Name, families, closed, ref current, number);
            string key = sample.Name + "{" + string.Join(',', sample.Labels.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)) + "}";
            if (!series.Add(key))
            {
                throw Error(number, $"the series {key} appears twice");
            }

            owner.Samples.Add(sample);
        }

        return [.. families.Select(family => new ExpositionFamily(family.Name, family.Type ?? "untyped", family.Help, family.Samples))];
    }

    private static Builder Enter(string name, List<Builder> families, HashSet<string> closed, ref Builder? current, int number)
    {
        if (current is not null && string.Equals(current.Name, name, StringComparison.Ordinal))
        {
            return current;
        }

        if (closed.Contains(name))
        {
            throw Error(number, $"the lines of '{name}' must be together");
        }

        if (current is not null)
        {
            closed.Add(current.Name);
        }

        var family = new Builder(name);
        families.Add(family);
        current = family;
        return family;
    }

    private static ExpositionSample ParseSample(string line, int number)
    {
        int position = 0;
        string name = ReadName(line, ref position, number);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (position < line.Length && line[position] == '{')
        {
            position++;
            ReadLabels(line, ref position, labels, number);
        }

        if (position >= line.Length || line[position] is not (' ' or '\t'))
        {
            throw Error(number, "a space and the value must follow the name and the labels");
        }

        // A server skips any run of blanks and tabs between the parts of a sample.
        string[] rest = line[position..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length is < 1 or > 2)
        {
            throw Error(number, "a sample is the name, the labels, the value and optionally a timestamp");
        }

        double value = ParseValue(rest[0], number);
        long? timestamp = null;
        if (rest.Length == 2)
        {
            if (!long.TryParse(rest[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed))
            {
                throw Error(number, "the timestamp must be an integer");
            }

            timestamp = parsed;
        }

        return new ExpositionSample(name, labels, value, timestamp);
    }

    private static string ReadName(string line, ref int position, int number)
    {
        int start = position;
        while (position < line.Length && (char.IsAsciiLetterOrDigit(line[position]) || line[position] is '_' or ':'))
        {
            position++;
        }

        string name = line[start..position];
        RequireName(name, number);
        return name;
    }

    private static void ReadLabels(string line, ref int position, Dictionary<string, string> labels, int number)
    {
        while (true)
        {
            if (position < line.Length && line[position] == '}')
            {
                position++;
                return;
            }

            int start = position;
            while (position < line.Length && (char.IsAsciiLetterOrDigit(line[position]) || line[position] == '_'))
            {
                position++;
            }

            string label = line[start..position];
            if (label.Length == 0 || char.IsAsciiDigit(label[0]))
            {
                throw Error(number, "invalid label name");
            }

            if (position + 1 >= line.Length || line[position] != '=' || line[position + 1] != '"')
            {
                throw Error(number, $"label '{label}' must be followed by =\"");
            }

            position += 2;
            var value = new StringBuilder();
            while (true)
            {
                if (position >= line.Length)
                {
                    throw Error(number, "unterminated label value");
                }

                char character = line[position++];
                if (character == '"')
                {
                    break;
                }

                if (character != '\\')
                {
                    value.Append(character);
                    continue;
                }

                if (position >= line.Length)
                {
                    throw Error(number, "unterminated escape");
                }

                char escaped = line[position++];
                value.Append(escaped switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    'n' => '\n',
                    _ => throw Error(number, $"unknown escape \\{escaped} in a label value"),
                });
            }

            if (!labels.TryAdd(label, value.ToString()))
            {
                throw Error(number, $"label '{label}' appears twice");
            }

            if (position < line.Length && line[position] == ',')
            {
                position++;
                continue;
            }

            if (position < line.Length && line[position] == '}')
            {
                position++;
                return;
            }

            throw Error(number, "a comma or } must follow a label value");
        }
    }

    private static double ParseValue(string text, int number)
    {
        switch (text.ToLowerInvariant())
        {
            case "nan":
                return double.NaN;
            case "inf" or "+inf":
                return double.PositiveInfinity;
            case "-inf":
                return double.NegativeInfinity;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            throw Error(number, $"'{text}' is not a float");
        }

        return value;
    }

    private static string UnescapeHelp(string text, int number)
    {
        var result = new StringBuilder();
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] != '\\')
            {
                result.Append(text[index]);
                continue;
            }

            if (index + 1 >= text.Length)
            {
                throw Error(number, "unterminated escape in a help text");
            }

            char escaped = text[++index];
            result.Append(escaped switch
            {
                '\\' => '\\',
                'n' => '\n',
                _ => throw Error(number, $"unknown escape \\{escaped} in a help text"),
            });
        }

        return result.ToString();
    }

    private static void RequireName(string name, int number)
    {
        bool valid = name.Length > 0
            && (char.IsAsciiLetter(name[0]) || name[0] is '_' or ':')
            && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or ':');
        if (!valid)
        {
            throw Error(number, $"'{name}' is not a valid metric name");
        }
    }

    private static FormatException Error(int number, string message) => new($"Line {number.ToString(CultureInfo.InvariantCulture)}: {message}.");

    private sealed class Builder
    {
        public Builder(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public string? Type { get; set; }

        public string? Help { get; set; }

        public List<ExpositionSample> Samples { get; } = [];
    }
}
