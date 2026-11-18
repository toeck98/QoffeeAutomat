using System.Globalization;
using System.Text;

namespace QoffeeAutomat.Platform.Linux.Metrics.SystemMetrics;

/// <summary>One line of <c>/proc/mounts</c>.</summary>
/// <param name="Device">The device or source, for example <c>/dev/nvme0n1p2</c>, with the escapes of the kernel resolved.</param>
/// <param name="MountPoint">Where it is mounted, with the escapes of the kernel resolved.</param>
/// <param name="FileSystemType">The type, for example <c>ext4</c>.</param>
public sealed record MountEntry(string Device, string MountPoint, string FileSystemType);

/// <summary>One interface of <c>/proc/net/dev</c>.</summary>
/// <param name="Name">The interface name.</param>
/// <param name="ReceivedBytes">Bytes received.</param>
/// <param name="SentBytes">Bytes sent.</param>
public sealed record NetDevEntry(string Name, long ReceivedBytes, long SentBytes);

/// <summary>
/// Parsers for the text files that the kernel and the distribution provide (PLAN.md S06, T801). They are pure functions on the
/// content of a file, so that tests feed them synthetic fixtures; a content that does not parse yields null or an empty list and
/// never an exception.
/// </summary>
public static class ProcFileParser
{
    private const int CpuFieldsNeeded = 4;
    private const int CpuFieldsCounted = 8;
    private const int CpuIdleField = 3;
    private const int CpuIoWaitField = 4;
    private const int NetDevFieldsNeeded = 9;
    private const int NetDevSentField = 8;
    private const ulong KibibyteBytes = 1024;
    private const int OctalEscapeLength = 3;

    private static readonly char[] RouteSeparators = ['\t', ' '];

    /// <summary>Parses the line <c>cpu</c> (the sum of all processors) of <c>/proc/stat</c>.</summary>
    /// <param name="content">The content of <c>/proc/stat</c>.</param>
    /// <returns>The times; null when there is no such line.</returns>
    public static CpuTimes? ParseCpuTimes(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        foreach (string line in Lines(content))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length <= CpuFieldsNeeded || !string.Equals(fields[0], "cpu", StringComparison.Ordinal))
            {
                continue;
            }

