using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Hub;

/// <summary>
/// Hashes of hub documents (PLAN.md S20): the SHA-256 of the canonical JSON of the body, for scripts of the UTF-8
/// text. The hash is written as 64 lower-case hex characters. The canonical JSON is the one of the manifest revision
/// (<see cref="CanonicalJson"/>).
/// </summary>
public static class DocumentHasher
{
    /// <summary>Hashes a JSON body: the SHA-256 of its canonical JSON.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The hash, 64 lower-case hex characters.</returns>
    public static string HashJson(JsonElement body)
    {
        return Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.SerializeToUtf8(body)));
    }

    /// <summary>Hashes the text of a script: the SHA-256 of its UTF-8 bytes.</summary>
    /// <param name="text">The script text.</param>
    /// <returns>The hash, 64 lower-case hex characters.</returns>
    public static string HashScript(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Hashes a document body according to its kind.</summary>
    /// <param name="kind">The document kind, see <see cref="DocumentKinds"/>.</param>
    /// <param name="body">A JSON string for scripts, a JSON object for every other kind.</param>
    /// <returns>The hash, 64 lower-case hex characters.</returns>
    /// <exception cref="ArgumentException">The body does not have the form its kind requires.</exception>
    public static string Hash(string kind, JsonElement body)
    {
        ArgumentNullException.ThrowIfNull(kind);

        if (string.Equals(kind, DocumentKinds.Script, StringComparison.Ordinal))
        {
            if (body.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("The body of a script must be a JSON string.", nameof(body));
            }

            return HashScript(body.GetString()!);
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The body of a " + kind + " document must be a JSON object.", nameof(body));
        }

        return HashJson(body);
    }

    /// <summary>Hashes the body of a document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The hash, 64 lower-case hex characters.</returns>
    public static string Hash(HubDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Hash(document.Kind, document.Body);
    }

    /// <summary>Whether the hash of a document equals the hash of its body.</summary>
    /// <param name="document">The document.</param>
    /// <returns>True when the hash is correct; false also when the body has the wrong form for its kind.</returns>
    public static bool Matches(HubDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        try
        {
            return string.Equals(Hash(document), document.Hash, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Creates the item of the signed item list for a document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The item with the computed hash.</returns>
    public static DesiredItem ToItem(HubDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new DesiredItem(document.Kind, document.Id, Hash(document));
    }

    /// <summary>Creates a document with the computed hash.</summary>
    /// <param name="kind">The document kind.</param>
    /// <param name="id">The document id.</param>
    /// <param name="body">The body.</param>
    /// <returns>The document.</returns>
    public static HubDocument CreateDocument(string kind, string id, JsonElement body)
    {
        return new HubDocument(kind, id, Hash(kind, body), body);
    }
}
