using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using QoffeeAutomat.Core.IO;
using QoffeeAutomat.Hub.Hosting;

namespace QoffeeAutomat.Hub.Security;

/// <summary>
/// The one-time setup token of the first run (PLAN.md S09 item 1, S20 "Hub security" item 1, T903). While the hub has no account, it
/// keeps a random token in <c>setup-token.txt</c> in the data directory (mode 0600) and prints it to the log, because in a container the
/// log is the one place the operator can read. The UI asks for the token once and lets the operator create the first administrator;
/// that deletes the file. The token is read from disk on every check, so deleting the file and restarting the hub makes a new one.
/// </summary>
public sealed class SetupTokenService
{
    private const int TokenBytes = 24;
    private const int MaxPresentedLength = 256;

    private readonly string _path;

    /// <summary>Creates the service.</summary>
    /// <param name="directory">The data directory (which makes sure that it exists).</param>
    public SetupTokenService(HubDataDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _path = directory.Paths.SetupTokenPath;
    }

    /// <summary>The full path of the token file.</summary>
    public string FilePath => _path;

    /// <summary>Tells whether the token file exists.</summary>
    /// <returns>True when the file exists.</returns>
    public bool Exists() => File.Exists(_path);

    /// <summary>Returns the token of the file, and creates the file with a new random token when there is none.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The token.</returns>
    public async Task<string> EnsureAsync(CancellationToken cancellationToken)
    {
        string? existing = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        string token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        byte[] bytes = Encoding.UTF8.GetBytes(token + "\n");
        await AtomicFile.WriteAsync(FileOperations.Instance, _path, bytes, ownerOnly: true, cancellationToken).ConfigureAwait(false);
        return token;
    }

    /// <summary>Checks a token against the file in constant time.</summary>
    /// <param name="presented">The token from the client.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True when the file exists and holds the token.</returns>
    public async Task<bool> VerifyAsync(string? presented, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presented) || presented.Length > MaxPresentedLength)
        {
            return false;
        }

        string? expected = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (expected is null)
        {
            return false;
        }

        // Hash both sides first so the comparison does not depend on the lengths.
        byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        byte[] presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented.Trim()));
        return CryptographicOperations.FixedTimeEquals(expectedHash, presentedHash);
    }

    /// <summary>Deletes the token file; nothing happens when it does not exist.</summary>
    /// <exception cref="IOException">The file cannot be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The hub may not delete the file.</exception>
    public void Delete() => File.Delete(_path);

    private async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            string text = (await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false)).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
