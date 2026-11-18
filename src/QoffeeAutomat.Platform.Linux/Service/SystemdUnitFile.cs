using System.Globalization;
using System.Text;

namespace QoffeeAutomat.Platform.Linux.Service;

/// <summary>The fixed facts of the systemd service (PLAN.md S05, S17).</summary>
public static class LinuxServiceDefaults
{
    /// <summary>The unit name without the suffix; also the name of the directories below <c>/etc</c>, <c>/var/lib</c> and <c>/var/log</c>.</summary>
    public const string Name = "qoffeeautomat";

    /// <summary>The directory that holds the units of the administrator.</summary>
    public const string UnitDirectory = "/etc/systemd/system";

    /// <summary>Where the installed package puts the agent; the unit file in <c>packaging/linux</c> starts the agent from here.</summary>
    public const string PackageExecutablePath = "/usr/lib/qoffeeautomat/qoffeeautomat";

    /// <summary>The suffix of a service unit.</summary>
    public const string UnitSuffix = ".service";

    /// <summary>How long <c>service start</c> and <c>service stop</c> wait for systemctl, which returns when the unit is up or down.</summary>
    public static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The name of the unit file for a service name.</summary>
    /// <param name="name">The service name.</param>
    /// <returns>For example <c>qoffeeautomat.service</c>.</returns>
    public static string UnitName(string name) => name + UnitSuffix;
}

/// <summary>What goes into the unit file.</summary>
/// <param name="ExecutablePath">The program that systemd starts: the agent, or <c>dotnet</c> when the agent runs through the dotnet host.</param>
/// <param name="ExecutableArguments">Arguments that come before the verb <c>run</c>, for example the path of the agent assembly when <paramref name="ExecutablePath"/> is <c>dotnet</c>.</param>
/// <param name="DataDirectory">The value of <c>--data-dir</c> as an absolute path, or null for the layout of PLAN.md S05.</param>
/// <param name="UiPort">The port of the loopback UI listener, or null for the setting.</param>
/// <param name="LinkPort">The port of the QoffeeLink listener, or null for the setting.</param>
public sealed record SystemdUnitOptions(
    string ExecutablePath,
    IReadOnlyList<string> ExecutableArguments,
    string? DataDirectory = null,
    int? UiPort = null,
    int? LinkPort = null)
{
    /// <summary>The options of the unit that the package ships (<c>packaging/linux/qoffeeautomat.service</c>).</summary>
    public static SystemdUnitOptions Packaged { get; } = new(LinuxServiceDefaults.PackageExecutablePath, []);
}

