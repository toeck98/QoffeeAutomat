using System.Globalization;
using System.Security.Cryptography;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Hub;

/// <summary>
/// The enrollment string of the hub, read and checked (PLAN.md S20, D-026): <c>qahub1.&lt;base64url({"url","token","fp","key"})&gt;</c>. It carries
/// the address of the hub, the enrollment token, the SHA-256 fingerprint of the hub certificate to pin and the public key that verifies every desired
/// state. It contains a token, so it is handled like a password: <see cref="ToString"/> and every message of <see cref="TryParse"/> leave it out.
/// </summary>
/// <param name="Url">The base address of the hub, <c>https://</c> (or <c>http://</c> for a loopback host); without credentials, query and fragment, and without a trailing slash.</param>
/// <param name="Token">The enrollment token, 16 to 128 base64url characters.</param>
/// <param name="PublicKey">The signing key of the hub: base64 of a DER SubjectPublicKeyInfo of an ECDSA P-256 key.</param>
/// <param name="Fingerprint">The SHA-256 fingerprint of the hub certificate to accept (64 upper-case hexadecimal digits), or null for the normal certificate validation.</param>
public sealed record HubEnrollment(Uri Url, string Token, string PublicKey, string? Fingerprint)
{
    /// <summary>The longest enrollment string that is read.</summary>
    public const int MaxStringLength = 4096;

    private const int MinTokenLength = 16;
    private const int MaxTokenLength = 128;

    /// <summary>The address of <c>POST /hub/v1/enroll</c>.</summary>
    public Uri EnrollUri => HubUrls.Combine(Url, HubProtocol.EnrollPath, webSocket: false);

    /// <summary>The <c>wss://</c> (or <c>ws://</c>) address of the agent link.</summary>
    public Uri LinkUri => HubUrls.Combine(Url, HubProtocol.AgentLinkPath, webSocket: true);

    /// <summary>
    /// Reads an enrollment string. The rule for the address: <c>https://</c> always; <c>http://</c> only for a loopback host, so that tests and a
    /// hub on the same PC work without a certificate but the token and the credential never travel unencrypted over a network.
    /// </summary>
    /// <param name="text">The string as the hub shows it; surrounding white space is ignored.</param>
    /// <param name="enrollment">The enrollment when the method returns true.</param>
    /// <param name="error">What is wrong when the method returns false; never contains the token.</param>
    /// <returns>True when the string is valid.</returns>
    public static bool TryParse(string? text, out HubEnrollment? enrollment, out string? error)
    {
        enrollment = null;
        string trimmed = text?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith(HubProtocol.EnrollmentStringPrefix, StringComparison.Ordinal))
        {
            error = "The enrollment string must start with " + HubProtocol.EnrollmentStringPrefix + ".";
            return false;
        }

        if (trimmed.Length > MaxStringLength)
        {
            error = "The enrollment string is too long.";
            return false;
        }

        EnrollmentPayload payload;
        try
        {
            payload = EnrollmentString.Decode(trimmed);
        }
        catch (FormatException)
        {
            error = "The enrollment string is damaged: it does not contain a valid payload.";
            return false;
        }

        if (HubUrls.CheckBaseUrl(payload.Url) is { } urlProblem)
        {
            error = urlProblem;
            return false;
        }

        if (!IsToken(payload.Token))
        {
            error = "The token in the enrollment string is not valid.";
            return false;
        }

        if (!HubSigningKey.TryImportPublicKey(payload.Key, out ECDsa? key))
        {
            error = "The signing key in the enrollment string is not an ECDSA P-256 public key.";
            return false;
        }

        key.Dispose();

        string? fingerprint = null;
        if (payload.Fp is not null)
        {
            if (!OutboundLinkRules.TryNormalizeFingerprint(payload.Fp, out string normalized))
            {
                error = "The fingerprint in the enrollment string is not a SHA-256 fingerprint.";
                return false;
            }

            fingerprint = normalized;
        }

        enrollment = new HubEnrollment(HubUrls.Normalize(payload.Url), payload.Token, payload.Key, fingerprint);
        error = null;
        return true;
    }

    /// <summary>Describes the enrollment without the token.</summary>
    /// <returns>The description.</returns>
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"HubEnrollment {{ Url = {Url}, Pinned = {Fingerprint is not null} }}");
    }

    private static bool IsToken(string? token)
    {
        if (token is null || token.Length is < MinTokenLength or > MaxTokenLength)
        {
            return false;
        }

        foreach (char c in token)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Address rules of the hub link: the base address of a hub and the addresses derived from it.</summary>
public static class HubUrls
{
    /// <summary>Checks the base address of a hub.</summary>
    /// <param name="url">The address from the enrollment string or from the stored state.</param>
    /// <returns>A message that says what is wrong, or null when the address may be used.</returns>
    public static string? CheckBaseUrl(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri)
        {
            return "The hub address must be an absolute https:// address.";
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            return "The hub address must not contain a user name or a password.";
        }

        if (!string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
        {
            return "The hub address must not contain a query or a fragment.";
        }

        if (string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return url.IsLoopback ? null : "http:// is only allowed for a loopback address; use https:// so that the token and the credential are encrypted.";
        }

        return "The hub address must use https://.";
    }

    /// <summary>The address without a trailing slash, so that paths can be appended.</summary>
    /// <param name="url">A valid base address.</param>
    /// <returns>The normalised address.</returns>
    public static Uri Normalize(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var builder = new UriBuilder(url) { Query = string.Empty, Fragment = string.Empty };
        builder.Path = builder.Path.TrimEnd('/');
        return builder.Uri;
    }

    /// <summary>Appends a path to the base address of the hub.</summary>
    /// <param name="baseUrl">The base address.</param>
    /// <param name="path">The path, starting with a slash, for example <see cref="HubProtocol.EnrollPath"/>.</param>
    /// <param name="webSocket">True for the <c>wss://</c> (<c>ws://</c>) form of the address.</param>
    /// <returns>The address.</returns>
    public static Uri Combine(Uri baseUrl, string path, bool webSocket)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var builder = new UriBuilder(baseUrl)
        {
            Path = baseUrl.AbsolutePath.TrimEnd('/') + path,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        if (webSocket)
        {
            builder.Scheme = string.Equals(baseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? Uri.UriSchemeWss : Uri.UriSchemeWs;
        }

        return builder.Uri;
    }
}

/// <summary>The form of an agent credential (PLAN.md S20): 32 random bytes as base64url without padding.</summary>
public static class HubCredentialRules
{
    /// <summary>The number of characters of a credential.</summary>
    public const int Length = 43;

    /// <summary>Tells whether a string has the form of a credential.</summary>
    /// <param name="credential">The candidate.</param>
    /// <returns>True when it is 43 base64url characters.</returns>
    public static bool IsValid(string? credential)
    {
        if (credential is null || credential.Length != Length)
        {
            return false;
        }

        foreach (char c in credential)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