            ulong total = 0;
            ulong idle = 0;
            int counted = Math.Min(fields.Length - 1, CpuFieldsCounted);
            for (int index = 0; index < counted; index++)
            {
                if (!ulong.TryParse(fields[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
                {
                    return null;
                }

                total += value;
                if (index is CpuIdleField or CpuIoWaitField)
                {
                    idle += value;
                }
            }

            return new CpuTimes(idle, total);
        }

        return null;
    }

    /// <summary>Parses <c>/proc/meminfo</c>.</summary>
    /// <param name="content">The content of <c>/proc/meminfo</c>.</param>
    /// <returns>The memory; null when the total is missing. A kernel without <c>MemAvailable</c> gets free plus buffers plus cache.</returns>
    public static MemoryStatus? ParseMemory(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var values = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (string line in Lines(content))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string[] fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length > 0 && ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong amount))
            {
                // The sizes are in kB; counters without a unit (HugePages_Total) are parsed too but never used.
                values[line[..colon].Trim()] = amount * KibibyteBytes;
            }
        }

        if (!values.TryGetValue("MemTotal", out ulong total) || total == 0)
        {
            return null;
        }

        if (values.TryGetValue("MemAvailable", out ulong available))
        {
            return new MemoryStatus(total, Math.Min(available, total));
        }

        if (!values.TryGetValue("MemFree", out ulong free))
        {
            return null;
        }

        ulong reclaimable = free + values.GetValueOrDefault("Buffers") + values.GetValueOrDefault("Cached");
        return new MemoryStatus(total, Math.Min(reclaimable, total));
    }

    /// <summary>Parses <c>/proc/mounts</c>.</summary>
    /// <param name="content">The content of <c>/proc/mounts</c>.</param>
    /// <returns>The entries in the order of the file; lines that do not have three fields are skipped.</returns>
    public static IReadOnlyList<MountEntry> ParseMounts(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var entries = new List<MountEntry>();
        foreach (string line in Lines(content))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 3)
            {
                entries.Add(new MountEntry(UnescapeMount(fields[0]), UnescapeMount(fields[1]), fields[2]));
            }
        }

        return entries;
    }

    /// <summary>Parses <c>/proc/net/dev</c>.</summary>
    /// <param name="content">The content of <c>/proc/net/dev</c>.</param>
    /// <returns>The interfaces in the order of the file; the two header lines are skipped.</returns>
    public static IReadOnlyList<NetDevEntry> ParseNetDev(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var entries = new List<NetDevEntry>();
        foreach (string line in Lines(content))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || line.Contains('|', StringComparison.Ordinal))
            {
                continue;
            }

            string[] fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length >= NetDevFieldsNeeded
                && ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong received)
                && ulong.TryParse(fields[NetDevSentField], NumberStyles.None, CultureInfo.InvariantCulture, out ulong sent))
            {
                entries.Add(new NetDevEntry(line[..colon].Trim(), Clamp(received), Clamp(sent)));
            }
        }

        return entries;
    }

    /// <summary>Parses <c>/proc/uptime</c>.</summary>
    /// <param name="content">The content of <c>/proc/uptime</c>.</param>
    /// <returns>The first number, the seconds since the start; null when it does not parse.</returns>
    public static TimeSpan? ParseUptime(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string[] fields = content.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return fields.Length > 0
            && double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            && double.IsFinite(seconds)
            && seconds >= 0
            && seconds < TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.FromSeconds(seconds)
                : null;
    }

    /// <summary>Parses <c>/etc/os-release</c>, a file of <c>KEY=value</c> lines in shell quoting.</summary>
    /// <param name="content">The content of the file.</param>
    /// <returns>The values by key; comments and lines without <c>=</c> are skipped.</returns>
    public static IReadOnlyDictionary<string, string> ParseOsRelease(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in Lines(content))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                values[line[..equals].Trim()] = Unquote(line[(equals + 1)..].Trim());
            }
        }

        return values;
    }

    /// <summary>Finds the interface of the default route in <c>/proc/net/route</c>.</summary>
    /// <param name="content">The content of <c>/proc/net/route</c>.</param>
    /// <returns>The name of the interface; with several default routes the one with the lowest metric. Null when there is none.</returns>
    public static string? ParseDefaultRouteInterface(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        const int interfaceField = 0;
        const int destinationField = 1;
        const int flagsField = 3;
        const int metricField = 6;
        const int maskField = 7;
        const int routeUp = 0x1;

        string? best = null;
        long bestMetric = long.MaxValue;
        foreach (string line in Lines(content))
        {
            string[] fields = line.Split(RouteSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= maskField
                || !string.Equals(fields[destinationField], "00000000", StringComparison.Ordinal)
                || !string.Equals(fields[maskField], "00000000", StringComparison.Ordinal)
                || !int.TryParse(fields[flagsField], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int flags)
                || (flags & routeUp) == 0
                || !long.TryParse(fields[metricField], NumberStyles.None, CultureInfo.InvariantCulture, out long metric))
            {
                continue;
            }

            if (metric < bestMetric)
            {
                best = fields[interfaceField];
                bestMetric = metric;
            }
        }

        return best;
    }

    private static string[] Lines(string content)
    {
        return content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static long Clamp(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    /// <summary>The kernel writes a space, tab, newline and backslash in a mount field as a three-digit octal escape.</summary>
    private static string UnescapeMount(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal))
        {
            return field;
        }

        var text = new StringBuilder(field.Length);
        for (int index = 0; index < field.Length; index++)
        {
            if (field[index] == '\\'
                && index + OctalEscapeLength < field.Length
                && IsOctal(field[index + 1])
                && IsOctal(field[index + 2])
                && IsOctal(field[index + 3]))
            {
                int code = ((field[index + 1] - '0') * 64) + ((field[index + 2] - '0') * 8) + (field[index + 3] - '0');
                text.Append((char)code);
                index += OctalEscapeLength;
            }
            else
            {
                text.Append(field[index]);
            }
        }

        return text.ToString();
    }

    private static bool IsOctal(char character) => character is >= '0' and <= '7';

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1];
        }

        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var text = new StringBuilder(value.Length);
        for (int index = 1; index < value.Length - 1; index++)
        {
            // In double quotes a backslash only escapes the characters that the shell treats specially.
            if (value[index] == '\\' && index + 1 < value.Length - 1 && value[index + 1] is '"' or '\\' or '$' or '`')
            {
                index++;
            }

            text.Append(value[index]);
        }

        return text.ToString();
    }
}
