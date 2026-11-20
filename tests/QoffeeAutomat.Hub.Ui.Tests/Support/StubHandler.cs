using System.Net;
using System.Text;

namespace QoffeeAutomat.Hub.Ui.Tests.Support;

/// <summary>What a request looked like when it arrived (the message itself is disposed by the client afterwards).</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? ContentType, string? Body, string? Accept);

/// <summary>An HTTP handler that records the requests and answers with what the test says.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, HttpResponseMessage> _respond;

    public StubHandler(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>A JSON answer.</summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string json, string mediaType = "application/json")
    {
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };
    }

    /// <summary>A Server-Sent Events answer with the text as its whole body.</summary>
    public static HttpResponseMessage EventStream(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Dictionary<string, string> headers = request.Headers.ToDictionary(
            header => header.Key,
            header => string.Join(",", header.Value),
            StringComparer.OrdinalIgnoreCase);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!,
            headers,
            request.Content?.Headers.ContentType?.ToString(),
            body,
            request.Headers.Accept.Count == 0 ? null : string.Join(",", request.Headers.Accept));
        lock (Requests)
        {
            Requests.Add(recorded);
        }

        return _respond(recorded);
    }
}
