namespace QoffeeAutomat.Agent.Hosting;

/// <summary>The platform implementations that exist or are planned.</summary>
public enum PlatformKind
{
    /// <summary>Real Windows providers (milestone M3).</summary>
    Windows,

    /// <summary>Deterministic fake providers; runs everywhere (PLAN.md D-021).</summary>
    Simulated,

    /// <summary>Real Linux providers (milestone M8).</summary>
    Linux,
}

/// <summary>The outcome of <see cref="PlatformSelector.Select"/>.</summary>
/// <param name="Kind">The platform to use; meaningful only when <paramref name="Error"/> is null.</param>
/// <param name="Warning">Set when the preferred platform is not available and another one was chosen.</param>
/// <param name="Error">Set when the request cannot be honoured.</param>
public sealed record PlatformSelection(PlatformKind Kind, string? Warning = null, string? Error = null)
{
    /// <summary>True when the selection can be used.</summary>
    public bool IsValid => Error is null;
}

/// <summary>
/// Chooses the platform implementation at start-up (PLAN.md T201): Windows on Windows, Linux on
/// Linux, and the simulated platform with <c>--platform simulated</c> or on systems that have no real
/// implementation. A platform that is not implemented in this build yet is replaced by the simulated
/// one when nothing was requested explicitly, and is an error when it was.
/// </summary>
public static class PlatformSelector
{
    /// <summary>The platforms that this build implements. Windows joins in milestone M3, Linux in M8.</summary>
    public static IReadOnlySet<PlatformKind> Implemented { get; } = new HashSet<PlatformKind> { PlatformKind.Simulated };

    /// <summary>The host operating system of the running process.</summary>
    /// <returns>The operating system family.</returns>
    public static HostOs CurrentOs()
    {
        return OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsLinux() ? HostOs.Linux : HostOs.Other;
    }

    /// <summary>Chooses the platform.</summary>
    /// <param name="requested">The value of <c>--platform</c>: null, <c>auto</c>, <c>simulated</c>, <c>windows</c> or <c>linux</c>.</param>
    /// <param name="os">The operating system the agent runs on.</param>
    /// <returns>The selection.</returns>
    public static PlatformSelection Select(string? requested, HostOs os)
    {
        if (string.IsNullOrEmpty(requested) || string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase))
        {
            PlatformKind preferred = os switch
            {
                HostOs.Windows => PlatformKind.Windows,
                HostOs.Linux => PlatformKind.Linux,
                _ => PlatformKind.Simulated,
            };

            return Implemented.Contains(preferred)
                ? new PlatformSelection(preferred)
                : new PlatformSelection(
                    PlatformKind.Simulated,
                    Warning: $"The {preferred} platform is not available in this build; using the simulated platform.");
        }

        PlatformKind? kind = requested.ToLowerInvariant() switch
        {
            "simulated" => PlatformKind.Simulated,
            "windows" => PlatformKind.Windows,
            "linux" => PlatformKind.Linux,
            _ => null,
        };

        if (kind is not { } chosen)
        {
            return new PlatformSelection(PlatformKind.Simulated, Error: $"Unknown platform '{requested}'.");
        }

        if ((chosen == PlatformKind.Windows && os != HostOs.Windows) || (chosen == PlatformKind.Linux && os != HostOs.Linux))
        {
            return new PlatformSelection(chosen, Error: $"The {chosen} platform needs a {chosen} system.");
        }

        if (!Implemented.Contains(chosen))
        {
            return new PlatformSelection(chosen, Error: $"The {chosen} platform is not available in this build. Use --platform simulated.");
        }

        return new PlatformSelection(chosen);
    }
}
