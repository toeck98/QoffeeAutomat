using System.Globalization;

namespace QoffeeAutomat.Core.IO;

/// <summary>
/// Writes a file so that readers see either the old content or the new content, never a
/// truncated file: the bytes go to a temporary file next to the target, which is then renamed
/// over the target. A crash leaves at most a stale temporary file, which <see cref="IsTempFile"/>
/// recognises so the owner can sweep it at start-up.
/// </summary>
public static class AtomicFile
{
    /// <summary>The extension of temporary files.</summary>
    public const string TempExtension = ".tmp";

    /// <summary>Writes the content to the path atomically.</summary>
    /// <param name="operations">The file system operations.</param>
    /// <param name="path">The target file; its directory is created when missing.</param>
    /// <param name="content">The bytes to write.</param>
    /// <param name="ownerOnly">On Unix the file gets mode 0600.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the target holds the new content.</returns>
    public static async Task WriteAsync(
        IFileOperations operations,
        string path,
        ReadOnlyMemory<byte> content,
        bool ownerOnly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentException.ThrowIfNullOrEmpty(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + TempExtension;
        try
        {
            await operations.WriteAllBytesAsync(temp, content, ownerOnly, cancellationToken).ConfigureAwait(false);
            operations.Move(temp, path);
        }
        catch
        {
            TryDelete(operations, temp);
            throw;
        }
    }

    /// <summary>Tells whether a file name looks like a temporary file of <see cref="WriteAsync"/>.</summary>
    /// <param name="path">A file name or path.</param>
    /// <returns>True for names of the form <c>target.&lt;32 hex digits&gt;.tmp</c>.</returns>
    public static bool IsTempFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        const int hexLength = 32;
        string name = Path.GetFileName(path);
        if (!name.EndsWith(TempExtension, StringComparison.Ordinal))
        {
            return false;
        }

        int hexEnd = name.Length - TempExtension.Length;
        int hexStart = hexEnd - hexLength;
        if (hexStart < 2 || name[hexStart - 1] != '.')
        {
            return false;
        }

        for (int index = hexStart; index < hexEnd; index++)
        {
            if (!char.IsAsciiHexDigitLower(name[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDelete(IFileOperations operations, string path)
    {
        try
        {
            operations.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The caller is already failing; a stale temporary file is swept at the next start.
        }
    }
}
