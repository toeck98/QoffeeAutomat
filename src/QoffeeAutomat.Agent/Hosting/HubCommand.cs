using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Agent.Hub;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// The CLI verbs <c>hub join &lt;string&gt;</c>, <c>hub leave</c> and <c>hub status</c> (PLAN.md S20, T905). They talk to the running agent through the hub
/// channel (<see cref="HubAdminChannel"/>), whose address and secret are in <c>secrets/hub-channel.json</c>, a file that only administrators can read. The
/// verbs therefore work only on the PC where the agent runs and only for a local administrator (Windows) or root (Linux): a process that a command started
/// has the secret of the other local channel, but not this one, and must not be able to hand the PC to a hub. The request only ever goes to a loopback
/// address. The enrollment string contains a token: it is never printed, and <c>-</c> reads it from standard input so that it does not show in a process list.
/// </summary>
public static class HubCommand
{
    /// <summary>Runs the verb.</summary>
    /// <param name="options">The parsed command line; <see cref="AgentOptions.HubAction"/> is set.</param>
    /// <param name="output">Receives the result.</param>
    /// <param name="error">Receives error messages.</param>
    /// <param name="input">Standard input, for <c>hub join -</c>.</param>
    /// <param name="channelFile">The file with address and secret, or null to use the data directory of <paramref name="options"/>.</param>
    /// <param name="handler">The HTTP handler; null uses the default. Tests inject their own.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// <see cref="ExitCodes.Success"/>; <see cref="ExitCodes.Usage"/> when the agent refuses the enrollment string or the option;
    /// <see cref="ExitCodes.Unavailable"/> when no agent can be reached; <see cref="ExitCodes.NoPermission"/> when the agent rejected the secret;
    /// <see cref="ExitCodes.Failure"/> for any other problem, including a hub that cannot be reached.
    /// </returns>
    public static async Task<int> RunAsync(
        AgentOptions options,
        TextWriter output,
        TextWriter error,
        TextReader input,
        string? channelFile = null,
        HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(input);

        if (options.HubAction is not { } action)
        {
            await error.WriteLineAsync("hub needs an action: hub join <string>, hub leave or hub status.");
            return ExitCodes.Usage;
        }

        string? enrollment = null;
        if (action == HubAction.Join)
        {
            enrollment = options.HubEnrollment == "-" ? await input.ReadLineAsync(cancellationToken) : options.HubEnrollment;
            if (string.IsNullOrWhiteSpace(enrollment))
            {
                await error.WriteLineAsync("hub join needs the enrollment string: hub join <string>, or hub join - to read it from standard input.");
                return ExitCodes.Usage;
            }
        }

        string file = channelFile ?? Path.Combine(AppPaths.ForCurrentProcess(options.DataDir).SecretsDirectory, HubAdminChannel.FileName);
        LocalChannelEndpoint? endpoint = await LocalChannelEndpoint.FromFileAsync(file, cancellationToken);
        if (endpoint is null)
        {
            await error.WriteLineAsync(
                "No running agent was found, or its channel file cannot be read. Run hub as a local administrator (Windows) or root (Linux) on the PC where the agent runs.");
            return ExitCodes.Unavailable;
        }

        if (!endpoint.BaseAddress.IsLoopback)
        {
            await error.WriteLineAsync("The hub channel address is not a loopback address; refusing to send the secret there.");
            return ExitCodes.Failure;
        }

        using HttpRequestMessage request = BuildRequest(endpoint, action, enrollment, options.HubDocuments);
        using HttpClient client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

        // Joining asks the hub, which may take a while to answer.
        client.Timeout = TimeSpan.FromSeconds(45);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return await ReportAsync(action, body, output, error);
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    await error.WriteLineAsync("The agent rejected the request: this caller has no access to the hub channel.");
                    return ExitCodes.NoPermission;
                case HttpStatusCode.BadRequest:
                case HttpStatusCode.RequestEntityTooLarge:
                case HttpStatusCode.UnprocessableEntity:
                    await error.WriteLineAsync(LocalChannelClient.ErrorMessage(body));
                    return ExitCodes.Usage;
                case HttpStatusCode.Conflict:
                case HttpStatusCode.BadGateway:
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

    private static HttpRequestMessage BuildRequest(LocalChannelEndpoint endpoint, HubAction action, string? enrollment, string? documents)
    {
        string path = action switch
        {
            HubAction.Join => HubAdminChannel.JoinPath,
            HubAction.Leave => HubAdminChannel.LeavePath,
            _ => HubAdminChannel.BasePath,
        };
        var request = new HttpRequestMessage(action == HubAction.Status ? HttpMethod.Get : HttpMethod.Post, new Uri(endpoint.BaseAddress, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        if (action != HubAction.Status)
        {
            request.Content = new StringContent(BodyOf(action, enrollment, documents), Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static string BodyOf(HubAction action, string? enrollment, string? documents)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (action == HubAction.Join)
            {
                writer.WriteString("enrollment", enrollment!.Trim());
            }
            else if (documents is not null)
            {
                writer.WriteString("documents", documents);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<int> ReportAsync(HubAction action, string body, TextWriter output, TextWriter error)
    {
        HubLinkStatus? status;
        try
        {
            status = JsonSerializer.Deserialize(body, ApiJsonContext.Default.HubLinkStatus);
        }
        catch (JsonException)
        {
            status = null;
        }

        if (status is null)
        {
            await error.WriteLineAsync("The agent answered with something that is not a hub status.");
            return ExitCodes.Failure;
        }

        if (action == HubAction.Join)
        {
            await output.WriteLineAsync(status.State == HubLinkStates.Pending
                ? "Waiting for an administrator of the hub to approve this agent."
                : "Joined the hub" + (status.HubName is null ? "." : " '" + status.HubName + "'."));
        }
        else if (action == HubAction.Leave)
        {
            await output.WriteLineAsync("Left the hub.");
        }

        await output.WriteLineAsync("State: " + status.State);
        if (status.HubName is not null)
        {
            await output.WriteLineAsync("Hub: " + status.HubName + (status.HubId is null ? string.Empty : " (" + status.HubId + ")"));
        }

        if (status.Url is not null)
        {
            await output.WriteLineAsync("Address: " + status.Url);
        }

        await output.WriteLineAsync("Management tunnel: " + (status.ManagementTunnel ? "on" : "off"));
        if (status.Detail is not null)
        {
            await output.WriteLineAsync("Detail: " + status.Detail);
        }

        return ExitCodes.Success;
    }
}
