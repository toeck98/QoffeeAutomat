using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Core.IO;

namespace QoffeeAutomat.Agent.Security.Auth;

/// <summary>
/// The setup token file, <c>secrets/setup-token.txt</c> (PLAN.md S09 item 1). The agent writes a random
/// token on the first run, and <c>qoffeeautomat reset-password</c> writes a new one. The file is
/// readable by administrators only (mode 0600 on Unix, the secrets directory ACL on Windows). While the
/// file exists, the UI can set a password with that token; setting a password deletes the file. The
/// token is read from disk on every check, so a token written by the command-line verb works without
/// restarting the agent.
/// </summary>
public sealed class SetupTokenService
{
    /// <summary>The name of the token file in the secrets directory.</summary>
    public const string FileName = "setup-token.txt";

    private const int TokenBytes = 24;

    private readonly string _path;

    /// <summary>Creates the service.</summary>
    /// <param name="paths">The data directories.</param>
    public SetupTokenService(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _path = Path.Combine(paths.SecretsDirectory, FileName);
    }

    /// <summary>The full path of the token file.</summary>
    public string FilePath => _path;

    /// <summary>Tells whether a setup token file exists, which means setup is possible.</summary>
    /// <returns>True when the file exists.</returns>
    public bool Exists() => File.Exists(_path);

    /// <summary>Writes a new random token, replacing an existing one.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not write to the secrets directory.</exception>
    public Task CreateAsync(CancellationToken cancellationToken) => CreateFileAsync(_path, cancellationToken);

    /// <summary>Checks a token against the file in constant time.</summary>
    /// <param name="presented">The token from the client.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True when the file exists and holds the token.</returns>
    public async Task<bool> VerifyAsync(string? presented, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presented) || presented.Length > 256)
        {
            return false;
        }

        string expected;
        try
        {
            expected = (await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (expected.Length == 0)
        {
            return false;
        }

        // Hash both sides first so the comparison does not depend on the lengths.
        byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        byte[] presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented.Trim()));
        return CryptographicOperations.FixedTimeEquals(expectedHash, presentedHash);
    }

    /// <summary>Deletes the token file; nothing happens when it does not exist.</summary>
    /// <exception cref="IOException">The file cannot be deleted; setup stays possible until it is.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not delete the file.</exception>
    public void Delete() => File.Delete(_path);

    /// <summary>Writes a new random token to a file that only its owner can read.</summary>
    /// <param name="path">The token file.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public static Task CreateFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        string token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        byte[] bytes = Encoding.UTF8.GetBytes(token + "\n");
        return AtomicFile.WriteAsync(FileOperations.Instance, path, bytes, ownerOnly: true, cancellationToken);
    }
}
