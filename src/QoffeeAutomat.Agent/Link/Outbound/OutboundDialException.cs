namespace QoffeeAutomat.Agent.Link.Outbound;

/// <summary>Why a dial attempt failed; it decides whether the connector tries again.</summary>
public enum OutboundFailure
{
    /// <summary>The peer cannot be reached or did not answer in time (DNS, TCP, timeout). Try again later.</summary>
    Unreachable,

    /// <summary>The TLS certificate of the peer is not acceptable (not trusted, or not the pinned one). Try again later: the peer may be misconfigured for a moment, or this is a captive portal.</summary>
    Certificate,

    /// <summary>The peer answered the upgrade with an HTTP error other than 401. Try again later.</summary>
    Refused,

    /// <summary>The peer does not speak QoffeeLink (no WebSocket, wrong sub-protocol). Try again later.</summary>
    Protocol,

    /// <summary>The peer rejected the credential (HTTP 401 or 403). Do not try again until the credential changes.</summary>
    Unauthorized,
}

/// <summary>A dial attempt of an outbound link failed. The message never contains the credential.</summary>
public sealed class OutboundDialException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public OutboundDialException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public OutboundDialException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and a cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public OutboundDialException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception with a reason.</summary>
    /// <param name="failure">Why the attempt failed.</param>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause, if any.</param>
    public OutboundDialException(OutboundFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>Why the attempt failed.</summary>
    public OutboundFailure Failure { get; }
}
