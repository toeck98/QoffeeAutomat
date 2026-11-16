using System.Globalization;
using QoffeeAutomat.Core.IO;

namespace QoffeeAutomat.Core.Tests;

/// <summary>A directory below the system temp folder that is removed when the test ends.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        FullPath = Path.Combine(
            Path.GetTempPath(),
            "qoffeeautomat-tests-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(FullPath);
    }

    public string FullPath { get; }

    public string Combine(params string[] parts)
    {
        return Path.Combine(new[] { FullPath }.Concat(parts).ToArray());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(FullPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory does not fail a test.
        }
    }
}

/// <summary>
/// File operations with switches that simulate a crash at each step of an atomic write: while the
/// temporary file is written, before the rename, and a crash that also prevents the clean-up.
/// </summary>
internal sealed class FaultInjectingFileOperations : IFileOperations
{
    private readonly FileOperations _inner = FileOperations.Instance;

    /// <summary>The write stores half of the content in the temporary file and then fails.</summary>
    public bool TruncateWrite { get; set; }

    /// <summary>The rename fails.</summary>
    public bool FailMove { get; set; }

    /// <summary>Deleting fails, so a temporary file of a failed write stays behind like after a real crash.</summary>
    public bool FailDelete { get; set; }

    public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, bool ownerOnly, CancellationToken cancellationToken)
    {
        if (TruncateWrite)
        {
            await _inner.WriteAllBytesAsync(path, content[..(content.Length / 2)], ownerOnly, cancellationToken);
            throw new IOException("Simulated crash while writing the temporary file.");
        }

        await _inner.WriteAllBytesAsync(path, content, ownerOnly, cancellationToken);
    }

    public void Move(string source, string destination)
    {
        if (FailMove)
        {
            throw new IOException("Simulated crash before the rename.");
        }

        _inner.Move(source, destination);
    }

    public void Delete(string path)
    {
        if (FailDelete)
        {
            throw new IOException("Simulated crash that prevents the clean-up.");
        }

        _inner.Delete(path);
    }
}
