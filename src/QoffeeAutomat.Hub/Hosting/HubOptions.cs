using System.Globalization;

namespace QoffeeAutomat.Hub.Hosting;

/// <summary>How the hub terminates TLS (PLAN.md S20, "Listeners").</summary>
public enum HubTlsMode
{
    /// <summary>The hub creates a self-signed ECDSA P-256 certificate on first start and keeps it in the secret store (default).</summary>
    SelfSigned,

    /// <summary>The operator supplies a certificate: a PKCS#12 file, or a PEM certificate with its key.</summary>
    Certificate,

    /// <summary>Plain HTTP. Only allowed behind a trusted reverse proxy, which terminates TLS.</summary>
    None,
}

/// <summary>The format of the log lines on the console.</summary>
public enum HubLogFormat
{
    /// <summary>One readable line per message.</summary>
    Text,

    /// <summary>One JSON object per message (structured logging for log collectors); the default in a container.</summary>
    Json,
}

/// <summary>The parsed command line of the hub. Values that are null fall back to the environment and then to the defaults, see <see cref="HubSettings"/>.</summary>
public sealed record HubOptions
{
    /// <summary>The value of <c>--data-dir</c>.</summary>
    public string? DataDir { get; init; }

    /// <summary>The value of <c>--port</c>; zero picks a free port (tests).</summary>
    public int? Port { get; init; }

    /// <summary>The value of <c>--bind</c>: the local address to listen on.</summary>
    public string? Bind { get; init; }

    /// <summary>The value of <c>--name</c>: the name of a new hub. A hub that already has a name keeps it.</summary>
    public string? Name { get; init; }

    /// <summary>The value of <c>--tls</c>.</summary>
    public HubTlsMode? Tls { get; init; }

    /// <summary>The value of <c>--tls-cert</c>: the PKCS#12 file or the PEM certificate.</summary>
    public string? TlsCertificate { get; init; }

    /// <summary>The value of <c>--tls-key</c>: the PEM private key that belongs to a PEM certificate.</summary>
    public string? TlsKey { get; init; }

    /// <summary>The value of <c>--tls-password-file</c>: a file that holds the password of a PKCS#12 file.</summary>
    public string? TlsPasswordFile { get; init; }

    /// <summary>The values of <c>--trusted-proxy</c> (repeatable): addresses or CIDR ranges of reverse proxies whose forwarded headers are honoured.</summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];

    /// <summary>The value of <c>--log-format</c>.</summary>
    public HubLogFormat? LogFormat { get; init; }

    /// <summary>True for <c>--help</c>.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>True for <c>--version</c>.</summary>
    public bool ShowVersion { get; init; }

    /// <summary>What is wrong with the command line, or null.</summary>
    public string? Error { get; init; }

    /// <summary>Arguments that the hub does not know; they go to ASP.NET Core (<c>--environment</c>, <c>--contentRoot</c> and so on).</summary>
    public IReadOnlyList<string> PassThrough { get; init; } = [];
}

/// <summary>Parses the command line of the hub: <c>qoffeeautomat-hub [options]</c>.</summary>
public static class HubCommandLine
{
    /// <summary>The help text.</summary>
    public const string Usage = """
        QoffeeAutomat hub

        Usage: qoffeeautomat-hub [options]

        Options (each also exists as an environment variable, the option wins):
          --data-dir <path>           Data directory.            QOFFEE_HUB_DATA_DIR (default /data in the container)
          --port <n>                  Listener port.             QOFFEE_HUB_PORT (default 8490)
          --bind <address>            Listener address.          QOFFEE_HUB_BIND (default 0.0.0.0)
          --name <text>               Name of a new hub.         QOFFEE_HUB_NAME
          --tls <mode>                selfsigned | certificate | none.  QOFFEE_HUB_TLS (default selfsigned)
          --tls-cert <file>           Certificate (PKCS#12 or PEM).     QOFFEE_HUB_TLS_CERT
          --tls-key <file>            Private key of a PEM certificate. QOFFEE_HUB_TLS_KEY
          --tls-password-file <file>  Password of a PKCS#12 file.       QOFFEE_HUB_TLS_PASSWORD_FILE
          --trusted-proxy <address>   Reverse proxy whose X-Forwarded-* headers are trusted (repeatable;
                                      an address or a CIDR range).      QOFFEE_HUB_TRUSTED_PROXIES (comma separated)
          --log-format <text|json>    Console log format.        QOFFEE_HUB_LOG_FORMAT (default json in a container)
          --version                   Print the version.
          --help                      Print this text.

        With --tls none the hub speaks plain HTTP and needs at least one --trusted-proxy.
        """;

