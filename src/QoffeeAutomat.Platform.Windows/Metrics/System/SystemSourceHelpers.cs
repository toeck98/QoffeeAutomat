using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace QoffeeAutomat.Platform.Windows.Metrics.SystemMetrics;

/// <summary>Decides which network adapters count as physical (PLAN.md S06, T304): wired and wireless adapters, no virtual switches, VPNs or tunnels.</summary>
public static class NetworkAdapterFilter
{
    // Words in the name or description that mark software adapters. Hyper-V, VMware, VirtualBox, WSL, Docker, VPN clients
    // and the virtual adapters of Windows itself (Wi-Fi Direct, hosted network) all show up as Ethernet adapters.
    private static readonly string[] SoftwareMarkers =
    [
        "virtual",
        "vethernet",
        "vmware",
        "vbox",
        "hyper-v",
        "docker",
        "vpn",
        "tap-",
        "tap windows",
        "tunnel",
        "loopback",
        "bluetooth",
        "pseudo",
        "miniport",
        "wi-fi direct",
        "npcap",
        "wireguard",
        "zerotier",
        "tailscale",
    ];

    /// <summary>Tells whether an adapter is a physical network card.</summary>
    /// <param name="type">The interface type that Windows reports.</param>
    /// <param name="name">The name of the adapter, for example <c>Ethernet</c>.</param>
    /// <param name="description">The description, for example the name of the card.</param>
    /// <returns>True for wired, wireless and mobile broadband adapters that are not recognisably software.</returns>
    public static bool IsPhysical(NetworkInterfaceType type, string name, string description)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);

        bool physicalType = type is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Ethernet3Megabit
            or NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.Wwanpp
            or NetworkInterfaceType.Wwanpp2;
        if (!physicalType)
        {
            return false;
        }

        string text = name + " " + description;
        return !SoftwareMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>What <see cref="NetworkAddressSelector"/> needs to know about an adapter.</summary>
/// <param name="IsUp">The adapter is operational.</param>
/// <param name="IsPhysical">See <see cref="NetworkAdapterFilter.IsPhysical"/>.</param>
/// <param name="IsLoopbackOrTunnel">The adapter is the loopback or a tunnel.</param>
/// <param name="HasGateway">The adapter has an IPv4 default gateway, which means that it leads to the network.</param>
/// <param name="IPv4Addresses">Its IPv4 addresses in dotted notation.</param>
public sealed record AdapterAddresses(bool IsUp, bool IsPhysical, bool IsLoopbackOrTunnel, bool HasGateway, IReadOnlyList<string> IPv4Addresses);

/// <summary>Chooses the primary IPv4 address of the computer (PLAN.md S06, T304) without sending a single packet.</summary>
public static class NetworkAddressSelector
{
    /// <summary>
    /// Picks the address of the adapter that leads to the network: operational adapters with a usable address (not loopback, not
    /// link-local, not unspecified); those with a default gateway first, then physical adapters, then the order of the list.
    /// </summary>
    /// <param name="adapters">The adapters in the order that Windows lists them.</param>
    /// <returns>The address; null when there is no usable one.</returns>
    public static string? SelectPrimary(IEnumerable<AdapterAddresses> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        return adapters
            .Where(adapter => adapter.IsUp && !adapter.IsLoopbackOrTunnel)
            .SelectMany(adapter => adapter.IPv4Addresses.Where(IsUsable).Select(address => (Adapter: adapter, Address: address)))
            .OrderByDescending(candidate => candidate.Adapter.HasGateway)
            .ThenByDescending(candidate => candidate.Adapter.IsPhysical)
            .Select(candidate => candidate.Address)
            .FirstOrDefault();
    }

    private static bool IsUsable(string text)
    {
        if (!IPAddress.TryParse(text, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        bool linkLocal = bytes[0] == 169 && bytes[1] == 254;
        return !IPAddress.IsLoopback(address) && !linkLocal && !address.Equals(IPAddress.Any);
    }
}

/// <summary>Formats the description of the operating system (PLAN.md S06, T304).</summary>
public static class OsVersionFormatter
{
    private const int FirstWindows11Build = 22000;
    private const string Windows10Prefix = "Windows 10";

    /// <summary>
    /// Builds a text like <c>Windows 11 Pro 24H2 (10.0.26100.4061)</c> from the values of
    /// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion</c>. Windows 11 still announces itself as "Windows 10" in
    /// <c>ProductName</c>, so builds from 22000 on are renamed.
    /// </summary>
    /// <param name="productName">The <c>ProductName</c> value, for example <c>Windows 10 Pro</c>.</param>
    /// <param name="displayVersion">The <c>DisplayVersion</c> value, for example <c>24H2</c>; may be empty.</param>
    /// <param name="build">The build number (<c>CurrentBuildNumber</c>); null when unknown.</param>
    /// <param name="revision">The update build revision (<c>UBR</c>); null when unknown.</param>
    /// <param name="fallbackVersion">The version to show when the build number is unknown.</param>
    /// <returns>The description.</returns>
    public static string Format(string? productName, string? displayVersion, int? build, int? revision, Version fallbackVersion)
    {
        ArgumentNullException.ThrowIfNull(fallbackVersion);

        string name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
        if (build is >= FirstWindows11Build && name.StartsWith(Windows10Prefix, StringComparison.Ordinal))
        {
            name = "Windows 11" + name[Windows10Prefix.Length..];
        }

        string release = string.IsNullOrWhiteSpace(displayVersion) ? string.Empty : " " + displayVersion.Trim();
        string number = build is { } buildNumber
            ? string.Create(CultureInfo.InvariantCulture, $"10.0.{buildNumber}") + (revision is { } update ? string.Create(CultureInfo.InvariantCulture, $".{update}") : string.Empty)
            : fallbackVersion.ToString(3);
        return $"{name}{release} ({number})";
    }
}
