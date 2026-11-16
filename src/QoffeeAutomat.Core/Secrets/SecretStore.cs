using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Core.IO;

namespace QoffeeAutomat.Core.Secrets;

/// <summary>
/// The secrets file, <c>secrets/secrets.json</c>: a JSON document whose values are blobs made by an
/// <see cref="ISecretProtector"/>, each bound to its secret's name. Writes are atomic and the file is
/// created with mode 0600 on Unix. A file that cannot be read is never replaced, so a damaged file
/// cannot silently destroy the secrets that are still in it.
/// </summary>
public sealed class SecretStore : ISecretStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly IFileOperations _files;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _blobs;

    /// <summary>Creates a store; the file is read on first use.</summary>
    /// <param name="filePath">The path of <c>secrets.json</c>.</param>
    /// <param name="protector">Encrypts and decrypts the values.</param>
    /// <param name="fileOperations">The file system operations; null uses the real file system.</param>
    public SecretStore(string filePath, ISecretProtector protector, IFileOperations? fileOperations = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(protector);

        _path = Path.GetFullPath(filePath);
        _protector = protector;
        _files = fileOperations ?? FileOperations.Instance;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetBytesAsync(string name, CancellationToken cancellationToken)
    {
        RequireValidName(name);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> blobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!blobs.TryGetValue(name, out string? encoded))
            {
                return null;
            }

            return Unprotect(name, encoded);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetStringAsync(string name, CancellationToken cancellationToken)
    {
        byte[]? bytes = await GetBytesAsync(name, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new SecretStoreException(SecretStoreFailure.DecryptionFailed, $"The secret '{name}' is not text.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <inheritdoc />
    public async Task SetBytesAsync(string name, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        RequireValidName(name);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> blobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            byte[] blob = _protector.Protect(value.Span, Encoding.UTF8.GetBytes(name));

            // Write a copy first; memory only changes when the file is safely on disk.
            var updated = new Dictionary<string, string>(blobs, StringComparer.Ordinal)
            {
                [name] = Convert.ToBase64String(blob),
            };

            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _blobs = updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetStringAsync(string name, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            await SetBytesAsync(name, bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        RequireValidName(name);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> blobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!blobs.ContainsKey(name))
            {
                return false;
            }

            var updated = new Dictionary<string, string>(blobs, StringComparer.Ordinal);
            updated.Remove(name);
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            _blobs = updated;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> blobs = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return [.. blobs.Keys.Order(StringComparer.Ordinal)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the lock of the store.</summary>
    public void Dispose()
    {
        _gate.Dispose();
    }

    private static void RequireValidName(string name)
    {
        if (!SecretNames.IsValid(name))
        {
            throw new ArgumentException("A secret name must match ^[a-z][a-z0-9_.-]{0,127}$.", nameof(name));
        }
    }

    private byte[] Unprotect(string name, string encoded)
    {
        try
        {
            byte[] blob = Convert.FromBase64String(encoded);
            return _protector.Unprotect(blob, Encoding.UTF8.GetBytes(name));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new SecretStoreException(
                SecretStoreFailure.DecryptionFailed,
                $"The secret '{name}' cannot be decrypted. The key may have changed or the file is damaged.",
                exception);
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_blobs is not null)
        {
            return _blobs;
        }

        if (!File.Exists(_path))
        {
            _blobs = new Dictionary<string, string>(StringComparer.Ordinal);
            return _blobs;
        }

        SecretsFile? file;
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            file = JsonSerializer.Deserialize(bytes, SecretsJsonContext.Default.SecretsFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new SecretStoreException(
                SecretStoreFailure.UnreadableStore,
                "The secrets file cannot be read. It was left untouched.",
                exception);
        }

        if (file is null || file.SchemaVersion != CurrentSchemaVersion)
        {
            throw new SecretStoreException(
                SecretStoreFailure.UnreadableStore,
                "The secrets file has an unknown format. It was left untouched.");
        }

        if (!string.Equals(file.Protector, _protector.Scheme, StringComparison.Ordinal))
        {
            throw new SecretStoreException(
                SecretStoreFailure.ProtectorMismatch,
                $"The secrets file was written with the protector '{file.Protector}', but '{_protector.Scheme}' is in use.");
        }

        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (file.Secrets is not null)
        {
            foreach ((string name, string value) in file.Secrets)
            {
                if (SecretNames.IsValid(name) && value is not null)
                {
                    blobs[name] = value;
                }
            }
        }

        _blobs = blobs;
        return blobs;
    }

    private async Task SaveAsync(Dictionary<string, string> blobs, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            SecretFiles.EnsureDirectory(directory);
        }

        var document = new SecretsFile(
            CurrentSchemaVersion,
            _protector.Scheme,
            new SortedDictionary<string, string>(blobs, StringComparer.Ordinal));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, SecretsJsonContext.Default.SecretsFile);
        await AtomicFile.WriteAsync(_files, _path, bytes, ownerOnly: true, cancellationToken).ConfigureAwait(false);
    }
}
