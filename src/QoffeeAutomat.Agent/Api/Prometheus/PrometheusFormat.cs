using System.Globalization;
using System.Text;

namespace QoffeeAutomat.Agent.Api.Prometheus;

/// <summary>One value of the Prometheus export.</summary>
/// <param name="MetricId">The metric id, for example <c>cpu.usage</c>.</param>
/// <param name="Name">The display name of the metric; the help text of the export.</param>
/// <param name="Unit">The unit, when the metric has one.</param>
/// <param name="Value">The value; a boolean metric is 1 or 0.</param>
public sealed record PrometheusSample(string MetricId, string Name, string? Unit, double Value);

/// <summary>
/// The Prometheus text exposition format, version 0.0.4 (PLAN.md S08, T709). Every metric is a gauge named
/// <c>qoffeeautomat_&lt;metric id with every character other than a letter, a digit or an underscore replaced by an underscore&gt;</c>,
/// with the labels <c>agent_id</c> and <c>agent</c>, the same naming that the hub uses (S20).
/// </summary>
public static class PrometheusFormat
{
    /// <summary>The media type of the export.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>The prefix of every metric name.</summary>
    public const string NamePrefix = "qoffeeautomat_";

    /// <summary>The name that a metric has in the export.</summary>
    /// <param name="metricId">The metric id.</param>
    /// <returns>The Prometheus metric name, for example <c>qoffeeautomat_cpu_usage</c>.</returns>
    public static string MetricName(string metricId)
    {
        ArgumentNullException.ThrowIfNull(metricId);

        var name = new StringBuilder(NamePrefix, NamePrefix.Length + metricId.Length);
        foreach (char character in metricId)
        {
            name.Append(char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_');
        }

        return name.ToString();
    }

    /// <summary>
    /// Writes the samples, ordered by metric id. A metric whose name is the same as that of an earlier one (<c>a.b</c> and <c>a_b</c>)
    /// is left out, so that the output never has two series that Prometheus would refuse as a duplicate; a value that is not a finite
    /// number is left out as well.
    /// </summary>
    /// <param name="samples">The values.</param>
    /// <param name="agentId">The persistent agent id, the label <c>agent_id</c>.</param>
    /// <param name="agentName">The agent name, the label <c>agent</c>.</param>
    /// <returns>The text; empty when there is nothing to export.</returns>
    public static string Write(IEnumerable<PrometheusSample> samples, string agentId, string agentName)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(agentName);

        string labels = "{agent_id=\"" + EscapeLabel(agentId) + "\",agent=\"" + EscapeLabel(agentName) + "\"}";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var text = new StringBuilder();
        foreach (PrometheusSample sample in samples.OrderBy(candidate => candidate.MetricId, StringComparer.Ordinal))
        {
            string name = MetricName(sample.MetricId);
            if (!double.IsFinite(sample.Value) || !seen.Add(name))
            {
                continue;
            }

            string help = string.IsNullOrEmpty(sample.Unit) ? sample.Name : sample.Name + " (" + sample.Unit + ")";
            text.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(help)).Append('\n');
            text.Append("# TYPE ").Append(name).Append(" gauge\n");
            text.Append(name).Append(labels).Append(' ').Append(sample.Value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Escapes a help text: backslash and line feed.</summary>
    private static string EscapeHelp(string text)
    {
        return text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Escapes a label value: backslash, double quote and line feed.</summary>
    private static string EscapeLabel(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);
    }
}
