using System.Runtime.CompilerServices;
using System.Text;

namespace QoffeeAutomat.Ui;

/// <summary>One event of a Server-Sent Events stream.</summary>
/// <param name="Name">The event name; <c>message</c> when the stream named none.</param>
/// <param name="Data">The data lines joined with line feeds.</param>
public readonly record struct ServerSentEvent(string Name, string Data);

/// <summary>
/// Reads a <c>text/event-stream</c> body (the live metric values and run output of the agent, PLAN.md S08). It follows the
/// format of the WHATWG specification that the agent writes: <c>event:</c> and <c>data:</c> lines, a blank line ends an
/// event, lines that start with a colon are comments.
/// </summary>
public static class ServerSentEventReader
{
    private const string DefaultName = "message";

    /// <summary>Reads the events until the stream ends or the token is cancelled.</summary>
    /// <param name="reader">The body of the response.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    /// <returns>The events in order.</returns>
    public static async IAsyncEnumerable<ServerSentEvent> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? name = null;
        var data = new StringBuilder();
        bool hasData = false;
        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                // An event that the stream did not terminate with a blank line is dropped, as the specification says.
                yield break;
            }

            if (line.Length == 0)
            {
                if (hasData)
                {
                    yield return new ServerSentEvent(name ?? DefaultName, data.ToString());
                }

                name = null;
                data.Clear();
                hasData = false;
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            string field = colon < 0 ? line : line[..colon];
            string value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "event":
                    name = value;
                    break;
                case "data":
                    if (hasData)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    hasData = true;
                    break;
            }
        }
    }
}
