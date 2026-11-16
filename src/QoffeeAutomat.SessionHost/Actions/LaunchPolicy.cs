using System.Buffers;
using System.Text.RegularExpressions;

namespace QoffeeAutomat.SessionHost.Actions;

/// <summary>
/// The rules that keep <c>app.launch</c>, <c>url.open</c> and <c>process.kill</c> from doing more than their name says
/// (PLAN.md S09, D-012). The values come from Home Assistant, so they are checked here again whatever the agent validated:
/// a path must be a full path, a web address must be http or https, and a process name selects processes by image name only.
/// Every check returns a message for the user, or null when the value is acceptable.
/// </summary>
public static class LaunchPolicy
{
    private static readonly Regex FullPath = new(
        @"^(?:[A-Za-z]:[\\/]|\\\\(?![?.]\\)[^\\/]+[\\/][^\\/]+)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly char[] PathSeparators = ['\\', '/'];
    private static readonly SearchValues<char> InvalidPathCharacters = SearchValues.Create("\"<>|*?");
    private static readonly SearchValues<char> InvalidNameCharacters = SearchValues.Create("\\/:*?\"<>|");
    private static readonly SearchValues<char> CommandInterpreterCharacters = SearchValues.Create("&|<>^%\"");

    // Processes that Windows (or this product) needs to keep running. The user's token cannot end most of them anyway.
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "registry",
        "smss",
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass",
        "lsm",
        "svchost",
        "qoffeeautomat",
        "qoffeeautomat.agent",
        "qoffeeautomat.sessionhost",
    };

    /// <summary>Checks the arguments of <c>app.launch</c>.</summary>
    /// <param name="path">The program or document.</param>
    /// <param name="arguments">The arguments, or null.</param>
    /// <returns>Why the launch is refused; null when it is acceptable.</returns>
    public static string? CheckLaunch(string? path, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "A path is required.";
        }

        if (HasControlCharacter(path) || path.AsSpan().ContainsAny(InvalidPathCharacters))
        {
            return "The path contains characters that a file name cannot contain.";
        }

        if (!IsFullPath(path))
        {
            return "The path must be a full path such as C:\\Program Files\\App\\app.exe or a network path.";
        }

        if (arguments is null)
        {
            return null;
        }

        if (arguments.Contains('\0', StringComparison.Ordinal) || arguments.Contains('\r', StringComparison.Ordinal) || arguments.Contains('\n', StringComparison.Ordinal))
        {
            return "The arguments must not contain line breaks.";
        }

        if (IsCommandScript(path) && arguments.AsSpan().ContainsAny(CommandInterpreterCharacters))
        {
            return "The arguments contain characters that the command interpreter would act on, which a batch file cannot be given safely.";
        }

        return null;
    }

    /// <summary>Checks the address of <c>url.open</c>.</summary>
    /// <param name="url">The address.</param>
    /// <param name="uri">The parsed address when it is acceptable.</param>
    /// <returns>Why the address is refused; null when it is acceptable.</returns>
    public static string? CheckUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return "An address is required.";
        }

        if (HasControlCharacter(url) || url.Contains(' ', StringComparison.Ordinal))
        {
            return "The address must not contain spaces or control characters.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            return "The address is not a complete web address.";
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return "Only http and https addresses can be opened.";
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return "The address has no host name.";
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return "Addresses with a user name or password are not opened.";
        }

        uri = parsed;
        return null;
    }

    /// <summary>Checks the process name of <c>process.kill</c> and strips a trailing <c>.exe</c>.</summary>
    /// <param name="name">The name as given.</param>
    /// <param name="normalized">The name without <c>.exe</c> when it is acceptable.</param>
    /// <returns>Why the name is refused; null when it is acceptable.</returns>
    public static string? CheckProcessName(string? name, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return "A process name is required.";
        }

        string trimmed = name.Trim();
        if (HasControlCharacter(trimmed) || trimmed.AsSpan().ContainsAny(InvalidNameCharacters))
        {
            return "A process name is a file name without a path and without wildcards.";
        }

        string stem = trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
        if (stem.Length == 0)
        {
            return "A process name is required.";
        }

        if (ProtectedProcesses.Contains(stem))
        {
            return $"The process '{stem}' keeps the computer or this product running and cannot be ended.";
        }

        normalized = stem;
        return null;
    }

    private static bool HasControlCharacter(string value)
    {
        return value.Any(char.IsControl);
    }

    private static bool IsFullPath(string path)
    {
        return FullPath.IsMatch(path);
    }

    private static bool IsCommandScript(string path)
    {
        int separator = path.LastIndexOfAny(PathSeparators);
        string fileName = separator < 0 ? path : path[(separator + 1)..];
        int dot = fileName.LastIndexOf('.');
        if (dot < 0)
        {
            return false;
        }

        string extension = fileName[dot..];
        return extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }
}
