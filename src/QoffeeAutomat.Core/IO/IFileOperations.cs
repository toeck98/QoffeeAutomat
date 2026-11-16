namespace QoffeeAutomat.Core.IO;

/// <summary>
/// The few file system operations that <see cref="AtomicFile"/> is built from. The seam exists so
/// tests can inject faults (a crash between the write and the rename) without touching the real
/// implementation.
/// </summary>
public interface IFileOperations
{
    /// <summary>Creates a new file and writes the content to it, flushed to disk before returning.</summary>
    /// <param name="path">The file; it must not exist yet.</param>
    /// <param name="content">The bytes to write.</param>
    /// <param name="ownerOnly">On Unix the file is created with mode 0600. Ignored on Windows.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the content is on disk.</returns>
    Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, bool ownerOnly, CancellationToken cancellationToken);

    /// <summary>Renames a file, replacing the destination atomically.</summary>
    /// <param name="source">The existing file.</param>
    /// <param name="destination">The target; replaced when it exists.</param>
    void Move(string source, string destination);

    /// <summary>Deletes a file; nothing happens when it does not exist.</summary>
    /// <param name="path">The file.</param>
    void Delete(string path);
}
