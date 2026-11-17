using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// The CLI verb <c>emit &lt;name&gt; [--data &lt;json&gt;]</c> (PLAN.md T217): sends a custom event to the
/// running agent through the authenticated local channel, so a script can notify Home Assistant. The
/// address and the secret come from the environment (<c>QA_EMIT_URL</c>, <c>QA_EMIT_TOKEN</c>), which
/// the agent sets for every process that a command starts, or from <c>secrets/local-channel.json</c>,
/// which only administrators can read. The request only ever goes to a loopback address.
/// </summary>
public static class EmitCommand
{
    /// <summary>Runs the verb.</summary>
    /// <param name="options">The parsed command line; <see cref="AgentOptions.EventName"/> is set.</param>
    /// <param name="output">Receives the confirmation.</param>
    /// <param name="error">Receives error messages.</param>
    /// <param name="getVariable">Reads environment variables; <see cref="Environment.GetEnvironmentVariable(string)"/> in production.</param>
    /// <param name="channelFile">The file with address and secret, or null to use the data directory of <paramref name="options"/>.</param>
    /// <param name="handler">The HTTP handler; null uses the default. Tests inject their own.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// <see cref="ExitCodes.Success"/>; <see cref="ExitCodes.Usage"/> for an invalid name or data;
    /// <see cref="ExitCodes.Unavailable"/> when no agent can be reached;
    /// <see cref="ExitCodes.NoPermission"/> when the agent rejected the secret;
    /// <see cref="ExitCodes.Failure"/> for any other problem.
    /// </returns>
    public static async Task<int> RunAsync(
        AgentOptions options,
        TextWriter output,
        TextWriter error,
        Func<string, string?> getVariable,
        string? channelFile = null,
        HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(getVariable);

        JsonElement? data;
        try
        {
            data = ParseData(options.EventData);
        }
        catch (JsonException exception)
        {
            await error.WriteLineAsync("--data is not valid JSON: " + exception.Message);
            return ExitCodes.Usage;
        }

        CustomEventResult check = CustomEvents.Create(options.EventName, data);
        if (!check.IsValid)
        {
            await error.WriteLineAsync(check.Error);
            return ExitCodes.Usage;
        }

        (LocalChannelEndpoint? endpoint, int exitCode) = await LocalChannelClient.ResolveAsync("emit", options, error, getVariable, channelFile, cancellationToken);
        if (endpoint is null)
        {
            return exitCode;
        }

        return await SendAsync(options.EventName!, data, endpoint, output, error, handler, cancellationToken);
    }

    private static JsonElement? ParseData(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<int> SendAsync(
        string name,
        JsonElement? data,
        LocalChannelEndpoint endpoint,
        TextWriter output,
        TextWriter error,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        JsonElement body = data is { } value
            ? EventData.Create(("name", name), ("data", value))
            : EventData.Create(("name", name));

        using HttpClient client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = LocalChannelClient.RequestTimeout;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseAddress, LocalChannelNames.EmitPath))
        {
            Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                case HttpStatusCode.Accepted:
                    await output.WriteLineAsync("Event '" + name + "' sent.");
                    return ExitCodes.Success;
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    await error.WriteLineAsync("The agent rejected the request: this caller has no access to the local channel.");
                    return ExitCodes.NoPermission;
                case HttpStatusCode.BadRequest:
                case HttpStatusCode.RequestEntityTooLarge:
                    await error.WriteLineAsync("The agent rejected the event: " + (await response.Content.ReadAsStringAsync(cancellationToken)).Trim());
                    return ExitCodes.Usage;
                default:
                    await error.WriteLineAsync($"The agent answered with HTTP {(int)response.StatusCode}.");
                    return ExitCodes.Failure;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            await error.WriteLineAsync("The agent could not be reached: " + exception.Message);
            return ExitCodes.Unavailable;
        }
    }
}
