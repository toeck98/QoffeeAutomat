using System.Globalization;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Platform.Linux.Service;

namespace QoffeeAutomat.Platform.Linux.Sessions;

/// <summary>Reads the metrics of the user's desktop inside the session helper (PLAN.md S06, S17, T803).</summary>
public interface IDesktopMetricSource
{
    /// <summary>Samples metrics.</summary>
    /// <param name="metricIds">The ids to sample; ids that are not in <see cref="LinuxSessionMetricCatalog.Ids"/> are ignored.</param>
    /// <param name="cancellationToken">Cancels the sampling.</param>
    /// <returns>The values by metric id (<see cref="double"/>, <see cref="bool"/> or <see cref="string"/>); a known id with value null is unavailable.</returns>
    ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(IReadOnlyCollection<string> metricIds, CancellationToken cancellationToken);
}

/// <summary>
/// The desktop metrics of Linux, read with the tools that the desktop has (PLAN.md S06, S17, T803). Programs are started without a shell,
/// and each one is optional: a metric whose tool is not installed is unavailable (null), which is not an error. The idle time comes from
/// <c>xprintidle</c> (X11) or from the idle monitor of the GNOME shell; the volume from <c>wpctl</c> (PipeWire) or <c>pactl</c>
/// (PulseAudio); the media player from <c>playerctl</c> (MPRIS, every player). The position of the track is published like on Windows: the
/// value and the moment it was valid, renewed when the track, the state or, by a jump, the position changes, and a client interpolates.
/// </summary>
public sealed class LinuxDesktopMetricSource : IDesktopMetricSource
{
    /// <summary>The separator of the fields of the <c>playerctl</c> answer (the unit separator never occurs in a title).</summary>
    public const char FieldSeparator = '\u001f';

