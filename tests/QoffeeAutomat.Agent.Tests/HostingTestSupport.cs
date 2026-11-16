namespace QoffeeAutomat.Agent.Tests;

/// <summary>A temporary data directory that is removed on dispose.</summary>
internal sealed class TempDataDirectory : IDisposable
{
    public TempDataDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qoffeeautomat-agent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a log file may still be open on Windows.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
