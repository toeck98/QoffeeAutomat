using Microsoft.Win32;

namespace QoffeeAutomat.SessionHost.Metrics;

/// <summary>The part of the current user's registry hive that <see cref="CapabilityAccess"/> reads.</summary>
public interface IUserRegistry
{
    /// <summary>Lists the sub keys of a key.</summary>
    /// <param name="path">The path below <c>HKEY_CURRENT_USER</c>.</param>
    /// <returns>The names; null when the key does not exist.</returns>
    string[]? GetSubKeyNames(string path);

    /// <summary>Reads a value.</summary>
    /// <param name="path">The path of the key below <c>HKEY_CURRENT_USER</c>.</param>
    /// <param name="name">The value name.</param>
    /// <returns>The value; null when the key or the value does not exist.</returns>
    object? GetValue(string path, string name);
}

/// <summary>The <see cref="IUserRegistry"/> of the real registry.</summary>
public sealed class CurrentUserRegistry : IUserRegistry
{
    /// <inheritdoc />
    public string[]? GetSubKeyNames(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetSubKeyNames();
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public object? GetValue(string path, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(name);

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// Finds out whether an application uses the microphone or the webcam right now (PLAN.md S06, T305) from the data of the
/// capability access manager, the part of Windows that shows the privacy indicator: below
/// <c>ConsentStore\microphone</c> and <c>ConsentStore\webcam</c> every application has a key with the times it started
/// (<c>LastUsedTimeStart</c>) and stopped (<c>LastUsedTimeStop</c>) using the device; a stop time of zero means "still in use".
/// Desktop applications are listed below <c>NonPackaged</c>. Entries that started before the last start of the computer are
/// ignored: an application that was killed while it used the device leaves a start without a stop behind.
/// </summary>
public sealed class CapabilityAccess
{
    /// <summary>The capability name of the microphone.</summary>
    public const string Microphone = "microphone";

    /// <summary>The capability name of the webcam.</summary>
    public const string Webcam = "webcam";

    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string NonPackagedKey = "NonPackaged";
    private const string StartValue = "LastUsedTimeStart";
    private const string StopValue = "LastUsedTimeStop";

    private readonly IUserRegistry _registry;
    private readonly Func<DateTimeOffset> _bootTime;

    /// <summary>Creates the reader.</summary>
    /// <param name="registry">The registry of the current user.</param>
    /// <param name="bootTime">When the computer was started, to ignore leftovers of earlier sessions.</param>
    public CapabilityAccess(IUserRegistry registry, Func<DateTimeOffset> bootTime)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bootTime);

        _registry = registry;
        _bootTime = bootTime;
    }

    /// <summary>Tells whether an application uses a device.</summary>
    /// <param name="capability"><see cref="Microphone"/> or <see cref="Webcam"/>.</param>
    /// <returns>True or false; null when this Windows keeps no such data (the key does not exist).</returns>
    public bool? IsInUse(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);

        string root = ConsentStore + @"\" + capability;
        string[]? applications = _registry.GetSubKeyNames(root);
        if (applications is null)
        {
            return null;
        }

        DateTimeOffset boot = _bootTime();
        if (applications.Any(name => !string.Equals(name, NonPackagedKey, StringComparison.Ordinal) && IsUsing(root + @"\" + name, boot)))
        {
            return true;
        }

        string nonPackaged = root + @"\" + NonPackagedKey;
        string[]? desktopApplications = _registry.GetSubKeyNames(nonPackaged);
        return desktopApplications is not null && desktopApplications.Any(name => IsUsing(nonPackaged + @"\" + name, boot));
    }

    private static long AsFileTime(object? value)
    {
        return value switch
        {
            long number => number,
            byte[] { Length: 8 } bytes => BitConverter.ToInt64(bytes, 0),
            _ => 0,
        };
    }

    private bool IsUsing(string path, DateTimeOffset boot)
    {
        long start = AsFileTime(_registry.GetValue(path, StartValue));
        long stop = AsFileTime(_registry.GetValue(path, StopValue));
        if (start <= 0 || start <= stop)
        {
            return false;
        }

        try
        {
            return DateTime.FromFileTimeUtc(start) >= boot.UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
