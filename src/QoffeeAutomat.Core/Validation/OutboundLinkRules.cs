using System.Text;

namespace QoffeeAutomat.Core.Validation;

/// <summary>
/// The rules that every outbound QoffeeLink connection follows (ADR 0003, PLAN.md S09): the URL must be
/// <c>wss://</c> (a plain <c>ws://</c> is only accepted for a loopback host, so that tests and development work
/// without a certificate but a credential never travels in the clear over a network), and a pinned certificate
/// is a SHA-256 fingerprint. The reverse link to Home Assistant (T710) and the hub link (T905) share them.
/// </summary>
public static class OutboundLinkRules
{
    /// <summary>The number of hexadecimal digits of a SHA-256 fingerprint.</summary>
    public const int FingerprintDigits = 64;

    /// <summary>Checks the URL of an outbound link.</summary>
    /// <param name="url">The URL; null is reported as missing.</param>
    /// <returns>A message that says what is wrong, or null when the URL may be used.</returns>
    public static string? CheckUrl(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri)
        {
            return "The URL must be an absolute wss:// address.";
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            return "The URL must not contain a user name or a password.";
        }

        if (string.Equals(url.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(url.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase))
        {
            return url.IsLoopback ? null : "ws:// is only allowed for a loopback address; use wss:// so that the credential is encrypted.";
        }

        return "The URL must use wss://.";
    }

    /// <summary>Checks the URL of an outbound link given as text.</summary>
    /// <param name="text">The URL; null or empty is reported as missing.</param>
    /// <returns>A message that says what is wrong, or null when the URL may be used.</returns>
    public static string? CheckUrl(string? text)
    {
        return Uri.TryCreate(text, UriKind.Absolute, out Uri? url) ? CheckUrl(url) : "The URL must be an absolute wss:// address.";
    }

    /// <summary>Normalises a SHA-256 fingerprint to 64 upper-case hexadecimal digits without separators.</summary>
    /// <param name="text">The fingerprint with or without colons, dashes or spaces, in any case.</param>
    /// <param name="fingerprint">The normalised value when the method returns true.</param>
    /// <returns>True when the text is a SHA-256 fingerprint.</returns>
    public static bool TryNormalizeFingerprint(string? text, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var digits = new StringBuilder(FingerprintDigits);
        foreach (char c in text)
        {
            if (c is ':' or '-' or ' ')
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(c) || digits.Length >= FingerprintDigits)
            {
                return false;
            }

            digits.Append(char.ToUpperInvariant(c));
        }

        if (digits.Length != FingerprintDigits)
        {
            return false;
        }

        fingerprint = digits.ToString();
        return true;
    }
}
