using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Hub;

/// <summary>
/// The JSON inside an enrollment string (PLAN.md S20, D-026).
/// </summary>
/// <param name="Url">The base URL of the hub, for example <c>https://hub.example.org:8490</c>.</param>
/// <param name="Token">The enrollment token.</param>
/// <param name="Key">The public key that verifies every signed desired state: base64 of a DER SubjectPublicKeyInfo of an ECDSA P-256 key.</param>
/// <param name="Fp">The SHA-256 fingerprint of the hub certificate (upper-case hex bytes separated by colons); absent means normal certificate validation.</param>
public sealed record EnrollmentPayload(Uri Url, string Token, string Key, string? Fp = null)
{
    /// <summary>Hides the token from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the token.</returns>
    public override string ToString() => nameof(EnrollmentPayload) + " { Url = " + Url + " }";
}

/// <summary>Body of <c>POST /hub/v1/enroll</c>.</summary>
/// <param name="Token">The enrollment token.</param>
/// <param name="Agent">The identity of the enrolling agent.</param>
public sealed record EnrollRequest(string Token, AgentInfo Agent)
{
    /// <summary>Hides the token from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the token.</returns>
    public override string ToString() => nameof(EnrollRequest) + " { Agent = " + Agent.Id + " }";
}

/// <summary>The hub in the response of the enrollment endpoint.</summary>
public sealed record EnrollHubInfo(string Id, string Name);

/// <summary>Response of <c>POST /hub/v1/enroll</c>.</summary>
/// <param name="State">One of <see cref="EnrollStates"/>.</param>
/// <param name="Hub">The hub.</param>
/// <param name="Credential">The agent credential; present when <c>State</c> is <c>enrolled</c>.</param>
/// <param name="RetryAfter">Seconds after which a pending agent repeats the request; present when <c>State</c> is <c>pending</c>.</param>
public sealed record EnrollResponse(
    string State,
    EnrollHubInfo Hub,
    string? Credential = null,
    int? RetryAfter = null)
{
    /// <summary>Hides the credential from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the credential.</returns>
    public override string ToString() =>
        nameof(EnrollResponse) + " { State = " + State + ", Hub = " + Hub.Id + " }";
}

/// <summary>Creates and reads enrollment strings: <c>qahub1.&lt;base64url(JSON)&gt;</c> (PLAN.md S20).</summary>
public static class EnrollmentString
{
    /// <summary>Creates the enrollment string of a payload.</summary>
    /// <param name="payload">The payload.</param>
    /// <returns>The string, with the prefix and base64url without padding.</returns>
    public static string Encode(EnrollmentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, HubJsonContext.Default.EnrollmentPayload);
        return HubProtocol.EnrollmentStringPrefix + Base64Url.EncodeToString(json);
    }

    /// <summary>Reads an enrollment string.</summary>
    /// <param name="text">The string the administrator pasted.</param>
    /// <returns>The payload.</returns>
    /// <exception cref="FormatException">The string is malformed: wrong prefix, bad base64url, bad JSON, a URL that is not http or https.</exception>
    public static EnrollmentPayload Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        if (!trimmed.StartsWith(HubProtocol.EnrollmentStringPrefix, StringComparison.Ordinal))
        {
            throw new FormatException("The enrollment string must start with \"" + HubProtocol.EnrollmentStringPrefix + "\".");
        }

        try
        {
            byte[] json = Base64Url.DecodeFromChars(trimmed.AsSpan(HubProtocol.EnrollmentStringPrefix.Length));
            EnrollmentPayload payload = JsonSerializer.Deserialize(json, HubJsonContext.Default.EnrollmentPayload)
                ?? throw new FormatException("The enrollment string is empty.");
            if (!payload.Url.IsAbsoluteUri
                || (payload.Url.Scheme != Uri.UriSchemeHttps && payload.Url.Scheme != Uri.UriSchemeHttp))
            {
                throw new FormatException("The hub URL of the enrollment string must be an http or https URL.");
            }

            return payload;
        }
        catch (JsonException error)
        {
            throw new FormatException("The enrollment string does not contain a valid payload.", error);
        }
    }

    /// <summary>Reads an enrollment string without throwing.</summary>
    /// <param name="text">The string the administrator pasted.</param>
    /// <param name="payload">The payload when the string is valid.</param>
    /// <returns>True when the string is valid.</returns>
    public static bool TryDecode(string? text, [NotNullWhen(true)] out EnrollmentPayload? payload)
    {
        payload = null;
        if (text is null)
        {
            return false;
        }

        try
        {
            payload = Decode(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The pinned certificate fingerprint as hex without the colons, or null.</summary>
    /// <param name="payload">The payload.</param>
    /// <returns>Upper-case hex without separators, or null when the payload has no fingerprint.</returns>
    public static string? FingerprintHex(EnrollmentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.Fp?.Replace(":", string.Empty, StringComparison.Ordinal);
    }
}
