namespace QoffeeAutomat.Core.IO;

/// <summary>The real file system implementation of <see cref="IFileOperations"/>.</summary>
public sealed class FileOperations : IFileOperations
{
    /// <summary>The shared instance; the class holds no state.</summary>
    public static FileOperations Instance { get; } = new();

    /// <inheritdoc />
    public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, bool ownerOnly, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        };

        if (ownerOnly && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        FileStream stream = new(path, options);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            RandomAccess.FlushToDisk(stream.SafeFileHandle);
        }
    }

    /// <inheritdoc />
    public void Move(string source, string destination)
    {
        File.Move(source, destination, overwrite: true);
    }

    /// <inheritdoc />
    public void Delete(string path)
    {
        File.Delete(path);
    }
}
