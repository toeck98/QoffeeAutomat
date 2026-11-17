namespace QoffeeAutomat.Contracts.Updates;

/// <summary>
/// The members of a GitHub release (<c>GET /repos/{owner}/{repo}/releases/latest</c>) that the update check reads. GitHub sends
/// <c>snake_case</c> names and many more members; the others are ignored. The shape is GitHub's, not a QoffeeAutomat document.
/// </summary>
/// <param name="TagName">The tag of the release, for example <c>v1.2.0</c>.</param>
/// <param name="Name">The title of the release.</param>
/// <param name="Body">The release notes in Markdown.</param>
/// <param name="HtmlUrl">The address of the release page.</param>
/// <param name="Draft">True for a release that is not published.</param>
/// <param name="Prerelease">True for a pre-release.</param>
/// <param name="PublishedAt">When the release was published.</param>
/// <param name="Assets">The files of the release.</param>
public sealed record GitHubRelease(
    string TagName,
    string? Name = null,
    string? Body = null,
    string? HtmlUrl = null,
    bool Draft = false,
    bool Prerelease = false,
    DateTimeOffset? PublishedAt = null,
    IReadOnlyList<GitHubAsset>? Assets = null);

/// <summary>A file of a GitHub release.</summary>
/// <param name="Name">The file name, for example <c>QoffeeAutomat-Setup-1.2.0-x64.exe</c>.</param>
/// <param name="BrowserDownloadUrl">The address the file is downloaded from.</param>
/// <param name="Size">The size in bytes.</param>
public sealed record GitHubAsset(string Name, string BrowserDownloadUrl, long Size = 0);
