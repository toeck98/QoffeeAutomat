using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using QoffeeAutomat.Agent.Link.Outbound;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Hub;

/// <summary>Creates the HTTP clients that talk to a hub: the enrollment call and the probe of a refused agent link. Tests replace it.</summary>
public interface IHubHttpClientFactory
{
    /// <summary>Creates a client that accepts the certificate of the hub as the enrollment string says.</summary>
    /// <param name="pinnedFingerprint">The SHA-256 fingerprint (64 upper-case hexadecimal digits) of the only certificate to accept, or null for the normal certificate validation.</param>
    /// <returns>The client; the caller disposes it. It does not follow redirects, so the token and the credential never go anywhere else.</returns>
    HttpClient Create(string? pinnedFingerprint);
}

/// <summary>
/// <see cref="IHubHttpClientFactory"/> on <see cref="SocketsHttpHandler"/>. With a fingerprint the certificate of the hub must be exactly that one and
/// nothing else is accepted, whatever the chain says; without one the operating system validates it (PLAN.md S20).
/// </summary>
public sealed class HubHttpClientFactory : IHubHttpClientFactory
{
    /// <summary>The longest a call may take.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    public HttpClient Create(string? pinnedFingerprint)
    {
        byte[]? pinned = pinnedFingerprint is null ? null : Convert.FromHexString(pinnedFingerprint);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = RequestTimeout,
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            ClientWebSocketConnector.CertificateIsAcceptable(pinned, certificate, errors);
        return new HttpClient(handler, disposeHandler: true) { Timeout = RequestTimeout };
    }
}

/// <summary>How an enrollment attempt ended.</summary>
public enum HubEnrollResult
{
    /// <summary>The hub issued a credential.</summary>
    Enrolled,

    /// <summary>The token needs approval; the agent repeats the call after <see cref="HubEnrollOutcome.RetryAfterSeconds"/>.</summary>
    Pending,

    /// <summary>The hub refuses this enrollment for good: the token is unknown, expired or used up, an administrator rejected the agent, or the request is not acceptable. Repeating it does not help.</summary>
    Rejected,

    /// <summary>The attempt failed for a reason that may pass: the hub cannot be reached, the certificate does not match, the hub is busy. Try again later.</summary>
    Failed,
}

/// <summary>The result of <see cref="HubEnrollmentClient.EnrollAsync"/>.</summary>
/// <param name="Result">How the attempt ended.</param>
/// <param name="Message">A sentence for the user; never contains the token or the credential.</param>
/// <param name="Hub">The hub that answered; present for <see cref="HubEnrollResult.Enrolled"/> and <see cref="HubEnrollResult.Pending"/>.</param>
/// <param name="Credential">The agent credential; present for <see cref="HubEnrollResult.Enrolled"/> only.</param>
/// <param name="RetryAfterSeconds">The seconds to wait before the next attempt; for <see cref="HubEnrollResult.Pending"/> and a busy hub, otherwise 0.</param>
public sealed record HubEnrollOutcome(
    HubEnrollResult Result,
    string Message,
    EnrollHubInfo? Hub = null,
    string? Credential = null,
    int RetryAfterSeconds = 0)
{
    /// <summary>Hides the credential from the generated <c>ToString</c> so that it does not end up in logs.</summary>
    /// <returns>A text without the credential.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"HubEnrollOutcome {{ Result = {Result} }}");
}

/// <summary>
/// The enrollment call of the agent: <c>POST /hub/v1/enroll</c> with the token and the identity of the agent (PLAN.md S20, step 3). The answer is a
/// credential or, when the token requires approval, the state <c>pending</c>. Errors of the hub are
/// <c>{"error":{"code","message"}}</c> with an HTTP status; the codes that matter here are <c>invalid_token</c> (401), <c>rejected</c> (403),
/// <c>bad_request</c> (400), <c>conflict</c> (409) and <c>rate_limited</c> (429, with <c>Retry-After</c>).
/// </summary>
public sealed class HubEnrollmentClient
{
    private const int MaxResponseBytes = 64 * 1024;
    private const int DefaultRetryAfterSeconds = 30;
    private const int MaxRetryAfterSeconds = 3600;

    private readonly IHubHttpClientFactory _http;

