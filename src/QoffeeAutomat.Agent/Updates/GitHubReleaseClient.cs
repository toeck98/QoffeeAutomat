using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Updates;

namespace QoffeeAutomat.Agent.Updates;

/// <summary>
/// Reads the releases of the project from GitHub (PLAN.md S09 item 13, S13, T704). It is the only code of the agent that contacts the
/// internet on its own initiative, and it does so only through <see cref="IReleaseSource.GetLatestAsync"/>, which the
/// <see cref="UpdateChecker"/> calls while the check is switched on. Downloads are limited to HTTPS addresses of GitHub (also after
/// redirects), to a maximum size and to a time limit, and they send nothing but the standard headers: no cookie, no token, no
/// identifier of the PC.
/// </summary>
public sealed partial class GitHubReleaseClient : IReleaseSource
{
    private const int BufferSize = 81920;

    private readonly HttpClient _http;
    private readonly UpdateOptions _options;

    /// <summary>Creates the client.</summary>
    /// <param name="http">The HTTP client; the client does not change its settings.</param>
    /// <param name="options">The parameters.</param>
    public GitHubReleaseClient(HttpClient http, UpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        if (!RepositoryPattern().IsMatch(options.Repository))
        {
            throw new ArgumentException("The repository must be owner/name.", nameof(options));
        }

        _http = http;
        _options = options;
    }

    /// <summary>Tells whether an address may be fetched: HTTPS, and a host of GitHub (<c>github.com</c> or <c>githubusercontent.com</c> and their subdomains).</summary>
    /// <param name="url">The address.</param>
    /// <returns>True when the address is trusted.</returns>
    public static bool IsTrustedAddress(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        string host = url.IdnHost;
        return IsHostOrSubdomain(host, "github.com") || IsHostOrSubdomain(host, "githubusercontent.com");
    }

    /// <inheritdoc />
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        var url = new Uri(_options.ApiBaseAddress, "repos/" + _options.Repository + "/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        AddUserAgent(request);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The project has no published release yet.
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException("GitHub answered with the status " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        byte[] bytes = await ReadLimitedAsync(body, _options.MaxReleaseBytes, timeout.Token).ConfigureAwait(false);
        GitHubRelease? release;
        try
        {
            release = JsonSerializer.Deserialize(bytes, UpdateJsonContext.Default.GitHubRelease);
        }
        catch (JsonException exception)
        {
            throw new UpdateException("The answer of GitHub is not a release.", exception);
        }

        return release is not null && ReleaseParser.TryCreate(release, _options.InstallerArchitecture, out ReleaseInfo? info) ? info : null;
    }

    /// <inheritdoc />
    public async Task<string> DownloadTextAsync(ReleaseAsset asset, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        using HttpResponseMessage response = await OpenAsync(asset, maxBytes, timeout.Token).ConfigureAwait(false);
        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        byte[] bytes = await ReadLimitedAsync(body, maxBytes, timeout.Token).ConfigureAwait(false);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes);
    }

    /// <inheritdoc />
    public async Task DownloadFileAsync(ReleaseAsset asset, string destination, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.DownloadTimeout);

        using HttpResponseMessage response = await OpenAsync(asset, maxBytes, timeout.Token).ConfigureAwait(false);
        bool complete = false;
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            byte[] buffer = new byte[BufferSize];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new UpdateException("The download is larger than the limit of " + maxBytes.ToString(CultureInfo.InvariantCulture) + " bytes.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }

            await file.FlushAsync(timeout.Token).ConfigureAwait(false);
            complete = true;
        }
        finally
        {
            if (!complete)
            {
                TryDelete(destination);
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    private static bool IsHostOrSubdomain(string host, string domain)
    {
        return string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[BufferSize];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new UpdateException("The answer is larger than the limit of " + maxBytes.ToString(CultureInfo.InvariantCulture) + " bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A partial file that cannot be removed is removed with its directory later.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private void AddUserAgent(HttpRequestMessage request)
    {
        // The standard identification of a client; GitHub requires one. It names the software and its version, nothing about the PC.
        request.Headers.UserAgent.ParseAdd("QoffeeAutomat/" + _options.InstalledVersion);
    }

    private async Task<HttpResponseMessage> OpenAsync(ReleaseAsset asset, long maxBytes, CancellationToken cancellationToken)
    {
        if (!IsTrustedAddress(asset.Url))
        {
            throw new UpdateException("The address of " + asset.Name + " is not a GitHub address.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        AddUserAgent(request);

        HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            // Redirects are followed by the handler; the file must still come from GitHub in the end.
            Uri final = response.RequestMessage?.RequestUri ?? asset.Url;
            if (!IsTrustedAddress(final))
            {
                throw new UpdateException("The download of " + asset.Name + " was redirected away from GitHub.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException("GitHub answered with the status " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " for " + asset.Name + ".");
            }

            if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
            {
                throw new UpdateException(asset.Name + " is larger than the limit of " + maxBytes.ToString(CultureInfo.InvariantCulture) + " bytes.");
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
