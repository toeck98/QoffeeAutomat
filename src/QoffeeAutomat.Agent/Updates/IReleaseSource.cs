namespace QoffeeAutomat.Agent.Updates;

/// <summary>
/// Where releases come from (PLAN.md S13, T704): asks for the newest release and downloads its files. The only implementation talks to the
/// GitHub API (<see cref="GitHubReleaseClient"/>); the tests use a fake, so that no test touches the network.
/// </summary>
public interface IReleaseSource
{
    /// <summary>Asks for the newest published release. This is a network request.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The release; null when the project has no release that can be offered (none yet, or only drafts and pre-releases).</returns>
    /// <exception cref="UpdateException">The answer is not usable.</exception>
    /// <exception cref="HttpRequestException">The request failed.</exception>
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>Downloads a small text file of a release, such as the checksum file.</summary>
    /// <param name="asset">The file.</param>
    /// <param name="maxBytes">The largest size that is accepted.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The text.</returns>
    /// <exception cref="UpdateException">The file is too large or the address is not trusted.</exception>
    /// <exception cref="HttpRequestException">The request failed.</exception>
    Task<string> DownloadTextAsync(ReleaseAsset asset, int maxBytes, CancellationToken cancellationToken);

    /// <summary>Downloads a file of a release into a new file.</summary>
    /// <param name="asset">The file.</param>
    /// <param name="destination">The path of the file to create; it must not exist.</param>
    /// <param name="maxBytes">The largest size that is accepted.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the file is complete; a partial file is deleted when the download fails.</returns>
    /// <exception cref="UpdateException">The file is too large or the address is not trusted.</exception>
    /// <exception cref="HttpRequestException">The request failed.</exception>
    Task DownloadFileAsync(ReleaseAsset asset, string destination, long maxBytes, CancellationToken cancellationToken);
}