    /// <summary>Creates the client.</summary>
    /// <param name="http">Creates the HTTP clients.</param>
    public HubEnrollmentClient(IHubHttpClientFactory http)
    {
        ArgumentNullException.ThrowIfNull(http);

        _http = http;
    }

    /// <summary>Calls the enrollment endpoint once.</summary>
    /// <param name="hubUrl">The base address of the hub.</param>
    /// <param name="fingerprint">The pinned certificate fingerprint, or null.</param>
    /// <param name="token">The enrollment token.</param>
    /// <param name="agent">The identity of this agent, as in <c>hello</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome. The method does not throw for a failure of the hub or the network.</returns>
    public async Task<HubEnrollOutcome> EnrollAsync(Uri hubUrl, string? fingerprint, string token, AgentInfo agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hubUrl);
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(agent);

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new EnrollRequest(token, agent), HubJsonContext.Default.EnrollRequest);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, HubUrls.Combine(hubUrl, HubProtocol.EnrollPath, webSocket: false)) { Content = content };

        try
        {
            using HttpClient client = _http.Create(fingerprint);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Interpret(response, text);
        }
        catch (HttpRequestException exception)
        {
            return Failed(exception.InnerException is AuthenticationException
                ? "The certificate of the hub is not acceptable" + (fingerprint is null ? "." : ": it does not match the fingerprint of the enrollment string.")
                : "The hub cannot be reached: " + exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("The hub did not answer in time.");
        }
        catch (JsonException)
        {
            return Failed("The hub answered with something that is not an enrollment response.");
        }
    }

    private static HubEnrollOutcome Interpret(HttpResponseMessage response, string text)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            EnrollResponse? answer = JsonSerializer.Deserialize(text, HubJsonContext.Default.EnrollResponse);
            if (answer is { State: EnrollStates.Enrolled } && HubCredentialRules.IsValid(answer.Credential))
            {
                return new HubEnrollOutcome(HubEnrollResult.Enrolled, "The hub accepted this agent.", answer.Hub, answer.Credential);
            }

            if (answer is { State: EnrollStates.Pending, RetryAfter: >= 1 })
            {
                int retry = Math.Min(answer.RetryAfter.Value, MaxRetryAfterSeconds);
                return new HubEnrollOutcome(HubEnrollResult.Pending, "An administrator of the hub has to accept this agent.", answer.Hub, null, retry);
            }

            return Failed("The hub answered with an enrollment response that this agent does not understand.");
        }

        string? code = HubHttp.ReadErrorCode(text);
        int status = (int)response.StatusCode;
        return (status, code) switch
        {
            (401, "invalid_token") => Rejected("The hub does not accept the token: it is unknown, expired or used up."),
            (403, "rejected") => Rejected("An administrator of the hub rejected this agent."),
            (400, _) => Rejected("The hub refused the request as malformed."),
            (409, _) => Rejected("The hub reports a conflict with an existing agent."),
            (429, _) => new HubEnrollOutcome(HubEnrollResult.Failed, "The hub limits enrollment requests; try again later.", null, null, RetryAfter(response)),
            _ => Failed(string.Create(CultureInfo.InvariantCulture, $"The hub answered with HTTP {status}.")),
        };
    }

    private static int RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return (int)Math.Clamp(Math.Ceiling(delta.TotalSeconds), 1, MaxRetryAfterSeconds);
        }

        return DefaultRetryAfterSeconds;
    }

    private static HubEnrollOutcome Rejected(string message) => new(HubEnrollResult.Rejected, message);

    private static HubEnrollOutcome Failed(string message) => new(HubEnrollResult.Failed, message);
}

/// <summary>What the agent reads from the error answers of the hub.</summary>
public static class HubHttp
{
    /// <summary>Reads the <c>code</c> of <c>{"error":{"code","message"}}</c>, or of an RFC 9457 problem with a member <c>code</c>.</summary>
    /// <param name="body">The response body.</param>
    /// <returns>The code, or null when the body has another shape.</returns>
    public static string? ReadErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out JsonElement nested)
                && nested.ValueKind == JsonValueKind.String)
            {
                return nested.GetString();
            }

            return root.TryGetProperty("code", out JsonElement code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
