using System.Reflection;

namespace QoffeeAutomat.Hub.Hosting;

/// <summary>The version of the hub.</summary>
public static class HubVersion
{
    /// <summary>The semantic version from the assembly, without build metadata; <c>0.0.0</c> when unknown.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        string? informational = typeof(HubVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}

/// <summary>Process exit codes of the hub.</summary>
public static class HubExitCodes
{
    /// <summary>The hub ran and stopped normally.</summary>
    public const int Success = 0;

    /// <summary>The hub could not start or stopped because of an error.</summary>
    public const int Failure = 1;

    /// <summary>The command line is wrong.</summary>
    public const int Usage = 64;
}
