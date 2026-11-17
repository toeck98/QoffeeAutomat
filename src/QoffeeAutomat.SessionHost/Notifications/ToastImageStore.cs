using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.SessionHost.Notifications;

/// <summary>
/// Keeps the pictures of notifications as files, because a toast shows a picture from a file (PLAN.md S11, T702). The agent hands
/// the picture over as base64 text; the store checks it once more, whatever the agent checked: not larger than
/// <see cref="NotifyToastSpec.MaxImageBytes"/> and really a PNG, JPEG, GIF or BMP by its first bytes. The files live in the
/// temporary folder of the user (which only that user and the system can read) under <c>QoffeeAutomat\toast</c> with names that
/// the store chooses, so nothing the caller sends ever becomes a path. Files older than a day are removed whenever a new one is
/// saved.
/// </summary>
public sealed class ToastImageStore
{
    /// <summary>How long a picture is kept: as long as its notification can be in the action center.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    private readonly string _directory;
    private readonly TimeProvider _time;

    /// <summary>Creates the store.</summary>
    /// <param name="directory">The folder; it is created when the first picture is saved.</param>
    /// <param name="time">The clock for the retention; null uses the system clock.</param>
    public ToastImageStore(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _directory = directory;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The folder of the current user.</summary>
    /// <returns>The path below the temporary folder.</returns>
    public static string DefaultDirectory() => Path.Combine(Path.GetTempPath(), "QoffeeAutomat", "toast");

    /// <summary>Checks a picture and saves it.</summary>
    /// <param name="base64">The picture as base64 text.</param>
    /// <param name="path">The full path of the saved file; null when the picture was refused.</param>
    /// <returns>Null when the picture was saved, otherwise why it was refused.</returns>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder cannot be written.</exception>
    public string? TrySave(string base64, out string? path)
    {
        ArgumentNullException.ThrowIfNull(base64);

        path = null;
        if (base64.Length > (NotifyToastSpec.MaxImageBytes / 3 * 4) + 16)
        {
            return "The picture is larger than 1 MiB.";
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return "The picture data is not valid base64.";
        }

        if (data.Length == 0 || data.Length > NotifyToastSpec.MaxImageBytes)
        {
            return "The picture is empty or larger than 1 MiB.";
        }

        string? extension = NotifyToastSpec.DetectImageExtension(data);
        if (extension is null)
        {
            return "The data is not a PNG, JPEG, GIF or BMP picture.";
        }

        Directory.CreateDirectory(_directory);
        Cleanup();
        string file = Path.Combine(_directory, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(file, data);
        path = file;
        return null;
    }

    private void Cleanup()
    {
        DateTime limit = (_time.GetUtcNow() - Retention).UtcDateTime;
        try
        {
            foreach (string file in Directory.EnumerateFiles(_directory))
            {
                string extension = Path.GetExtension(file);
                bool ours = extension is ".png" or ".jpg" or ".gif" or ".bmp";
                if (ours && File.GetLastWriteTimeUtc(file) < limit)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An old picture that cannot be removed now is removed the next time.
        }
    }
}
