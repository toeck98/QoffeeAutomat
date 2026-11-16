using System.Text.RegularExpressions;

namespace QoffeeAutomat.Agent.Api.Runtime;

/// <summary>
/// Removes what must never leave the PC from text that goes into the diagnostics bundle (PLAN.md S08, S09, T214):
/// private keys and certificates in PEM form, stored password hashes, bearer tokens and link tokens, and the values
/// of members that are named like a secret. The agent does not log such values, so this is a second line of defence
/// for the log lines of third-party components and for mistakes. A token is recognised by its shape: a run of at
/// least 40 characters of base64 or hex (a link token is 43). Identifiers that are shorter, such as run ids (32
/// hex characters), stay readable.
/// </summary>
public static partial class SecretScrubber
{
    /// <summary>What replaces a removed secret.</summary>
    public const string Replacement = "[redacted]";

    private const int TimeoutMilliseconds = 1000;

    /// <summary>Removes secrets from text.</summary>
    /// <param name="text">The text, for example a log line or a document.</param>
    /// <returns>The text without PEM blocks, password hashes, tokens and values of secret-like members.</returns>
    public static string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string result = PemBlock().Replace(text, "[redacted PEM block]");
        result = PemHeader().Replace(result, "[redacted PEM block]");
        result = PasswordHash().Replace(result, "[redacted password hash]");
        result = BearerToken().Replace(result, "$1" + Replacement);
        result = SecretMember().Replace(result, "$1\"" + Replacement + "\"");
        return TokenLike().Replace(result, Replacement);
    }

    // A PEM block in a JSON log line has its line feeds escaped, so the body is matched as ordinary characters.
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]+-----[\s\S]*?-----END [A-Z0-9 ]+-----", RegexOptions.CultureInvariant, TimeoutMilliseconds)]
    private static partial Regex PemBlock();

    // A block whose end is missing (a truncated line): everything to the end of the line goes.
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]+-----[^\r\n]*", RegexOptions.CultureInvariant, TimeoutMilliseconds)]
    private static partial Regex PemHeader();

    // In a JSON log line the plus sign of base64 is written as +.
    [GeneratedRegex(@"pbkdf2-sha256\$[0-9]+\$(?:[A-Za-z0-9/=]|\+|\\u002[Bb])+\$(?:[A-Za-z0-9/=]|\+|\\u002[Bb])+", RegexOptions.CultureInvariant, TimeoutMilliseconds)]
    private static partial Regex PasswordHash();

    [GeneratedRegex(@"(bearer\s+)[A-Za-z0-9._~+/=\-]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeoutMilliseconds)]
    private static partial Regex BearerToken();

    // "password": "x", password=x, setupToken: x, ...; the member name and the separator stay.
    [GeneratedRegex(
        @"((?:password|passphrase|token|secret|apikey|privatekey|passwordhash)[""']?\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;}]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeoutMilliseconds)]
    private static partial Regex SecretMember();

    [GeneratedRegex(@"[A-Za-z0-9_\-+/=]{40,}", RegexOptions.CultureInvariant, TimeoutMilliseconds)]
    private static partial Regex TokenLike();
}
