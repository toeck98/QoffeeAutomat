using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Link.Outbound;

/// <summary>
/// The enrollment string of the reverse link (ADR 0003): <c>qalr1.&lt;base64url(JSON {"url","token","fp"})&gt;</c>. Home Assistant creates it when
/// the user adds a roaming PC; the agent takes it with <see cref="ReverseLinkManager.JoinAsync"/>. It contains the credential, so it is
/// handled like a password: it is never logged and <see cref="ToString"/> does not show it.
/// </summary>
/// <param name="Url">The <c>wss://</c> URL of the endpoint of the integration.</param>
/// <param name="Token">The reverse-link token, <c>qalr_</c> and 43 base64url characters.</param>
/// <param name="Fingerprint">The SHA-256 fingerprint of the certificate to accept (64 upper-case hexadecimal digits), or null for the normal validation.</param>
public sealed record ReverseLinkEnrollment(Uri Url, string Token, string? Fingerprint)
{
    /// <summary>The start of every enrollment string; the digit is the version of the format.</summary>
    public const string Prefix = "qalr1.";

    /// <summary>The start of every token.</summary>
    public const string TokenPrefix = "qalr_";

    private const int TokenLength = 5 + 43;
    private const int MaxStringLength = 4096;

    /// <summary>Reads an enrollment string.</summary>
    /// <param name="text">The string as Home Assistant shows it; surrounding white space is ignored.</param>
    /// <param name="enrollment">The enrollment when the method returns true.</param>
    /// <param name="error">What is wrong when the method returns false; never contains the token.</param>
    /// <returns>True when the string is valid.</returns>
    public static bool TryParse(string? text, out ReverseLinkEnrollment? enrollment, out string? error)
    {
        enrollment = null;
        string trimmed = text?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "The enrollment string must start with " + Prefix + ".";
            return false;
        }

        if (trimmed.Length > MaxStringLength)
        {
            error = "The enrollment string is too long.";
            return false;
        }

        byte[] json;
        try
        {
            json = Base64Url.DecodeFromChars(trimmed.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            error = "The enrollment string is damaged (it is not base64url).";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return TryRead(document.RootElement, out enrollment, out error);
        }
        catch (JsonException)
        {
            error = "The enrollment string is damaged (it does not contain JSON).";
            return false;
        }
    }

    /// <summary>Writes the enrollment string.</summary>
    /// <returns>The string; it contains the token.</returns>
    public string ToEnrollmentString()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("url", Url.AbsoluteUri);
            writer.WriteString("token", Token);
            if (Fingerprint is not null)
            {
                writer.WriteString("fp", Fingerprint);
            }

            writer.WriteEndObject();
        }

        return Prefix + Base64Url.EncodeToString(stream.ToArray());
    }

    /// <summary>Describes the enrollment without the token.</summary>
    /// <returns>The description.</returns>
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"ReverseLinkEnrollment {{ Url = {Url.GetLeftPart(UriPartial.Path)}, Pinned = {Fingerprint is not null} }}");
    }

    private static bool TryRead(JsonElement root, out ReverseLinkEnrollment? enrollment, out string? error)
    {
        enrollment = null;
        if (root.ValueKind != JsonValueKind.Object
            || !TryString(root, "url", out string? urlText)
            || !TryString(root, "token", out string? token))
        {
            error = "The enrollment string must contain a url and a token.";
            return false;
        }

        if (OutboundLinkRules.CheckUrl(urlText) is { } urlProblem)
        {
            error = urlProblem;
            return false;
        }

        if (token!.Length != TokenLength || !token.StartsWith(TokenPrefix, StringComparison.Ordinal) || !IsBase64Url(token.AsSpan(TokenPrefix.Length)))
        {
            error = "The token in the enrollment string is not valid.";
            return false;
        }

        string? fingerprint = null;
        if (TryString(root, "fp", out string? fpText))
        {
            if (!OutboundLinkRules.TryNormalizeFingerprint(fpText, out string normalized))
            {
                error = "The fingerprint in the enrollment string is not a SHA-256 fingerprint.";
                return false;
            }

            fingerprint = normalized;
        }

        enrollment = new ReverseLinkEnrollment(new Uri(urlText!, UriKind.Absolute), token, fingerprint);
        error = null;
        return true;
    }

    private static bool TryString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrEmpty(value);
    }

    private static bool IsBase64Url(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Creates a token the way Home Assistant does, for tests and tools.</summary>
    /// <param name="random">32 random bytes.</param>
    /// <returns>The token.</returns>
    public static string CreateToken(ReadOnlySpan<byte> random)
    {
        if (random.Length != 32)
        {
            throw new ArgumentException("A token has 32 random bytes.", nameof(random));
        }

        return TokenPrefix + Base64Url.EncodeToString(random);
    }
}
