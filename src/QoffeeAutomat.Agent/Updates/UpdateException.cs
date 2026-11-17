namespace QoffeeAutomat.Agent.Updates;

/// <summary>
/// An update step failed or was refused on purpose (a wrong checksum, an untrusted signature, a download that is too large). The
/// message is meant for the log and for the run result that Home Assistant shows; it never contains a secret.
/// </summary>
public sealed class UpdateException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public UpdateException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public UpdateException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public UpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
