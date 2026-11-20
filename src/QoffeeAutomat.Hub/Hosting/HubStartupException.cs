namespace QoffeeAutomat.Hub.Hosting;

/// <summary>The hub cannot start for a reason that the operator can fix; the message says what to do.</summary>
public sealed class HubStartupException : Exception
{
    /// <summary>Creates an exception without a message.</summary>
    public HubStartupException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong and what to do about it.</param>
    public HubStartupException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    /// <param name="message">What went wrong and what to do about it.</param>
    /// <param name="innerException">The cause.</param>
    public HubStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
