namespace QoffeeAutomat.Contracts.Link;

/// <summary>WebSocket close codes used by QoffeeLink v1 (PLAN.md S07, D-039).</summary>
public static class CloseCodes
{
    /// <summary>Orderly close by either side.</summary>
    public const int Normal = 1000;

    /// <summary>Sent after <c>bye</c>.</summary>
    public const int GoingAway = 1001;

    /// <summary>A frame exceeds <see cref="ProtocolInfo.MaxFrameBytes"/>.</summary>
    public const int MessageTooBig = 1009;

    /// <summary>Unexpected failure in the agent.</summary>
    public const int InternalError = 1011;

    /// <summary>Handshake violation.</summary>
    public const int BadRequest = 4400;

    /// <summary>The link token was revoked while the connection was open.</summary>
    public const int Unauthorized = 4401;

    /// <summary>The client's protocol version is outside the agent's range.</summary>
    public const int UnsupportedVersion = 4406;
}
