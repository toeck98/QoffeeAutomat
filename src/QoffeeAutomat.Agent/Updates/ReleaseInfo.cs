using System.Diagnostics.CodeAnalysis;
using QoffeeAutomat.Contracts.Updates;
using QoffeeAutomat.Core.Updates;

namespace QoffeeAutomat.Agent.Updates;

/// <summary>A file of a release.</summary>
/// <param name="Name">The file name.</param>
/// <param name="Url">The address to download it from.</param>
/// <param name="Size">The size in bytes as GitHub reports it; zero when unknown.</param>
public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <summary>The newest release as the update check understands it.</summary>
/// <param name="Version">The version of the release.</param>
/// <param name="Tag">The tag.</param>
/// <param name="PageUrl">The release page, or null.</param>
/// <param name="Notes">The release notes, shortened to <see cref="UpdateStatus.MaxNotesLength"/> characters; null when empty.</param>
/// <param name="Installer">The installer for this architecture; null when the release has none (it cannot be installed).</param>
/// <param name="Checksums">The checksum file <c>SHA256SUMS.txt</c>; null when the release has none (the installer is then refused).</param>
public sealed record ReleaseInfo(
    SemanticVersion Version,
    string Tag,
    string? PageUrl,
    string? Notes,
    ReleaseAsset? Installer,
    ReleaseAsset? Checksums);

/// <summary>Turns the answer of the GitHub releases API into a <see cref="ReleaseInfo"/>.</summary>
public static class ReleaseParser
{
    /// <summary>The name of the installer of a version, as <c>release.yml</c> names it (PLAN.md S13).</summary>
    /// <param name="version">The version, for example <c>1.2.0</c>.</param>
    /// <param name="architecture">The architecture, <c>x64</c> or <c>arm64</c>.</param>
    /// <returns>For example <c>QoffeeAutomat-Setup-1.2.0-x64.exe</c>.</returns>
    public static string InstallerName(string version, string architecture)
    {
        return "QoffeeAutomat-Setup-" + version + "-" + architecture + ".exe";
    }

    /// <summary>Reads a release.</summary>
    /// <param name="release">The release as GitHub sent it.</param>
    /// <param name="architecture">The architecture of the installer to pick.</param>
    /// <param name="info">The release.</param>
    /// <returns>False for a draft, a pre-release or a tag that is no version: they are never offered.</returns>
    public static bool TryCreate(GitHubRelease release, string architecture, [NotNullWhen(true)] out ReleaseInfo? info)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(architecture);

        info = null;
        if (release.Draft || release.Prerelease || !SemanticVersion.TryParse(release.TagName, out SemanticVersion? version) || version.IsPreRelease)
        {
            return false;
        }

        string installerName = InstallerName(version.ToString(), architecture);
        ReleaseAsset? installer = null;
        ReleaseAsset? checksums = null;
        foreach (GitHubAsset asset in release.Assets ?? [])
        {
            if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri? url))
            {
                continue;
            }

            if (string.Equals(asset.Name, installerName, StringComparison.Ordinal))
            {
                installer = new ReleaseAsset(asset.Name, url, asset.Size);
            }
            else if (string.Equals(asset.Name, UpdateOptions.ChecksumsAssetName, StringComparison.Ordinal))
            {
                checksums = new ReleaseAsset(asset.Name, url, asset.Size);
            }
        }

        info = new ReleaseInfo(version, release.TagName, ShortenUrl(release.HtmlUrl), ShortenNotes(release.Body), installer, checksums);
        return true;
    }

    private static string? ShortenUrl(string? url)
    {
        // Only a web address is published: the value ends up in a Home Assistant entity and a link in its dialog.
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && parsed.Scheme == Uri.UriSchemeHttps ? parsed.AbsoluteUri : null;
    }

    private static string? ShortenNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        string text = body.Trim();
        return text.Length <= UpdateStatus.MaxNotesLength ? text : text[..(UpdateStatus.MaxNotesLength - 1)] + "…";
    }
}
