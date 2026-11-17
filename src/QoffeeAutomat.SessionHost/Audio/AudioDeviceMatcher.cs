using System.Globalization;

namespace QoffeeAutomat.SessionHost.Audio;

/// <summary>How a device name was resolved.</summary>
public enum AudioMatchKind
{
    /// <summary>The text is the label of one device, character for character.</summary>
    Exact,

    /// <summary>The text is part of exactly one label, ignoring case.</summary>
    UniquePart,

    /// <summary>The text is part of several labels and is not the exact label of any.</summary>
    Ambiguous,

    /// <summary>The text is part of no label.</summary>
    NotFound,

    /// <summary>The text is empty or only white space.</summary>
    Empty,
}

/// <summary>The outcome of <see cref="AudioDeviceMatcher.Match"/>.</summary>
/// <param name="Kind">How the text was resolved.</param>
/// <param name="Device">The device for <see cref="AudioMatchKind.Exact"/> and <see cref="AudioMatchKind.UniquePart"/>; null otherwise.</param>
/// <param name="Candidates">The labels that fit, for <see cref="AudioMatchKind.Ambiguous"/>; empty otherwise.</param>
public sealed record AudioMatch(AudioMatchKind Kind, AudioDevice? Device, IReadOnlyList<string> Candidates)
{
    /// <summary>True when exactly one device was found.</summary>
    public bool IsMatch => Device is not null;
}

/// <summary>
/// Resolves the variable <c>Device</c> of <c>audio.set_default_output</c> and <c>audio.set_default_input</c> to a device (PLAN.md
/// S11, T713): an exact label first, otherwise a text that is part of exactly one label, ignoring case. Everything else is
/// refused, and the caller changes nothing: a guess that picks one of several devices could switch the sound of a meeting to
/// the wrong speakers.
/// </summary>
public static class AudioDeviceMatcher
{
    /// <summary>Finds the device that a text names.</summary>
    /// <param name="devices">The active devices of one direction.</param>
    /// <param name="text">The text from the variable; surrounding white space is ignored.</param>
    /// <returns>The outcome.</returns>
    public static AudioMatch Match(IReadOnlyList<AudioDevice> devices, string? text)
    {
        ArgumentNullException.ThrowIfNull(devices);

        string query = text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return new AudioMatch(AudioMatchKind.Empty, null, []);
        }

        AudioDevice? exact = devices.FirstOrDefault(device => string.Equals(device.Label, query, StringComparison.Ordinal));
        if (exact is not null)
        {
            return new AudioMatch(AudioMatchKind.Exact, exact, []);
        }

        AudioDevice[] parts = [.. devices.Where(device => device.Label.Contains(query, StringComparison.OrdinalIgnoreCase))];
        return parts.Length switch
        {
            0 => new AudioMatch(AudioMatchKind.NotFound, null, []),
            1 => new AudioMatch(AudioMatchKind.UniquePart, parts[0], []),
            _ => new AudioMatch(AudioMatchKind.Ambiguous, null, [.. parts.Select(device => device.Label)]),
        };
    }

    /// <summary>
    /// Gives every device a label that is unique within the list: the friendly name, and <c>Name #2</c>, <c>Name #3</c> for further
    /// devices with the same name (two identical USB headsets). The numbering follows the endpoint ids, so it does not change
    /// when Windows lists the devices in another order. A device without a name is called <c>Unnamed device</c>.
    /// </summary>
    /// <param name="endpoints">The active endpoints of one direction.</param>
    /// <returns>The devices, sorted by label (ignoring case, then ordinal) so that the same devices always give the same list.</returns>
    public static IReadOnlyList<AudioDevice> Label(IEnumerable<AudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var used = new HashSet<string>(StringComparer.Ordinal);
        var devices = new List<AudioDevice>();
        foreach (AudioEndpoint endpoint in endpoints
            .DistinctBy(endpoint => endpoint.Id, StringComparer.Ordinal)
            .OrderBy(endpoint => NameOf(endpoint), StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Id, StringComparer.Ordinal))
        {
            string name = NameOf(endpoint);
            string label = name;
            for (int number = 2; !used.Add(label); number++)
            {
                label = name + " #" + number.ToString(CultureInfo.InvariantCulture);
            }

            devices.Add(new AudioDevice(endpoint.Id, label));
        }

        return [.. devices
            .OrderBy(device => device.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Label, StringComparer.Ordinal)];
    }

    private static string NameOf(AudioEndpoint endpoint)
    {
        return string.IsNullOrWhiteSpace(endpoint.Name) ? "Unnamed device" : endpoint.Name.Trim();
    }
}
