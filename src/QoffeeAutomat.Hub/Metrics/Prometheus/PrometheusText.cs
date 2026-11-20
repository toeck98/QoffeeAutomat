using System.Globalization;
using System.Text;

namespace QoffeeAutomat.Hub.Metrics.Prometheus;

/// <summary>
/// The Prometheus text exposition format, version 0.0.4 (PLAN.md S20 "Metrics on the hub", T911): names, escaping and the writer. Metric ids, agent names and
/// everything else that comes from an agent is untrusted, so names are reduced to the characters the format allows and label values and help texts are escaped.
/// </summary>
public static class PrometheusText
{
    /// <summary>The content type of the text format.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>The prefix of every exported metric.</summary>
    public const string Prefix = "qoffeeautomat_";

    /// <summary>The name of the exported metric of an agent metric: <see cref="Prefix"/> plus the metric id with every character other than letters, digits and underscores replaced by an underscore.</summary>
    /// <param name="metricId">The metric id from the manifest, for example <c>cpu.usage</c>.</param>
    /// <returns>The name, for example <c>qoffeeautomat_cpu_usage</c>; always a valid Prometheus metric name.</returns>
    public static string MetricName(string metricId)
    {
        ArgumentNullException.ThrowIfNull(metricId);

        var name = new StringBuilder(Prefix.Length + metricId.Length);
        name.Append(Prefix);
        foreach (char c in metricId)
        {
            name.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
        }

        return name.ToString();
    }

    /// <summary>Escapes a label value: backslash, double quote and line feed are escaped, other control characters become a space and unpaired surrogates the replacement character.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The escaped value without the surrounding quotes.</returns>
    public static string EscapeLabelValue(string value) => Escape(value, quotes: true);

    /// <summary>Escapes a help text: backslash and line feed are escaped.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The escaped text.</returns>
    public static string EscapeHelp(string value) => Escape(value, quotes: false);

    /// <summary>Formats a sample value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The shortest text that reads back as the same number; <c>NaN</c>, <c>+Inf</c> and <c>-Inf</c> as the format spells them.</returns>
    public static string FormatValue(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "+Inf" : "-Inf";
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Escape(string value, bool quotes)
    {
        ArgumentNullException.ThrowIfNull(value);

        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\')
            {
                result.Append("\\\\");
            }
            else if (c == '\n')
            {
                result.Append("\\n");
            }
            else if (c == '"' && quotes)
            {
                result.Append("\\\"");
            }
            else if (c < ' ' || c == '\u007f')
            {
                result.Append(quotes ? ' ' : c);
            }
            else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                result.Append(c).Append(value[++i]);
            }
            else
            {
                result.Append(char.IsSurrogate(c) ? '�' : c);
            }
        }

        return result.ToString();
    }
}

/// <summary>Writes the text exposition format. A family is written once with its <c>HELP</c> and <c>TYPE</c> lines and all of its samples together, as the format requires.</summary>
public sealed class PrometheusTextWriter
{
    private readonly StringBuilder _text = new();
    private readonly HashSet<string> _families = new(StringComparer.Ordinal);

    /// <summary>Tells whether a family was written already.</summary>
    /// <param name="name">The family name.</param>
    /// <returns>True when <see cref="Family"/> wrote it.</returns>
    public bool Has(string name) => _families.Contains(name);

    /// <summary>Starts a family.</summary>
    /// <param name="name">The metric name; the caller makes sure that it is valid.</param>
    /// <param name="type">The type: <c>gauge</c> or <c>counter</c>.</param>
    /// <param name="help">The help text.</param>
    /// <returns>True when the family was started; false when a family of that name exists already (it is not written twice).</returns>
    public bool Family(string name, string type, string help)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(help);

        if (!_families.Add(name))
        {
            return false;
        }

        _text.Append("# HELP ").Append(name).Append(' ').Append(PrometheusText.EscapeHelp(help)).Append('\n');
        _text.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        return true;
    }

    /// <summary>Writes a sample of the family that was started last.</summary>
    /// <param name="name">The metric name; for a counter the name of the family.</param>
    /// <param name="value">The value.</param>
    /// <param name="labels">Label names (valid, written as they are) and values (escaped here), in the order to write.</param>
    public void Sample(string name, double value, params (string Name, string Value)[] labels)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(labels);

        _text.Append(name);
        if (labels.Length > 0)
        {
            _text.Append('{');
            for (int i = 0; i < labels.Length; i++)
            {
                if (i > 0)
                {
                    _text.Append(',');
                }

                _text.Append(labels[i].Name).Append("=\"").Append(PrometheusText.EscapeLabelValue(labels[i].Value)).Append('"');
            }

            _text.Append('}');
        }

        _text.Append(' ').Append(PrometheusText.FormatValue(value)).Append('\n');
    }

    /// <summary>The document so far as UTF-8.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ToBytes() => Encoding.UTF8.GetBytes(_text.ToString());

    /// <summary>The document so far.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => _text.ToString();
}
