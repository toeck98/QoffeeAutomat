using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Link;

/// <summary>
/// The way a connection that the agent opened itself takes part in the optional features of QoffeeLink that only that peer uses (the hub, PLAN.md S20,
/// T905): the features it adds to <c>hello</c>, and the messages that the base protocol does not know. The base protocol stays exactly as it is for every
/// client; a frame that the extension does not claim is ignored as before (S07, forward compatibility).
/// </summary>
public interface ILinkExtension
{
    /// <summary>The optional features to announce in <c>hello.features</c> besides those of the manifest. Read when <c>hello</c> is built, so a change applies to the next connection.</summary>
    IReadOnlyList<string> HelloFeatures { get; }

    /// <summary>Offers a frame whose message type the base protocol does not know.</summary>
    /// <param name="message">The frame; <see cref="UnknownMessage.Raw"/> holds the whole object.</param>
    /// <param name="context">What the extension may do with the connection.</param>
    /// <param name="cancellationToken">Fires when the connection ends.</param>
    /// <returns>True when the extension handled the frame (and sent the result, if the frame was a request); false to let the base protocol ignore it.</returns>
    ValueTask<bool> TryHandleAsync(UnknownMessage message, ILinkExtensionContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Called once the handshake is complete, before the first frame after <c>client_hello</c> is read. The extension may send its first frames here, for
    /// example the <c>config_status</c> of the hub link (T908).
    /// </summary>
    /// <param name="peerFeatures">The features that the peer announced in <c>client_hello</c>; a feature that is not in the list must not be used.</param>
    /// <param name="context">What the extension may do with the connection.</param>
    /// <param name="cancellationToken">Fires when the connection ends.</param>
    /// <returns>A task that completes when the extension is done; it must not throw for problems of its own.</returns>
    ValueTask OnEstablishedAsync(IReadOnlyList<string> peerFeatures, ILinkExtensionContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>Called when the connection has ended (or never got established); the extension forgets what it kept for it.</summary>
    /// <param name="context">The connection that ended.</param>
    void OnClosed(ILinkExtensionContext context)
    {
    }
}

/// <summary>What an <see cref="ILinkExtension"/> may do with the connection that it serves.</summary>
public interface ILinkExtensionContext
{
    /// <summary>Queues a frame for the peer.</summary>
    /// <param name="message">The frame.</param>
    /// <returns>False when the connection is closing or the queue is full.</returns>
    bool Send(LinkMessage message);

    /// <summary>Closes the connection after the queued frames.</summary>
    /// <param name="closeCode">The WebSocket close code, see <see cref="CloseCodes"/>.</param>
    /// <param name="reason">A short reason without secrets.</param>
    void Close(int closeCode, string reason);
}
