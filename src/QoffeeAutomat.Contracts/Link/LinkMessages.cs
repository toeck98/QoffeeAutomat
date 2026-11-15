using System.Text.Json;
using System.Text.Json.Serialization;

namespace QoffeeAutomat.Contracts.Link;

/// <summary>
/// Base of all QoffeeLink frames. The envelope field <c>t</c> selects the concrete type;
/// see <see cref="LinkMessageConverter"/>.
/// </summary>
public abstract record LinkMessage
{
    /// <summary>The message type, the value of <c>t</c> (see <see cref="MessageTypes"/>).</summary>
    public abstract string T { get; }
}

/// <summary>A frame with a message type this version does not know. It is to be ignored.</summary>
/// <param name="Type">The value of <c>t</c>.</param>
/// <param name="Raw">The complete frame.</param>
public sealed record UnknownMessage(string Type, JsonElement Raw) : LinkMessage
{
    /// <inheritdoc />
    public override string T => Type;
}

/// <summary>Identity of an agent, used in <c>hello</c> and in the manifest.</summary>
public sealed record AgentInfo(
    string Id,
    string Name,
    string Version,
    string Os,
    string Arch,
    string Hostname,
    IReadOnlyList<string> Macs,
    string? OsVersion = null,
    Uri? UiUrl = null);

/// <summary>The range of protocol versions an agent supports.</summary>
public sealed record ProtocolRange(int Min, int Max);

/// <summary>Agent to client. First frame after connect, sent no matter who dialled.</summary>
public sealed record HelloMessage(
    ProtocolRange Protocol,
    AgentInfo Agent,
    string ManifestRev,
    string BootId,
    IReadOnlyList<string>? Features = null) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.Hello;
}

/// <summary>The kind of peer in <c>client_hello</c>.</summary>
public sealed record ClientInfo(string Kind, string Name, string Version);

/// <summary>Client to agent. Answer to <c>hello</c>; states the chosen protocol version.</summary>
public sealed record ClientHelloMessage(
    int Protocol,
    ClientInfo Client,
    IReadOnlyList<string>? Features = null) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.ClientHello;
}

/// <summary>Client to agent request: fetch the manifest.</summary>
public sealed record GetManifestMessage(long Id) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.GetManifest;
}

/// <summary>Agent to client. The manifest revision changed.</summary>
public sealed record ManifestChangedMessage(string Rev) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.ManifestChanged;
}

/// <summary>Client to agent request: replace the metric subscription set.</summary>
/// <param name="Id">Request id chosen by the sender.</param>
/// <param name="Metrics">The metric ids or all metrics.</param>
/// <param name="Intervals">Optional metric id to seconds: at most one value per interval.</param>
public sealed record SubscribeMessage(
    long Id,
    MetricSelection Metrics,
    IReadOnlyDictionary<string, double>? Intervals = null) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.Subscribe;
}

/// <summary>Agent to client. Changed values since the last frame.</summary>
/// <param name="Ts">Unix time in milliseconds.</param>
/// <param name="V">Metric id to value; a JSON null means unavailable.</param>
public sealed record StateMessage(long Ts, IReadOnlyDictionary<string, JsonElement> V) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.State;
}

/// <summary>The Home Assistant user on whose behalf a command runs.</summary>
public sealed record RunContext(string? UserId = null);

/// <summary>Client to agent request: execute a command.</summary>
/// <param name="Id">Request id chosen by the sender.</param>
/// <param name="Command">The command id.</param>
/// <param name="Vars">Variable values; absent means none.</param>
/// <param name="Wait">Wait for the end of the run; absent means <c>true</c>.</param>
/// <param name="Context">Optional run context.</param>
public sealed record RunMessage(
    long Id,
    string Command,
    IReadOnlyDictionary<string, JsonElement>? Vars = null,
    bool? Wait = null,
    RunContext? Context = null) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.Run;

    /// <summary>Whether the request waits for the end of the run (default <c>true</c>).</summary>
    [JsonIgnore]
    public bool ShouldWait => Wait ?? true;
}

/// <summary>Agent to client. Something happened on the PC.</summary>
/// <param name="Type">The event type.</param>
/// <param name="Ts">Unix time in milliseconds.</param>
/// <param name="Data">Optional event data (an object).</param>
public sealed record EventMessage(string Type, long Ts, JsonElement? Data = null) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.Event;
}

/// <summary>Agent to client. The agent goes away deliberately.</summary>
/// <param name="Reason">One of <see cref="ByeReasons"/>.</param>
public sealed record ByeMessage(string Reason) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.Bye;
}

/// <summary>The error of a failed request.</summary>
/// <param name="Code">One of <see cref="ErrorCodes"/>; unknown codes must be tolerated.</param>
/// <param name="Message">A human-readable message.</param>
public sealed record ErrorInfo(string Code, string Message);

/// <summary>Answer to a request: <c>Data</c> when <c>Ok</c>, <c>Error</c> otherwise.</summary>
public sealed record ResultMessage(
    long Id,
    bool Ok,
    JsonElement? Data = null,
    ErrorInfo? Error = null) : LinkMessage
{
    /// <inheritdoc />
    [JsonPropertyOrder(-1)]
    public override string T => MessageTypes.Result;

    /// <summary>Creates a successful result.</summary>
    /// <param name="id">The id of the request.</param>
    /// <param name="data">The result data (a JSON object).</param>
    /// <returns>The result message.</returns>
    public static ResultMessage Success(long id, JsonElement data) => new(id, true, data);

    /// <summary>Creates a failed result.</summary>
    /// <param name="id">The id of the request.</param>
    /// <param name="code">The error code, see <see cref="ErrorCodes"/>.</param>
    /// <param name="message">The error message.</param>
    /// <returns>The result message.</returns>
    public static ResultMessage Failure(long id, string code, string message) =>
        new(id, false, null, new ErrorInfo(code, message));
}

/// <summary>The data of a successful <c>run</c> result.</summary>
public sealed record RunResultData(
    string RunId,
    int? ExitCode = null,
    string? Stdout = null,
    string? Stderr = null,
    long? DurationMs = null,
    bool? TimedOut = null);

/// <summary>The values of <c>bye.reason</c>.</summary>
public static class ByeReasons
{
    /// <summary>The PC goes to sleep.</summary>
    public const string Sleep = "sleep";

    /// <summary>The PC hibernates.</summary>
    public const string Hibernate = "hibernate";

    /// <summary>The PC shuts down.</summary>
    public const string Shutdown = "shutdown";

    /// <summary>The PC restarts.</summary>
    public const string Restart = "restart";

    /// <summary>The agent service stops.</summary>
    public const string ServiceStop = "service_stop";

    /// <summary>The agent is being updated.</summary>
    public const string Update = "update";
}