    /// <summary>How far the position may differ from the interpolated one before it counts as a jump (a seek).</summary>
    public const double SeekToleranceSeconds = 3;

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] GnomeIdleArguments =
    [
        "--user", "call", "org.gnome.Mutter.IdleMonitor", "/org/gnome/Mutter/IdleMonitor/Core", "org.gnome.Mutter.IdleMonitor", "GetIdletime",
    ];

    private static readonly string[] WpctlVolumeArguments = ["get-volume", "@DEFAULT_AUDIO_SINK@"];
    private static readonly string[] PactlVolumeArguments = ["get-sink-volume", "@DEFAULT_SINK@"];
    private static readonly string[] PactlMuteArguments = ["get-sink-mute", "@DEFAULT_SINK@"];

    private readonly ICommandRunner _runner;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private PublishedPosition? _published;

    /// <summary>Creates the source.</summary>
    /// <param name="runner">Runs the tools; tests pass a fake.</param>
    /// <param name="time">The clock for the moment of the position; null uses the system clock.</param>
    public LinuxDesktopMetricSource(ICommandRunner runner, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Reads the milliseconds of idle time from the answer of <c>xprintidle</c> (<c>1234</c>) or of the GNOME idle monitor (<c>t 1234</c>).</summary>
    /// <param name="output">What the tool wrote.</param>
    /// <returns>The idle time in seconds; null when the answer is not a number.</returns>
    public static double? ParseIdleSeconds(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        string text = output.Trim();
        if (text.StartsWith("t ", StringComparison.Ordinal))
        {
            text = text[2..].Trim();
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long milliseconds) ? Math.Round(milliseconds / 1000.0) : null;
    }

    /// <summary>Reads the answer of <c>wpctl get-volume</c> (<c>Volume: 0.45</c> or <c>Volume: 0.45 [MUTED]</c>).</summary>
    /// <param name="output">What the tool wrote.</param>
    /// <returns>The volume in percent and whether the output is muted; null when the answer is not understood.</returns>
    public static (double Volume, bool Muted)? ParseWpctl(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        const string Prefix = "Volume:";
        int start = output.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        string rest = output[(start + Prefix.Length)..].Trim();
        string number = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction)
            ? (Math.Round(fraction * 100), rest.Contains("[MUTED]", StringComparison.Ordinal))
            : null;
    }

    /// <summary>Reads the first percentage of the answer of <c>pactl get-sink-volume</c> (<c>Volume: front-left: 29491 /  45% / -20.00 dB, ...</c>).</summary>
    /// <param name="output">What the tool wrote.</param>
    /// <returns>The volume in percent; null when there is none.</returns>
    public static double? ParsePactlVolume(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        int percent = output.IndexOf('%', StringComparison.Ordinal);
        if (percent <= 0)
        {
            return null;
        }

        int start = percent;
        while (start > 0 && char.IsAsciiDigit(output[start - 1]))
        {
            start--;
        }

        return start < percent && int.TryParse(output.AsSpan(start, percent - start), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    /// <summary>Reads the answer of <c>pactl get-sink-mute</c> (<c>Mute: yes</c>).</summary>
    /// <param name="output">What the tool wrote.</param>
    /// <returns>Whether the output is muted; null when the answer is not understood.</returns>
    public static bool? ParsePactlMute(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (output.Contains("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return output.Contains("no", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(
        IReadOnlyCollection<string> metricIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metricIds);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metricIds.Contains(SessionMetricIds.IdleSeconds, StringComparer.Ordinal))
        {
            values[SessionMetricIds.IdleSeconds] = await ReadIdleAsync(cancellationToken).ConfigureAwait(false);
        }

        bool wantsVolume = metricIds.Contains(SessionMetricIds.Volume, StringComparer.Ordinal);
        bool wantsMuted = metricIds.Contains(SessionMetricIds.Muted, StringComparer.Ordinal);
        if (wantsVolume || wantsMuted)
        {
            (double Volume, bool Muted)? audio = await ReadAudioAsync(cancellationToken).ConfigureAwait(false);
            if (wantsVolume)
            {
                values[SessionMetricIds.Volume] = audio?.Volume;
            }

            if (wantsMuted)
            {
                values[SessionMetricIds.Muted] = audio?.Muted;
            }
        }

        if (metricIds.Any(id => id.StartsWith("media.", StringComparison.Ordinal)))
        {
            IReadOnlyDictionary<string, object?> media = await ReadMediaAsync(cancellationToken).ConfigureAwait(false);
            foreach (string id in metricIds)
            {
                if (id.StartsWith("media.", StringComparison.Ordinal))
                {
                    values[id] = media.GetValueOrDefault(id);
                }
            }
        }

        return values;
    }

    private async ValueTask<double?> ReadIdleAsync(CancellationToken cancellationToken)
    {
        CommandResult? xprintidle = await _runner.RunAsync("xprintidle", [], ToolTimeout, cancellationToken).ConfigureAwait(false);
        if (xprintidle is { Succeeded: true } && ParseIdleSeconds(xprintidle.StandardOutput) is { } fromX)
        {
            return fromX;
        }

        CommandResult? shell = await _runner.RunAsync("busctl", GnomeIdleArguments, ToolTimeout, cancellationToken).ConfigureAwait(false);
        return shell is { Succeeded: true } ? ParseIdleSeconds(shell.StandardOutput) : null;
    }

    private async ValueTask<(double Volume, bool Muted)?> ReadAudioAsync(CancellationToken cancellationToken)
    {
        CommandResult? wpctl = await _runner.RunAsync("wpctl", WpctlVolumeArguments, ToolTimeout, cancellationToken).ConfigureAwait(false);
        if (wpctl is { Succeeded: true } && ParseWpctl(wpctl.StandardOutput) is { } fromPipeWire)
        {
            return fromPipeWire;
        }

        CommandResult? volume = await _runner.RunAsync("pactl", PactlVolumeArguments, ToolTimeout, cancellationToken).ConfigureAwait(false);
        CommandResult? mute = await _runner.RunAsync("pactl", PactlMuteArguments, ToolTimeout, cancellationToken).ConfigureAwait(false);
        if (volume is { Succeeded: true } && mute is { Succeeded: true }
            && ParsePactlVolume(volume.StandardOutput) is { } level
            && ParsePactlMute(mute.StandardOutput) is { } muted)
        {
            return (level, muted);
        }

        return null;
    }

    private async ValueTask<IReadOnlyDictionary<string, object?>> ReadMediaAsync(CancellationToken cancellationToken)
    {
        string format = string.Join(FieldSeparator, "{{status}}", "{{playerName}}", "{{title}}", "{{artist}}", "{{album}}", "{{mpris:length}}", "{{position}}");
        CommandResult? answer = await _runner.RunAsync("playerctl", ["metadata", "--format", format], ToolTimeout, cancellationToken).ConfigureAwait(false);

        // Without playerctl every media metric is unavailable; with it but without a player the state is idle and the rest is empty.
        if (answer is null)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        string[] fields = answer.Succeeded ? answer.StandardOutput.TrimEnd('\n', '\r').Split(FieldSeparator) : [];
        string state = fields.Length > 0 ? fields[0] switch
        {
            "Playing" => MediaStates.Playing,
            "Paused" => MediaStates.Paused,
            _ => MediaStates.Idle,
        } : MediaStates.Idle;

        string? title = Field(fields, 2);
        double? duration = Seconds(Field(fields, 5));
        double? position = Seconds(Field(fields, 6));
        (double? Position, string? UpdatedAt) tracked = Track(state, title, position);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SessionMetricIds.MediaState] = state,
            [SessionMetricIds.MediaApp] = Field(fields, 1),
            [SessionMetricIds.MediaTitle] = title,
            [SessionMetricIds.MediaArtist] = Field(fields, 3),
            [SessionMetricIds.MediaAlbum] = Field(fields, 4),
            [SessionMetricIds.MediaDuration] = duration,
            [SessionMetricIds.MediaPosition] = tracked.Position,
            [SessionMetricIds.MediaPositionUpdatedAt] = tracked.UpdatedAt,
        };
    }

    private static string? Field(string[] fields, int index)
    {
        return index < fields.Length && !string.IsNullOrWhiteSpace(fields[index]) ? fields[index].Trim() : null;
    }

    private static double? Seconds(string? microseconds)
    {
        return microseconds is not null && long.TryParse(microseconds, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            ? Math.Round(value / 1_000_000.0, 1)
            : null;
    }

    /// <summary>Keeps the pair of the position and the moment it was valid; a new pair only when the track, the state or the position by a jump changed.</summary>
    private (double? Position, string? UpdatedAt) Track(string state, string? title, double? position)
    {
        if (position is null)
        {
            lock (_gate)
            {
                _published = null;
            }

            return (null, null);
        }

        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            PublishedPosition? previous = _published;
            double expected = previous is null
                ? double.NaN
                : previous.Position + (previous.State == MediaStates.Playing ? (now - previous.UpdatedAt).TotalSeconds : 0);
            bool renew = previous is null
                || previous.State != state
                || !string.Equals(previous.Title, title, StringComparison.Ordinal)
                || Math.Abs(position.Value - expected) > SeekToleranceSeconds;
            if (renew)
            {
                _published = new PublishedPosition(state, title, position.Value, now);
            }

            PublishedPosition current = _published!;
            return (current.Position, current.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }
    }

    private sealed record PublishedPosition(string State, string? Title, double Position, DateTimeOffset UpdatedAt);
}
