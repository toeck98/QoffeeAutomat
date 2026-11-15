namespace QoffeeAutomat.Contracts.Link;

/// <summary>The values of the envelope field <c>t</c> (PLAN.md S07).</summary>
public static class MessageTypes
{
    /// <summary>Agent to client, first frame after connect.</summary>
    public const string Hello = "hello";

    /// <summary>Client to agent, answer to <see cref="Hello"/>.</summary>
    public const string ClientHello = "client_hello";

    /// <summary>Client to agent request.</summary>
    public const string GetManifest = "get_manifest";

    /// <summary>Agent to client, the manifest revision changed.</summary>
    public const string ManifestChanged = "manifest_changed";

    /// <summary>Client to agent request.</summary>
    public const string Subscribe = "subscribe";

    /// <summary>Agent to client, changed metric values.</summary>
    public const string State = "state";

    /// <summary>Client to agent request.</summary>
    public const string Run = "run";

    /// <summary>Agent to client, something happened on the PC.</summary>
    public const string Event = "event";

    /// <summary>Agent to client, the agent goes away deliberately.</summary>
    public const string Bye = "bye";

    /// <summary>Answer to a request, either direction.</summary>
    public const string Result = "result";
}