    /// <summary>Parses the arguments.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The options; <see cref="HubOptions.Error"/> is set when the command line is wrong.</returns>
    public static HubOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? dataDir = null;
        int? port = null;
        string? bind = null;
        string? name = null;
        HubTlsMode? tls = null;
        string? tlsCertificate = null;
        string? tlsKey = null;
        string? tlsPasswordFile = null;
        HubLogFormat? logFormat = null;
        bool showHelp = false;
        bool showVersion = false;
        var proxies = new List<string>();
        var passThrough = new List<string>();

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];
            (string option, string? inline) = Split(argument);

            switch (option)
            {
                case "--help" or "-h" or "-?":
                    showHelp = true;
                    continue;
                case "--version":
                    showVersion = true;
                    continue;
                case "--data-dir" or "--port" or "--bind" or "--name" or "--tls" or "--tls-cert" or "--tls-key"
                    or "--tls-password-file" or "--trusted-proxy" or "--log-format":
                    break;
                default:
                    passThrough.Add(argument);
                    if (inline is null && index + 1 < args.Count && !args[index + 1].StartsWith('-'))
                    {
                        index++;
                        passThrough.Add(args[index]);
                    }

                    continue;
            }

            string? value = inline;
            if (value is null)
            {
                if (index + 1 >= args.Count)
                {
                    return new HubOptions { Error = option + " needs a value." };
                }

                index++;
                value = args[index];
            }

            switch (option)
            {
                case "--data-dir":
                    dataDir = value;
                    break;
                case "--port":
                    if (!TryParsePort(value, out int parsedPort))
                    {
                        return new HubOptions { Error = "--port must be a number from 0 to 65535." };
                    }

                    port = parsedPort;
                    break;
                case "--bind":
                    bind = value;
                    break;
                case "--name":
                    name = value;
                    break;
                case "--tls":
                    if (!TryParseTlsMode(value, out HubTlsMode parsedTls))
                    {
                        return new HubOptions { Error = "--tls must be selfsigned, certificate or none." };
                    }

                    tls = parsedTls;
                    break;
                case "--tls-cert":
                    tlsCertificate = value;
                    break;
                case "--tls-key":
                    tlsKey = value;
                    break;
                case "--tls-password-file":
                    tlsPasswordFile = value;
                    break;
                case "--trusted-proxy":
                    proxies.Add(value);
                    break;
                default:
                    if (!TryParseLogFormat(value, out HubLogFormat parsedFormat))
                    {
                        return new HubOptions { Error = "--log-format must be text or json." };
                    }

                    logFormat = parsedFormat;
                    break;
            }
        }

        return new HubOptions
        {
            DataDir = dataDir,
            Port = port,
            Bind = bind,
            Name = name,
            Tls = tls,
            TlsCertificate = tlsCertificate,
            TlsKey = tlsKey,
            TlsPasswordFile = tlsPasswordFile,
            TrustedProxies = proxies,
            LogFormat = logFormat,
            ShowHelp = showHelp,
            ShowVersion = showVersion,
            PassThrough = passThrough,
        };
    }

    /// <summary>Parses a port number.</summary>
    /// <param name="text">The text.</param>
    /// <param name="port">The port.</param>
    /// <returns>True when the text is a number from 0 to 65535.</returns>
    public static bool TryParsePort(string? text, out int port)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 0 and <= 65535;
    }

    /// <summary>Parses a TLS mode name.</summary>
    /// <param name="text">The text: <c>selfsigned</c>, <c>certificate</c> or <c>none</c>, in any case.</param>
    /// <param name="mode">The mode.</param>
    /// <returns>True when the text names a mode.</returns>
    public static bool TryParseTlsMode(string? text, out HubTlsMode mode)
    {
        switch (text?.Trim().ToUpperInvariant())
        {
            case "SELFSIGNED" or "SELF-SIGNED":
                mode = HubTlsMode.SelfSigned;
                return true;
            case "CERTIFICATE" or "CERT":
                mode = HubTlsMode.Certificate;
                return true;
            case "NONE" or "HTTP":
                mode = HubTlsMode.None;
                return true;
            default:
                mode = HubTlsMode.SelfSigned;
                return false;
        }
    }

    /// <summary>Parses a log format name.</summary>
    /// <param name="text">The text: <c>text</c> or <c>json</c>, in any case.</param>
    /// <param name="format">The format.</param>
    /// <returns>True when the text names a format.</returns>
    public static bool TryParseLogFormat(string? text, out HubLogFormat format)
    {
        switch (text?.Trim().ToUpperInvariant())
        {
            case "TEXT":
                format = HubLogFormat.Text;
                return true;
            case "JSON":
                format = HubLogFormat.Json;
                return true;
            default:
                format = HubLogFormat.Text;
                return false;
        }
    }

    private static (string Option, string? Inline) Split(string argument)
    {
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            return (argument, null);
        }

        int equals = argument.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? (argument, null) : (argument[..equals], argument[(equals + 1)..]);
    }
}
