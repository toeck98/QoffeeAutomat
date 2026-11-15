using System.Security.Cryptography;
using System.Text.Json;

namespace QoffeeAutomat.Contracts.Link;

/// <summary>
/// Computes the manifest revision <c>rev</c>: the first 12 hex characters of the SHA-256 of the
/// canonical manifest JSON without the <c>rev</c> member (PLAN.md S07, <see cref="CanonicalJson"/>).
/// </summary>
public static class ManifestRevision
{
    /// <summary>Number of hex characters in a revision.</summary>
    public const int Length = 12;

    /// <summary>Computes the revision of a manifest. The current value of <c>Rev</c> is ignored.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The revision, 12 lower-case hex characters.</returns>
    public static string Compute(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        JsonElement element = JsonSerializer.SerializeToElement(manifest, LinkJsonContext.Default.Manifest);
        return Compute(element);
    }

    /// <summary>Computes the revision of a manifest in JSON form. A <c>rev</c> member is ignored.</summary>
    /// <param name="manifest">The manifest as a JSON object.</param>
    /// <returns>The revision, 12 lower-case hex characters.</returns>
    public static string Compute(JsonElement manifest)
    {
        byte[] canonical = CanonicalJson.SerializeToUtf8(manifest, "rev");
        byte[] hash = SHA256.HashData(canonical);
        return Convert.ToHexStringLower(hash)[..Length];
    }

    /// <summary>Returns the manifest with its <c>Rev</c> set to the computed revision.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>A copy with the correct revision.</returns>
    public static Manifest WithRevision(Manifest manifest)
    {
        return manifest with { Rev = Compute(manifest) };
    }

    /// <summary>Whether <c>Rev</c> of the manifest equals its computed revision.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>True when the stored revision is correct.</returns>
    public static bool IsCurrent(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return string.Equals(manifest.Rev, Compute(manifest), StringComparison.Ordinal);
    }
}
