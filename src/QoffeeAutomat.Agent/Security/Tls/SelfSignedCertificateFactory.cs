using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace QoffeeAutomat.Agent.Security.Tls;

/// <summary>
/// Creates the self-signed ECDSA P-256 certificate of the link listener (PLAN.md S09 item 3). The
/// subject alternative names are the host name, its short name, <c>&lt;short name&gt;.local</c> and
/// <c>localhost</c>, plus the loopback addresses. Clients do not rely on the names: they pin the SHA-256
/// fingerprint at pairing time, so the certificate may live long.
/// </summary>
public static class SelfSignedCertificateFactory
{
    /// <summary>The validity of a new certificate: ten years.</summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(3650);

    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>Creates a certificate and returns it as a PFX with its private key (no password; the secret store encrypts it).</summary>
    /// <param name="hostName">The machine's host name.</param>
    /// <param name="now">The current time; the certificate is valid from one day earlier.</param>
    /// <param name="validity">How long the certificate stays valid.</param>
    /// <returns>The PFX bytes.</returns>
    public static byte[] CreatePfx(string hostName, DateTimeOffset now, TimeSpan validity)
    {
        ArgumentNullException.ThrowIfNull(hostName);

        string host = NormalizeHostName(hostName);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName("CN=" + host), key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var usages = new OidCollection { new Oid(ServerAuthenticationOid) };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var names = new SubjectAlternativeNameBuilder();
        foreach (string dnsName in DnsNames(host))
        {
            names.AddDnsName(dnsName);
        }

        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());

        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now + validity);
        return certificate.Export(X509ContentType.Pfx);
    }

    /// <summary>Loads a PFX made by <see cref="CreatePfx"/>.</summary>
    /// <param name="pfx">The PFX bytes.</param>
    /// <returns>The certificate with its private key.</returns>
    /// <exception cref="CryptographicException">The bytes are not a valid PFX.</exception>
    public static X509Certificate2 Load(byte[] pfx)
    {
        ArgumentNullException.ThrowIfNull(pfx);

        // Exportable and not ephemeral: the Windows TLS stack (SChannel) cannot use ephemeral keys.
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    private static List<string> DnsNames(string host)
    {
        var names = new List<string> { host };
        int dot = host.IndexOf('.', StringComparison.Ordinal);
        string shortName = dot > 0 ? host[..dot] : host;
        AddUnique(names, shortName);
        AddUnique(names, shortName + ".local");
        AddUnique(names, "localhost");
        return names;
    }

    private static void AddUnique(List<string> names, string name)
    {
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(name);
        }
    }

    /// <summary>Reduces a host name to lower-case ASCII letters, digits, hyphens and dots so it is safe in a subject and in a DNS name.</summary>
    private static string NormalizeHostName(string hostName)
    {
        var builder = new StringBuilder(hostName.Length);
        foreach (char character in hostName.Trim())
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '.')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append('-');
            }
        }

        string normalized = builder.ToString().Trim('.', '-');
        return normalized.Length == 0 ? "localhost" : normalized;
    }
}
