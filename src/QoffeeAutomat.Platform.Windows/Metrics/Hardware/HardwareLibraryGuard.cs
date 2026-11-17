using Microsoft.Win32;

namespace QoffeeAutomat.Platform.Windows.Metrics.HardwareSensors;

/// <summary>
/// Refuses hardware library versions and drivers that ADR 0002 rules out (T707). It holds no reference to the library, so it can be
/// used, and tested, without loading it.
/// </summary>
public static class HardwareLibraryGuard
{
    /// <summary>The name of the service that the WinRing0 based releases of the library create.</summary>
    public const string LegacyDriverServiceName = "WinRing0_1_2_0";

    /// <summary>
    /// The oldest version of LibreHardwareMonitorLib that the agent runs. Older releases ship the WinRing0 driver, which is vulnerable
    /// (CVE-2020-14979, CVE-2021-41285) and is flagged by Microsoft Defender; the PawnIO based releases start here (ADR 0002, to be
    /// re-verified by the owner before the first release that contains the sensors).
    /// </summary>
    public static readonly Version MinimumLibraryVersion = new(0, 9, 5);

    /// <summary>A version with all four parts, so that <c>0.0</c> and <c>0.0.0.0</c> both count as "not set" (a missing part compares lower than 0).</summary>
    private static readonly Version Zero = new(0, 0, 0, 0);

    /// <summary>Tells whether a version of the library may be used.</summary>
    /// <param name="version">The version of the library; null when it cannot be determined.</param>
    /// <returns>True when it is at least <see cref="MinimumLibraryVersion"/>; an unknown version is refused.</returns>
    public static bool IsAcceptable(Version? version)
    {
        return version is not null && version >= MinimumLibraryVersion;
    }

    /// <summary>
    /// Reads the version of the library from its informational version (<c>0.9.5+abc</c>), falling back to the assembly version.
    /// </summary>
    /// <param name="informationalVersion">The informational version, which may carry a <c>+</c> or <c>-</c> suffix; may be null.</param>
    /// <param name="assemblyVersion">The assembly version; may be null.</param>
    /// <returns>The version, or null when neither can be read or both are <c>0.0</c>.</returns>
    public static Version? ParseVersion(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            int end = informationalVersion.AsSpan().IndexOfAny('+', '-');
            string core = end >= 0 ? informationalVersion[..end] : informationalVersion;
            if (Version.TryParse(core, out Version? parsed) && parsed > Zero)
            {
                return parsed;
            }
        }

        return assemblyVersion is not null && assemblyVersion > Zero ? assemblyVersion : null;
    }

    /// <summary>Tells whether the service of the legacy WinRing0 driver exists. A read of the registry; nothing is started or loaded.</summary>
    /// <returns>True when the service is registered.</returns>
    public static bool LegacyDriverServiceExists()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + LegacyDriverServiceName);
        return key is not null;
    }
}

/// <summary>
/// Looks for PawnIO, the kernel driver that the library uses for processor and mainboard sensors (ADR 0002). The driver is installed by
/// the user with its own installer; the agent only checks that its service is registered.
/// </summary>
public sealed class PawnIoDriverProbe : IHardwareDriverProbe
{
    /// <summary>The name of the service that the PawnIO installer registers.</summary>
    public const string ServiceName = "PawnIO";

    /// <inheritdoc />
    public string DriverName => "PawnIO";

    /// <inheritdoc />
    public bool IsInstalled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
            return key is not null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not readable: say "not installed" rather than guess; the status then explains what to check.
            return false;
        }
    }
}