/// <summary>
/// Writes the systemd unit of the agent (PLAN.md S17, T802). The service runs as root, like the Windows service runs as LocalSystem,
/// because commands of <c>runAs: system</c> are meant to change the computer (D-009, D-010). It is <c>Type=notify</c>, so
/// <c>systemctl start</c> returns when the listeners accept connections, and it has a watchdog. For the default layout systemd itself
/// creates the directories of PLAN.md S05 with mode 0700; <c>UMask=0077</c> keeps what the agent writes in them private (S09).
/// <para>
/// The file that the package ships is exactly the output for <see cref="SystemdUnitOptions.Packaged"/>; a test compares the two.
/// </para>
/// </summary>
public static class SystemdUnitFile
{
    /// <summary>Quotes one word of <c>ExecStart=</c> as <c>systemd.service(5)</c> reads it.</summary>
    /// <param name="word">The word.</param>
    /// <returns>The word, quoted when it needs it, with <c>%</c> and <c>$</c> doubled so that no specifier or variable is expanded.</returns>
    /// <exception cref="ArgumentException">The word contains a line break or a control character, which would end the setting.</exception>
    public static string QuoteWord(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        if (word.Any(char.IsControl))
        {
            throw new ArgumentException("A unit file setting must not contain control characters.", nameof(word));
        }

        string escaped = word.Replace("%", "%%", StringComparison.Ordinal).Replace("$", "$$", StringComparison.Ordinal);
        bool needsQuotes = escaped.Length == 0 || escaped.Any(character => char.IsWhiteSpace(character) || character is '"' or '\'' or '\\' or ';');
        if (!needsQuotes)
        {
            return escaped;
        }

        return "\"" + escaped.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Builds the value of <c>ExecStart=</c>.</summary>
    /// <param name="options">The program and its options.</param>
    /// <returns>The quoted command line.</returns>
    public static string BuildExecStart(SystemdUnitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // systemd starts a program by its absolute path only.
        if (!options.ExecutablePath.StartsWith('/'))
        {
            throw new ArgumentException("The executable must be an absolute path.", nameof(options));
        }

        var words = new List<string> { options.ExecutablePath };
        words.AddRange(options.ExecutableArguments);
        words.Add("run");
        if (options.DataDirectory is not null)
        {
            words.Add("--data-dir");
            words.Add(options.DataDirectory);
        }

        if (options.UiPort is { } ui)
        {
            words.Add("--ui-port");
            words.Add(ui.ToString(CultureInfo.InvariantCulture));
        }

        if (options.LinkPort is { } link)
        {
            words.Add("--link-port");
            words.Add(link.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(' ', words.Select(QuoteWord));
    }

    /// <summary>Renders the unit file.</summary>
    /// <param name="options">The program and its options.</param>
    /// <returns>The text of the file; lines end with <c>\n</c> and the text ends with one.</returns>
    public static string Render(SystemdUnitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The agent finds its UI and templates next to itself; started through "dotnet agent.dll" the assembly is what counts.
        string program = options.ExecutableArguments.Count > 0 && options.ExecutableArguments[0].StartsWith('/')
            ? options.ExecutableArguments[0]
            : options.ExecutablePath;
        string workingDirectory = Path.GetDirectoryName(program) is { Length: > 0 } directory ? directory : "/";
        var text = new StringBuilder();
        text.Append("# QoffeeAutomat agent. 'qoffeeautomat service install' and package upgrades replace this file,\n");
        text.Append("# so put your own settings in a drop-in: systemctl edit qoffeeautomat\n");
        text.Append('\n');
        text.Append("[Unit]\n");
        text.Append("Description=QoffeeAutomat agent (lets Home Assistant monitor and control this computer)\n");
        text.Append("Documentation=https://github.com/toeck98/QoffeeAutomat\n");
        text.Append("After=network-online.target systemd-logind.service\n");
        text.Append("Wants=network-online.target\n");
        text.Append('\n');
        text.Append("[Service]\n");
        text.Append("Type=notify\n");
        text.Append("NotifyAccess=main\n");
        text.Append("ExecStart=").Append(BuildExecStart(options)).Append('\n');
        text.Append("WorkingDirectory=").Append(QuoteWord(workingDirectory)).Append('\n');
        text.Append("Restart=on-failure\n");
        text.Append("RestartSec=5\n");
        text.Append("WatchdogSec=60\n");
        text.Append("TimeoutStartSec=90\n");
        text.Append("TimeoutStopSec=45\n");
        text.Append("UMask=0077\n");
        if (options.DataDirectory is null)
        {
            // The directories of PLAN.md S05, created and protected by systemd (S09); not wanted for a data directory of the user's choice.
            text.Append("ConfigurationDirectory=").Append(LinuxServiceDefaults.Name).Append('\n');
            text.Append("ConfigurationDirectoryMode=0700\n");
            text.Append("StateDirectory=").Append(LinuxServiceDefaults.Name).Append('\n');
            text.Append("StateDirectoryMode=0700\n");
            text.Append("LogsDirectory=").Append(LinuxServiceDefaults.Name).Append('\n');
            text.Append("LogsDirectoryMode=0700\n");
        }

        text.Append("Environment=DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1\n");
        text.Append('\n');
        text.Append("[Install]\n");
        text.Append("WantedBy=multi-user.target\n");
        return text.ToString();
    }
}
