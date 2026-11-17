using System.Globalization;
using System.Net;
using System.Net.Sockets;
using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Platform.Windows.Notifications;

/// <summary>The outcome of fetching the picture of a notification: the bytes, or the reason why there are none.</summary>
/// <param name="Data">The picture; null when it could not be fetched.</param>
/// <param name="Failure">A short English explanation when <paramref name="Data"/> is null.</param>
public sealed record NotificationImage(byte[]? Data, string? Failure)
{
    /// <summary>Creates the outcome of a successful fetch.</summary>
    /// <param name="data">The picture.</param>
    /// <returns>The outcome.</returns>
    public static NotificationImage Loaded(byte[] data) => new(data, null);

    /// <summary>Creates the outcome of a failed fetch.</summary>
    /// <param name="reason">What went wrong.</param>
    /// <returns>The outcome.</returns>
    public static NotificationImage Failed(string reason) => new(null, reason);
}

/// <summary>Fetches the picture that <c>notify.toast</c> shows (PLAN.md S11, T702).</summary>
public interface INotificationImageFetcher
{
    /// <summary>Fetches a picture. It never throws for a network problem: the outcome says what went wrong.</summary>
    /// <param name="url">The address from the parameter <c>ImageUrl</c>.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The picture or the failure.</returns>
    ValueTask<NotificationImage> FetchAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// Prepares the variables of <c>notify.toast</c> for the SessionHost (T702): the agent fetches the picture from <c>ImageUrl</c>
/// and passes the bytes on as <c>ImageData</c>; the address itself does not go on.
/// </summary>
public static class NotifyToastVariables
{
    /// <summary>Fetches the picture and builds the variables for the session.</summary>
    /// <param name="variables">The validated variables of the run.</param>
    /// <param name="images">Fetches the picture.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>
    /// The variables for the SessionHost, and why the picture was left out (null when it was not asked for or is included). A
    /// picture that cannot be fetched does not stop the notification. A caller cannot hand the session a picture of its own:
    /// the member <c>ImageData</c> is dropped first, because only this method adds it.
    /// </returns>
    public static async ValueTask<(IReadOnlyDictionary<string, object> Variables, string? Skipped)> PrepareAsync(
        IReadOnlyDictionary<string, object> variables,
        INotificationImageFetcher images,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(images);

        var prepared = new Dictionary<string, object>(variables, StringComparer.Ordinal);
        prepared.Remove(NotifyToastSpec.ImageData);
        string? skipped = null;
        if (prepared.Remove(NotifyToastSpec.ImageUrl, out object? address) && address is string { Length: > 0 } url)
        {
            NotificationImage image = await images.FetchAsync(url, cancellationToken).ConfigureAwait(false);
            if (image.Data is { } data)
            {
                prepared[NotifyToastSpec.ImageData] = Convert.ToBase64String(data);
            }
            else
            {
                skipped = image.Failure ?? "The picture could not be loaded.";
            }
        }

        return (prepared, skipped);
    }
}

/// <summary>
/// The <see cref="INotificationImageFetcher"/> of the agent (PLAN.md S11, S09, T702). The agent fetches the picture, not the
/// SessionHost, so that the user session needs no network access of its own and the limits are enforced in one place. It is a
/// request that Home Assistant can trigger, so the fetch is narrow: only <c>http</c> and <c>https</c>, no credentials in the
/// address, no literal link-local address (where cloud metadata services live), at most three redirects, ten seconds in total, at
/// most 1 MiB, and only a PNG, JPEG, GIF or BMP picture, judged by the content type and by the first bytes. Anything else is
/// refused and the notification appears without a picture.
/// </summary>
public sealed class NotificationImageFetcher : INotificationImageFetcher, IDisposable
{
    /// <summary>How long a fetch may take in total.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] ContentTypes = ["image/png", "image/jpeg", "image/gif", "image/bmp"];

    private readonly HttpClient _client;

    /// <summary>Creates the fetcher.</summary>
    /// <param name="handler">The HTTP handler; null creates one with the limits above. Tests pass a fake.</param>
    /// <param name="timeout">The total time; null uses <see cref="DefaultTimeout"/>.</param>
    public NotificationImageFetcher(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _client = new HttpClient(handler ?? CreateHandler())
        {
            Timeout = timeout ?? DefaultTimeout,
            MaxResponseContentBufferSize = NotifyToastSpec.MaxImageBytes,
        };
    }

    /// <summary>
    /// Checks an address without fetching it.
    /// </summary>
    /// <param name="url">The address.</param>
    /// <param name="uri">The parsed address when it is acceptable.</param>
    /// <returns>Null when the address may be fetched, otherwise the reason why not.</returns>
    public static string? CheckUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > NotifyToastSpec.MaxUrlLength)
        {
            return "The picture address is empty or too long.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return "The picture address must be an absolute http or https address.";
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return "The picture address must not contain a user name or password.";
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return "The picture address has no host.";
        }

        if (IPAddress.TryParse(parsed.Host.Trim('[', ']'), out IPAddress? address) && IsLinkLocal(address))
        {
            return "The picture address points to a link-local address, which is not allowed.";
        }

        uri = parsed;
        return null;
    }

    /// <inheritdoc />
    public async ValueTask<NotificationImage> FetchAsync(string url, CancellationToken cancellationToken)
    {
        string? problem = CheckUrl(url, out Uri? uri);
        if (problem is not null || uri is null)
        {
            return NotificationImage.Failed(problem ?? "The picture address is not valid.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return NotificationImage.Failed("The server answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
            }

            string? contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is null || !ContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            {
                return NotificationImage.Failed("The server did not send a PNG, JPEG, GIF or BMP picture.");
            }

            if (response.Content.Headers.ContentLength is { } declared && declared > NotifyToastSpec.MaxImageBytes)
            {
                return NotificationImage.Failed("The picture is larger than 1 MiB.");
            }

            byte[]? data = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (data is null)
            {
                return NotificationImage.Failed("The picture is larger than 1 MiB.");
            }

            return NotifyToastSpec.DetectImageExtension(data) is null
                ? NotificationImage.Failed("The data is not a PNG, JPEG, GIF or BMP picture.")
                : NotificationImage.Loaded(data);
        }
        catch (HttpRequestException exception)
        {
            return NotificationImage.Failed("The picture could not be loaded: " + exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NotificationImage.Failed("The picture did not arrive in time.");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private static SocketsHttpHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || (address.IsIPv4MappedToIPv6 && IsLinkLocal(address.MapToIPv4()));
        }

        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>Reads the body up to the limit; null when it is longer.</summary>
    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > NotifyToastSpec.MaxImageBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
