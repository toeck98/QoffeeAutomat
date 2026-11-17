using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.SessionHost.Metrics;

namespace QoffeeAutomat.SessionHost.Audio;

/// <summary>
/// The <see cref="IUserMetricProbe"/> that adds the audio device metrics (<c>audio.output_device</c> and <c>audio.input_device</c>,
/// PLAN.md S06, S11, T713) to the probe of the session: it answers those two ids with the label of the current default device and
/// passes every other id to the inner probe. The value is always one of the options that the agent announces, or null (no default
/// device) which makes the metric unavailable.
/// </summary>
public sealed class AudioDeviceMetricProbe : IUserMetricProbe
{
    private readonly IUserMetricProbe _inner;
    private readonly AudioDeviceService _audio;

    /// <summary>Creates the probe.</summary>
    /// <param name="inner">Answers every metric that is not an audio device metric.</param>
    /// <param name="audio">Knows the devices.</param>
    public AudioDeviceMetricProbe(IUserMetricProbe inner, AudioDeviceService audio)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(audio);

        _inner = inner;
        _audio = audio;
    }

    /// <inheritdoc />
    public object? Read(string metricId)
    {
        ArgumentNullException.ThrowIfNull(metricId);

        return metricId switch
        {
            SessionMetricIds.AudioOutputDevice => _audio.CurrentLabel(AudioFlow.Output),
            SessionMetricIds.AudioInputDevice => _audio.CurrentLabel(AudioFlow.Input),
            _ => _inner.Read(metricId),
        };
    }
}
