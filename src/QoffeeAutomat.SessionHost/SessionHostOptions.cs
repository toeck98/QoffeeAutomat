using System.Globalization;
using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.SessionHost;

/// <summary>
/// The command line of the SessionHost: <c>--session &lt;id&gt;</c> (default: the session of this process),
/// <c>--pipe &lt;name&gt;</c> (default <see cref="SessionIpc.PipeName"/>) and <c>--agent-image &lt;path&gt;</c> (the executable
/// that the pipe server must run; default: the agent executable next to this one). The agent passes only the first.
/// A user who starts the SessionHost with other values only changes whom <i>that</i> instance trusts.
/// </summary>
/// <param name="SessionId">The Windows session of this process, announced in <c>hello</c>.</param>
/// <param name="PipeName">The pipe name.</param>
/// <param name="AgentImagePath">The executable that the pipe server must run.</param>
public sealed record SessionHostOptions(int SessionId, string PipeName, string AgentImagePath)
{
    /// <summary>Parses the command line.</summary>
    /// <param name="arguments">The arguments.</param>
    /// <param name="currentSessionId">The session of this process.</param>
    /// <param name="baseDirectory">The directory of this executable.</param>
    /// <param name="error">Why the command line is wrong; null when it is fine.</param>
    /// <returns>The options; null when the command line is wrong.</returns>
    public static SessionHostOptions? Parse(IReadOnlyList<string> arguments, int currentSessionId, string baseDirectory, out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        int sessionId = currentSessionId;
        string pipeName = SessionIpc.PipeName;
        string agentImage = Path.Combine(baseDirectory, SessionIpc.AgentFileName);
        error = null;

        for (int index = 0; index < arguments.Count; index++)
        {
            string name = arguments[index];
            if (index + 1 >= arguments.Count)
            {
                error = $"The option '{name}' needs a value.";
                return null;
            }

            string value = arguments[++index];
            switch (name)
            {
                case "--session":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out sessionId) || sessionId <= 0)
                    {
                        error = "The session id must be a positive number.";
                        return null;
                    }

                    break;
                case "--pipe":
                    if (string.IsNullOrWhiteSpace(value) || value.Contains('\\', StringComparison.Ordinal) || value.Contains('/', StringComparison.Ordinal))
                    {
                        error = "The pipe name must be a plain name.";
                        return null;
                    }

                    pipeName = value;
                    break;
                case "--agent-image":
                    agentImage = value;
                    break;
                default:
                    error = $"Unknown option '{name}'.";
                    return null;
            }
        }

        return new SessionHostOptions(sessionId, pipeName, agentImage);
    }
}
