namespace QoffeeAutomat.Contracts.Link;

/// <summary>Constants of QoffeeLink protocol version 1 (PLAN.md S07).</summary>
public static class ProtocolInfo
{
    /// <summary>The protocol version implemented by these contracts.</summary>
    public const int Version = 1;

    /// <summary>The WebSocket subprotocol.</summary>
    public const string SubProtocol = "qoffeelink.v1";

    /// <summary>Path of the WebSocket endpoint.</summary>
    public const string LinkPath = "/link/v1";

    /// <summary>Path of the pairing endpoint (HTTPS POST, no bearer token).</summary>
    public const string PairPath = "/link/v1/pair";

    /// <summary>Maximum size of one frame in bytes (1 MiB).</summary>
    public const int MaxFrameBytes = 1024 * 1024;
}
