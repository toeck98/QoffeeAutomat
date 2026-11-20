using System.Text.Json;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Hub.Config.Rollout;

/// <summary>The documents of a generation split into <c>config_documents</c> frames.</summary>
/// <param name="Chunks">The documents of each frame, in order; empty when <paramref name="TooLarge"/> is set.</param>
/// <param name="TooLarge">The first document that does not fit into a frame on its own; null when every document fits.</param>
public sealed record DocumentChunks(IReadOnlyList<IReadOnlyList<HubDocument>> Chunks, HubDocument? TooLarge);

/// <summary>
/// Splits the documents that an agent asked for over as many <c>config_documents</c> frames as the frame limit needs (PLAN.md S20): documents are packed in order, a
/// frame is closed when the next document would push it over the limit, and a document that cannot fit even into a frame of its own is reported. The size of a frame is
/// measured with the serializer that writes it, with the longest possible request id, so a frame that was planned to fit does.
/// </summary>
public static class DocumentChunker
{
    /// <summary>Room that is kept free in every frame for the framing of the transport.</summary>
    public const int Slack = 512;

    /// <summary>Splits documents over <c>config_documents</c> frames.</summary>
    /// <param name="generation">The generation the documents belong to.</param>
    /// <param name="documents">The documents, in the order they are sent.</param>
    /// <param name="maxFrameBytes">The frame limit of the link.</param>
    /// <returns>The documents of each frame, or the first document that does not fit into a frame of its own.</returns>
    public static DocumentChunks Split(long generation, IReadOnlyList<HubDocument> documents, int maxFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(documents);

        long budget = (long)maxFrameBytes - Slack;
        int envelope = Measure(generation, []);
        var chunks = new List<IReadOnlyList<HubDocument>>();
        var current = new List<HubDocument>();
        long size = envelope;
        foreach (HubDocument document in documents)
        {
            // One more document costs its own size and a comma.
            long documentSize = Measure(generation, [document]) - envelope + 1;
            if (envelope + documentSize > budget)
            {
                return new DocumentChunks([], document);
            }

            if (current.Count > 0 && size + documentSize > budget)
            {
                chunks.Add(current);
                current = [];
                size = envelope;
            }

            current.Add(document);
            size += documentSize;
        }

        if (current.Count > 0)
        {
            chunks.Add(current);
        }

        return new DocumentChunks(chunks, null);
    }

    private static int Measure(long generation, IReadOnlyList<HubDocument> documents)
    {
        // "final": false is the longer spelling, long.MaxValue the longest id.
        var message = new ConfigDocumentsMessage(long.MaxValue, generation, documents, false);
        return JsonSerializer.SerializeToUtf8Bytes(message, HubJsonContext.Default.ConfigDocumentsMessage).Length;
    }
}
