using System.Net;
using System.Net.Sockets;

namespace QoffeeAutomat.Hub.Hosting;

/// <summary>Parsing and matching of the addresses of trusted reverse proxies.</summary>
public static class ProxyAddresses
{
    /// <summary>Parses a proxy: a single IP address (IPv4 or IPv6) or a CIDR range such as <c>172.17.0.0/16</c>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="network">The network; a single address becomes a network of one address.</param>
    /// <returns>True when the text is valid.</returns>
    public static bool TryParse(string? text, out IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            int slash = trimmed.IndexOf('/');
            if (!IPNetwork.TryParse(trimmed, out network)
                || !IPAddress.TryParse(trimmed[..slash], out IPAddress? rawAddress)
                || !Normalize(rawAddress).Equals(network.BaseAddress))
            {
                network = default;
                return false;
            }

            return true;
        }

        if (!IPAddress.TryParse(trimmed, out IPAddress? address))
        {
            return false;
        }

        address = Normalize(address);
        network = new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }

    /// <summary>Tells whether an address belongs to one of the networks.</summary>
    /// <param name="networks">The trusted networks.</param>
    /// <param name="address">The address of a peer.</param>
    /// <returns>True when a network contains the address; an IPv4 address mapped into IPv6 counts as the IPv4 address.</returns>
    public static bool Contains(IReadOnlyList<IPNetwork> networks, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(networks);
        ArgumentNullException.ThrowIfNull(address);

        IPAddress normalized = Normalize(address);
        foreach (IPNetwork network in networks)
        {
            if (network.Contains(normalized))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Turns an IPv4 address that is mapped into IPv6 (<c>::ffff:a.b.c.d</c>) into the IPv4 address.</summary>
    /// <param name="address">The address.</param>
    /// <returns>The normalized address.</returns>
    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}

/// <summary>
/// Honours the <c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c> and <c>X-Forwarded-Host</c> headers, but only on requests that
/// come from a configured reverse proxy (PLAN.md S20, T902). A request from any other address keeps its own address, scheme and
/// host, so a client cannot choose the address that the login rate limit and the audit log see, and cannot make a plain request look
/// secure. With no proxy configured the middleware does nothing. The client address is the first address, from the right, that is not
/// itself a trusted proxy.
/// </summary>
public sealed class TrustedProxyMiddleware
{
    private const int MaxForwardedEntries = 16;
    private const int MaxHeaderLength = 2048;
    private const int MaxHostLength = 255;

    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<IPNetwork> _proxies;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="settings">The settings with the trusted proxies.</param>
    public TrustedProxyMiddleware(RequestDelegate next, HubSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _next = next;
        _proxies = settings.TrustedProxies;
    }

    /// <summary>Handles a request.</summary>
    /// <param name="context">The request context.</param>
    /// <returns>A task that completes when the request is handled.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IPAddress? peer = context.Connection.RemoteIpAddress;
        if (_proxies.Count > 0 && peer is not null && ProxyAddresses.Contains(_proxies, peer))
        {
            Apply(context);
        }

        return _next(context);
    }

    private static bool IsValidHost(string host)
    {
        if (host.Length is 0 or > MaxHostLength)
        {
            return false;
        }

        foreach (char character in host)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or ':' or '[' or ']'))
            {
                return false;
            }
        }

        return true;
    }

    private static string LastValue(string header)
    {
        if (header.Length > MaxHeaderLength)
        {
            return string.Empty;
        }

        int comma = header.LastIndexOf(',');
        return (comma < 0 ? header : header[(comma + 1)..]).Trim();
    }

    private static bool TryParseAddress(string entry, out IPAddress address)
    {
        address = IPAddress.None;
        string value = entry;
        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            value = value[1..close];
        }
        else if (value.Count(static character => character == ':') == 1)
        {
            // IPv4 with a port.
            value = value[..value.IndexOf(':')];
        }

        if (!(value.Contains('.') || value.Contains(':')) || !IPAddress.TryParse(value, out IPAddress? parsed))
        {
            return false;
        }

        address = ProxyAddresses.Normalize(parsed);
        return true;
    }

    private void Apply(HttpContext context)
    {
        IHeaderDictionary headers = context.Request.Headers;

        string forwardedFor = headers["X-Forwarded-For"].ToString();
        if (forwardedFor.Length is > 0 and <= MaxHeaderLength && ClientAddress(forwardedFor) is { } client)
        {
            context.Connection.RemoteIpAddress = client;
        }

        string proto = LastValue(headers["X-Forwarded-Proto"].ToString());
        if (string.Equals(proto, "https", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Scheme = "https";
        }
        else if (string.Equals(proto, "http", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Scheme = "http";
        }

        string host = LastValue(headers["X-Forwarded-Host"].ToString());
        if (IsValidHost(host))
        {
            context.Request.Host = new HostString(host);
        }
    }

    private IPAddress? ClientAddress(string forwardedFor)
    {
        string[] entries = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IPAddress? candidate = null;
        int lowest = Math.Max(0, entries.Length - MaxForwardedEntries);
        for (int index = entries.Length - 1; index >= lowest; index--)
        {
            if (!TryParseAddress(entries[index], out IPAddress address))
            {
                // A malformed entry: nothing in the header can be trusted.
                return null;
            }

            candidate = address;
            if (!ProxyAddresses.Contains(_proxies, address))
            {
                return address;
            }
        }

        return candidate;
    }
}
