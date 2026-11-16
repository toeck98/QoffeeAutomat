using System.Globalization;
using System.Security.Cryptography;

namespace QoffeeAutomat.Core.Secrets;

/// <summary>
/// An <see cref="ISecretProtector"/> that encrypts with AES-256-GCM under a random key kept in a
/// file (PLAN.md S09 item 6: on Linux a key file with mode 0600). The key file is created on first
/// use. On Unix it is created with mode 0600 and the protector refuses to load a key file that group
/// or others can access. On Windows the directory ACL is set by the installer (S05).
/// </summary>
public sealed class FileKeyProtector : ISecretProtector, IDisposable
{
    /// <summary>The value of <see cref="Scheme"/>.</summary>
    public const string SchemeName = "file-key-aes256gcm";

    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int HeaderBytes = 1;
    private const byte FormatVersion = 1;

    private readonly byte[] _key;
    private bool _disposed;

    /// <summary>Loads the key file, or creates it when it does not exist.</summary>
    /// <param name="keyFilePath">The path of the key file, for example <c>secrets/secrets.key</c>.</param>
    /// <exception cref="SecretStoreException">The key file is too permissive or malformed.</exception>
    public FileKeyProtector(string keyFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyFilePath);
        _key = LoadOrCreateKey(Path.GetFullPath(keyFilePath));
    }

    /// <inheritdoc />
    public string Scheme => SchemeName;

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] blob = new byte[HeaderBytes + NonceBytes + TagBytes + plaintext.Length];
        blob[0] = FormatVersion;
        Span<byte> nonce = blob.AsSpan(HeaderBytes, NonceBytes);
        Span<byte> tag = blob.AsSpan(HeaderBytes + NonceBytes, TagBytes);
        Span<byte> cipher = blob.AsSpan(HeaderBytes + NonceBytes + TagBytes);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plaintext, cipher, tag, context);
        return blob;
    }

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (protectedData.Length < HeaderBytes + NonceBytes + TagBytes || protectedData[0] != FormatVersion)
        {
            throw new CryptographicException("The protected data has an unknown format.");
        }

        ReadOnlySpan<byte> nonce = protectedData.Slice(HeaderBytes, NonceBytes);
        ReadOnlySpan<byte> tag = protectedData.Slice(HeaderBytes + NonceBytes, TagBytes);
        ReadOnlySpan<byte> cipher = protectedData[(HeaderBytes + NonceBytes + TagBytes)..];

        byte[] plaintext = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(nonce, cipher, tag, plaintext, context);
        return plaintext;
    }

    /// <summary>Wipes the key from memory.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            SecretFiles.EnsureDirectory(directory);
        }

        if (!File.Exists(path))
        {
            CreateKeyFile(path);
        }

        EnsureOwnerOnly(path);

        byte[] key;
        try
        {
            key = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreException(SecretStoreFailure.InvalidKeyFile, "The key file cannot be read.", exception);
        }

        if (key.Length != KeyBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new SecretStoreException(
                SecretStoreFailure.InvalidKeyFile,
                $"The key file must be exactly {KeyBytes} bytes long.");
        }

        return key;
    }

    /// <summary>
    /// Writes the key to a temporary file with mode 0600 and renames it into place without
    /// replacing, so a crash never leaves a short key file and two starters cannot overwrite each other.
    /// </summary>
    private static void CreateKeyFile(string path)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        byte[] key = RandomNumberGenerator.GetBytes(KeyBytes);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(key);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another process created the key first; use theirs.
                File.Delete(temp);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new SecretStoreException(SecretStoreFailure.InvalidKeyFile, "The key file cannot be created.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void EnsureOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode forbidden =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        UnixFileMode mode = File.GetUnixFileMode(path);
        if ((mode & forbidden) != UnixFileMode.None)
        {
            throw new SecretStoreException(
                SecretStoreFailure.InsecureKeyFile,
                "The key file is accessible by group or others. Restrict it to the owner (chmod 600) before starting the agent.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done.
        }
    }
}
