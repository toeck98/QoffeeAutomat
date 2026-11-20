using System.Net;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Hub.Hosting;

/// <summary>
/// The resolved configuration of the hub: the command line wins over the environment, which wins over the defaults
/// (PLAN.md S20 "Listeners", T902). It is created once, when the first service needs it; tests replace it in the service
/// collection.
/// </summary>
/// <param name="Paths">Where the hub keeps its files.</param>
public sealed record HubSettings(HubPaths Paths)
{
    /// <summary>The environment variable of <c>--port</c>.</summary>
    public const string PortEnvironmentVariable = "QOFFEE_HUB_PORT";

    /// <summary>The environment variable of <c>--bind</c>.</summary>
    public const string BindEnvironmentVariable = "QOFFEE_HUB_BIND";

    /// <summary>The environment variable of <c>--name</c>.</summary>
    public const string NameEnvironmentVariable = "QOFFEE_HUB_NAME";

    /// <summary>The environment variable of <c>--tls</c>.</summary>
    public const string TlsEnvironmentVariable = "QOFFEE_HUB_TLS";

    /// <summary>The environment variable of <c>--tls-cert</c>.</summary>
    public const string TlsCertificateEnvironmentVariable = "QOFFEE_HUB_TLS_CERT";

    /// <summary>The environment variable of <c>--tls-key</c>.</summary>
    public const string TlsKeyEnvironmentVariable = "QOFFEE_HUB_TLS_KEY";

    /// <summary>The environment variable of <c>--tls-password-file</c>.</summary>
    public const string TlsPasswordFileEnvironmentVariable = "QOFFEE_HUB_TLS_PASSWORD_FILE";

    /// <summary>The environment variable of <c>--trusted-proxy</c>; a comma separated list.</summary>
    public const string TrustedProxiesEnvironmentVariable = "QOFFEE_HUB_TRUSTED_PROXIES";

    /// <summary>The environment variable of <c>--log-format</c>.</summary>
    public const string LogFormatEnvironmentVariable = "QOFFEE_HUB_LOG_FORMAT";

    /// <summary>The environment variable that the official .NET container images set to <c>true</c>.</summary>
    public const string RunningInContainerEnvironmentVariable = "DOTNET_RUNNING_IN_CONTAINER";

    /// <summary>The environment variable for the optional Home Assistant ingress listener port.</summary>
    public const string IngressPortEnvironmentVariable = "QOFFEE_HUB_INGRESS_PORT";

    /// <summary>The environment variable for the single peer IP address allowed on the ingress listener.</summary>
    public const string IngressAllowedAddressEnvironmentVariable = "QOFFEE_HUB_INGRESS_ALLOWED_ADDRESS";

    /// <summary>The environment variable for the hub role assigned to Home Assistant ingress users.</summary>
    public const string IngressRoleEnvironmentVariable = "QOFFEE_HUB_INGRESS_ROLE";

    /// <summary>The default Home Assistant Supervisor address on the hassio Docker network.</summary>
    public static readonly IPAddress DefaultIngressAllowedAddress = IPAddress.Parse("172.30.32.2");

    private static readonly char[] ProxySeparators = [',', ';', ' '];

    /// <summary>The local address of the listener.</summary>
    public IPAddress Bind { get; init; } = IPAddress.Any;

    /// <summary>The port of the listener; zero picks a free port.</summary>
    public int Port { get; init; } = HubProtocol.DefaultPort;

    /// <summary>How TLS is terminated.</summary>
    public HubTlsMode Tls { get; init; } = HubTlsMode.SelfSigned;

    /// <summary>The certificate file of <see cref="HubTlsMode.Certificate"/>.</summary>
    public string? TlsCertificatePath { get; init; }

    /// <summary>The key file that belongs to a PEM certificate.</summary>
    public string? TlsKeyPath { get; init; }

    /// <summary>The file with the password of a PKCS#12 certificate.</summary>
    public string? TlsPasswordFilePath { get; init; }

    /// <summary>
    /// The reverse proxies whose <c>X-Forwarded-*</c> headers are honoured. Nothing else is: a request from any other address
    /// keeps its own address, scheme and host.
    /// </summary>
    public IReadOnlyList<IPNetwork> TrustedProxies { get; init; } = [];

    /// <summary>The name that a new hub gets; null picks the default.</summary>
    public string? InitialName { get; init; }

    /// <summary>The optional plain-HTTP listener port for Home Assistant ingress; null disables the ingress listener.</summary>
    public int? IngressPort { get; init; }

    /// <summary>The single peer IP address permitted to connect to the ingress listener (default: <c>172.30.32.2</c>).</summary>
    public IPAddress IngressAllowedAddress { get; init; } = DefaultIngressAllowedAddress;

    /// <summary>The hub role assigned to authenticated Home Assistant ingress users.</summary>
    public string IngressRole { get; init; } = Security.HubRoles.Admin;

