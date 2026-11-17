using System.Text.Json;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Agent.Hosting;

/// <summary>
/// What the verbs that talk to the running agent through the authenticated local channel (<c>emit</c>, <c>state</c>) have in
/// common: finding the address and the secret, and refusing to send the secret anywhere but to a loopback address.
/// </summary>
internal static class LocalChannelClient
{
    /// <summary>The longest the verbs wait for the agent.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Finds the local channel: the environment of a process that the agent started first, then the file for administrators.</summary>
    /// <param name="verb">The verb, for the message.</param>
    /// <param name="options">The parsed command line; its data directory locates the file.</param>
    /// <param name="error">Receives the explanation when there is no usable channel.</param>
    /// <param name="getVariable">Reads environment variables.</param>
    /// <param name="channelFile">The file with address and secret, or null to use the data directory of <paramref name="options"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The endpoint, or null with the exit code that the verb returns.</returns>
    public static async Task<(LocalChannelEndpoint? Endpoint, int ExitCode)> ResolveAsync(
        string verb,
        AgentOptions options,
        TextWriter error,
        Func<string, string?> getVariable,
        string? channelFile,
        CancellationToken cancellationToken)
    {
        LocalChannelEndpoint? endpoint = LocalChannelEndpoint.FromEnvironment(getVariable);
        if (endpoint is null)
        {
            string file = channelFile ?? Path.Combine(AppPaths.ForCurrentProcess(options.DataDir).SecretsDirectory, LocalChannelNames.FileName);
            endpoint = await LocalChannelEndpoint.FromFileAsync(file, cancellationToken);
        }

        if (endpoint is null)
        {
            await error.WriteLineAsync(
                $"No running agent was found. Run {verb} from a script that the agent started, or as an administrator on the PC where the agent runs.");
            return (null, ExitCodes.Unavailable);
        }

        if (!endpoint.BaseAddress.IsLoopback)
        {
            await error.WriteLineAsync("The local channel address is not a loopback address; refusing to send the secret there.");
            return (null, ExitCodes.Failure);
        }

        return (endpoint, ExitCodes.Success);
    }

    /// <summary>The message of an error answer of the local channel (<c>{"error":"..."}</c>), or the raw text when it has another shape.</summary>
    /// <param name="body">The response body.</param>
    /// <returns>The message.</returns>
    public static string ErrorMessage(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out JsonElement message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Not an error object; show what came.
        }

        return body.Trim();
    }
}
