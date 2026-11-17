using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// The CLI verbs <c>state set &lt;id&gt; &lt;value&gt;</c> and <c>state get &lt;id&gt;</c> (PLAN.md T714): set or read the value of a
/// custom sensor of type <c>state</c> in the running agent, through the authenticated local channel of <c>emit</c> (the same
/// environment variables <c>QA_EMIT_URL</c> and <c>QA_EMIT_TOKEN</c> that the agent gives to every process it starts, or
/// <c>secrets/local-channel.json</c> for administrators). The agent checks the value against the value type and the options of
/// the sensor and publishes it at once, so a script can report the real state of what it did.
/// </summary>
public static class StateCommand
{
    /// <summary>Runs the verb.</summary>
    /// <param name="options">The parsed command line; <see cref="AgentOptions.StateAction"/> and <see cref="AgentOptions.StateSensorId"/> are set.</param>
    /// <param name="output">Receives the result: for <c>get</c> the value alone, so that a script can read it.</param>
    /// <param name="error">Receives error messages.</param>
    /// <param name="getVariable">Reads environment variables; <see cref="Environment.GetEnvironmentVariable(string)"/> in production.</param>
    /// <param name="channelFile">The file with address and secret, or null to use the data directory of <paramref name="options"/>.</param>
    /// <param name="handler">The HTTP handler; null uses the default. Tests inject their own.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// <see cref="ExitCodes.Success"/>; <see cref="ExitCodes.Usage"/> when the agent refuses the sensor or the value (unknown sensor,
    /// sensor of another type, value that does not fit); <see cref="ExitCodes.Unavailable"/> when no agent can be reached;
    /// <see cref="ExitCodes.NoPermission"/> when the agent rejected the secret; <see cref="ExitCodes.Failure"/> for any other problem,
    /// including a value that was set but could not be saved.
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

        if (options.StateAction is null || string.IsNullOrWhiteSpace(options.StateSensorId))
        {
            await error.WriteLineAsync("state needs an action and a sensor id: state set <id> <value> or state get <id>.");
            return ExitCodes.Usage;
        }

        bool isSet = options.StateAction == StateAction.Set;
        if (isSet && options.StateValue is null)
        {
            await error.WriteLineAsync("state set needs a value: state set <id> <value>.");
            return ExitCodes.Usage;
        }

        (LocalChannelEndpoint? endpoint, int exitCode) = await LocalChannelClient.ResolveAsync("state", options, error, getVariable, channelFile, cancellationToken);
        if (endpoint is null)
        {
            return exitCode;
        }

        string sensorId = options.StateSensorId;
        var address = new Uri(endpoint.BaseAddress, LocalChannelNames.StatePath + "/" + Uri.EscapeDataString(sensorId));
        using var request = new HttpRequestMessage(isSet ? HttpMethod.Put : HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        if (isSet)
        {
            request.Content = new StringContent(BodyOf(options.StateValue!), Encoding.UTF8, "application/json");
        }

        using HttpClient client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = LocalChannelClient.RequestTimeout;
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return await ReportAsync(sensorId, isSet, body, output, error);
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    await error.WriteLineAsync("The agent rejected the request: this caller has no access to the local channel.");
                    return ExitCodes.NoPermission;
                case HttpStatusCode.BadRequest:
                case HttpStatusCode.NotFound:
                case HttpStatusCode.Conflict:
                case HttpStatusCode.RequestEntityTooLarge:
                    await error.WriteLineAsync(LocalChannelClient.ErrorMessage(body));
                    return ExitCodes.Usage;
                case HttpStatusCode.InternalServerError:
                    await error.WriteLineAsync(LocalChannelClient.ErrorMessage(body));
                    return ExitCodes.Failure;
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

    /// <summary>The request body: the value as typed, as a JSON string; the agent converts it to the type of the sensor.</summary>
    private static string BodyOf(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("value", value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<int> ReportAsync(string sensorId, bool isSet, string body, TextWriter output, TextWriter error)
    {
        string? text;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            text = ValueText(document.RootElement);
        }
        catch (JsonException)
        {
            await error.WriteLineAsync("The agent answered with something that is not a state value.");
            return ExitCodes.Failure;
        }

        if (isSet)
        {
            await output.WriteLineAsync($"Sensor '{sensorId}' set to {text ?? "(no value)"}.");
            return ExitCodes.Success;
        }

        if (text is null)
        {
            // Nothing on stdout, so that a script does not mistake a message for the value.
            await error.WriteLineAsync($"Sensor '{sensorId}' has no value yet.");
            return ExitCodes.Success;
        }

        await output.WriteLineAsync(text);
        return ExitCodes.Success;
    }

    /// <summary>The <c>value</c> of the answer as plain text: a string without quotes, <c>true</c>, <c>false</c> or the number; null for no value.</summary>
    private static string? ValueText(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("value", out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
    }
}