    /// <summary>Tells whether the process runs in a container.</summary>
    /// <param name="environment">Reads an environment variable.</param>
    /// <returns>True when <see cref="RunningInContainerEnvironmentVariable"/> is <c>true</c>.</returns>
    public static bool IsRunningInContainer(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return string.Equals(environment(RunningInContainerEnvironmentVariable), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Chooses the log format: the option, then the environment, then JSON in a container and text elsewhere.</summary>
    /// <param name="options">The command line.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <returns>The format.</returns>
    public static HubLogFormat ResolveLogFormat(HubOptions options, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (options.LogFormat is { } fromOption)
        {
            return fromOption;
        }

        if (HubCommandLine.TryParseLogFormat(environment(LogFormatEnvironmentVariable), out HubLogFormat fromEnvironment))
        {
            return fromEnvironment;
        }

        return IsRunningInContainer(environment) ? HubLogFormat.Json : HubLogFormat.Text;
    }

    /// <summary>Resolves the settings.</summary>
    /// <param name="options">The command line.</param>
    /// <param name="environment">Reads an environment variable; null or white space counts as not set.</param>
    /// <param name="inContainer">True when the hub runs in a container (the data directory then defaults to <c>/data</c>).</param>
    /// <param name="localAppData">The user's application data folder.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="HubStartupException">A value is invalid or the combination of values is not allowed.</exception>
    public static HubSettings Resolve(HubOptions options, Func<string, string?> environment, bool inContainer, string localAppData)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        string? Env(string name)
        {
            string? value = environment(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        HubPaths paths = HubPaths.Resolve(options.DataDir, Env(HubPaths.DataDirEnvironmentVariable), inContainer, localAppData);

        int port = options.Port ?? HubProtocol.DefaultPort;
        if (options.Port is null && Env(PortEnvironmentVariable) is { } portText && !HubCommandLine.TryParsePort(portText, out port))
        {
            throw new HubStartupException(PortEnvironmentVariable + " must be a number from 0 to 65535.");
        }

        IPAddress bind = IPAddress.Any;
        if ((options.Bind ?? Env(BindEnvironmentVariable)) is { } bindText && !IPAddress.TryParse(bindText, out bind!))
        {
            throw new HubStartupException("The listener address '" + bindText + "' is not an IP address.");
        }

        HubTlsMode tls = options.Tls ?? HubTlsMode.SelfSigned;
        if (options.Tls is null && Env(TlsEnvironmentVariable) is { } tlsText && !HubCommandLine.TryParseTlsMode(tlsText, out tls))
        {
            throw new HubStartupException(TlsEnvironmentVariable + " must be selfsigned, certificate or none.");
        }

        var proxies = new List<IPNetwork>();
        foreach (string text in AllProxyTexts(options, Env(TrustedProxiesEnvironmentVariable)))
        {
            if (!ProxyAddresses.TryParse(text, out IPNetwork network))
            {
                throw new HubStartupException("The trusted proxy '" + text + "' is neither an IP address nor a CIDR range such as 10.0.0.0/8.");
            }

            if (!proxies.Contains(network))
            {
                proxies.Add(network);
            }
        }

        string? certificate = options.TlsCertificate ?? Env(TlsCertificateEnvironmentVariable);
        if (tls == HubTlsMode.Certificate && (certificate is null || !File.Exists(certificate)))
        {
            throw new HubStartupException("--tls certificate needs --tls-cert with the path of an existing certificate file.");
        }

        if (tls == HubTlsMode.None && proxies.Count == 0)
        {
            throw new HubStartupException(
                "--tls none serves plain HTTP and is only safe behind a reverse proxy that terminates TLS. Name the proxy with --trusted-proxy <address>.");
        }

        int? ingressPort = null;
        if (Env(IngressPortEnvironmentVariable) is { } ingressPortText)
        {
            if (!HubCommandLine.TryParsePort(ingressPortText, out int parsedIngressPort) || parsedIngressPort == 0)
            {
                throw new HubStartupException(IngressPortEnvironmentVariable + " must be a number from 1 to 65535.");
            }

            if (port != 0 && parsedIngressPort == port)
            {
                throw new HubStartupException(IngressPortEnvironmentVariable + " must not match " + PortEnvironmentVariable + ".");
            }

            ingressPort = parsedIngressPort;
        }

        IPAddress ingressAllowedAddress = DefaultIngressAllowedAddress;
        if (Env(IngressAllowedAddressEnvironmentVariable) is { } ingressAddressText
            && !IPAddress.TryParse(ingressAddressText, out ingressAllowedAddress!))
        {
            throw new HubStartupException(IngressAllowedAddressEnvironmentVariable + " ('" + ingressAddressText + "') is not a valid IP address.");
        }

        string ingressRole = Security.HubRoles.Admin;
        if (Env(IngressRoleEnvironmentVariable) is { } ingressRoleText)
        {
            if (!Security.HubRoles.IsValid(ingressRoleText))
            {
                throw new HubStartupException(IngressRoleEnvironmentVariable + " must be admin, operator or viewer.");
            }

            ingressRole = ingressRoleText;
        }

        return new HubSettings(paths)
        {
            Bind = bind,
            Port = port,
            Tls = tls,
            TlsCertificatePath = certificate,
            TlsKeyPath = options.TlsKey ?? Env(TlsKeyEnvironmentVariable),
            TlsPasswordFilePath = options.TlsPasswordFile ?? Env(TlsPasswordFileEnvironmentVariable),
            TrustedProxies = proxies,
            InitialName = options.Name ?? Env(NameEnvironmentVariable),
            IngressPort = ingressPort,
            IngressAllowedAddress = ingressAllowedAddress,
            IngressRole = ingressRole,
        };
    }

    private static IEnumerable<string> AllProxyTexts(HubOptions options, string? fromEnvironment)
    {
        foreach (string text in options.TrustedProxies)
        {
            yield return text;
        }

        if (fromEnvironment is not null)
        {
            foreach (string text in fromEnvironment.Split(ProxySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return text;
            }
        }
    }
}
