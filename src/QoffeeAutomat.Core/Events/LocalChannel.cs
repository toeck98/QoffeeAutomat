using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QoffeeAutomat.Core.IO;

namespace QoffeeAutomat.Core.Events;

/// <summary>Names shared by the agent and the <c>emit</c> command.</summary>
public static class LocalChannelNames
{
    /// <summary>Environment variable that tells a child process where the local channel is (a loopback address).</summary>
    public const string UrlVariable = "QA_EMIT_URL";

    /// <summary>Environment variable that carries the secret of the local channel.</summary>
    public const string TokenVariable = "QA_EMIT_TOKEN";

    /// <summary>The path of the endpoint that accepts custom events.</summary>
    public const string EmitPath = "/local/v1/emit";

    /// <summary>The file in the secrets directory that holds address and secret for administrators who use the command line by hand.</summary>
    public const string FileName = "local-channel.json";
}

/// <summary>
/// The server side of the authenticated local channel that <c>qoffeeautomat emit</c> uses (PLAN.md
/// T217, S09). The agent creates a random secret for every run of the process. Everything the agent
/// starts (scripts and processes) inherits the secret through the environment, so a script can notify
/// Home Assistant; any other caller must read the file in the secrets directory, which only
/// administrators can open (S09), and is otherwise rejected. The endpoint listens on loopback only.
/// </summary>
public sealed class LocalChannel
{
    private const int TokenBytes = 32;

    private readonly byte[] _tokenHash;
    private volatile Uri? _baseAddress;

    /// <summary>Creates a channel with a fresh random secret.</summary>
    public LocalChannel()
        : this(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes)))
    {
    }

    /// <summary>Creates a channel with a known secret (tests).</summary>
    /// <param name="token">The secret.</param>
    public LocalChannel(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        Token = token;
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>The secret. Do not log it.</summary>
    public string Token { get; }

    /// <summary>The loopback address of the agent (scheme, host and port); null until the agent listens.</summary>
    public Uri? BaseAddress => _baseAddress;

    /// <summary>Records where the agent listens.</summary>
    /// <param name="baseAddress">A loopback address such as <c>http://127.0.0.1:8480/</c>.</param>
    public void SetBaseAddress(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        _baseAddress = baseAddress;
    }

    /// <summary>Checks a presented secret in constant time.</summary>
    /// <param name="presented">The secret from the caller; null or empty never matches.</param>
    /// <returns>True when it equals the channel's secret.</returns>
    public bool Verify(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(hash, _tokenHash);
    }

    /// <summary>The environment variables that give a child process access to the channel; empty until <see cref="BaseAddress"/> is known.</summary>
    /// <returns>The variables <see cref="LocalChannelNames.UrlVariable"/> and <see cref="LocalChannelNames.TokenVariable"/>.</returns>
    public IReadOnlyDictionary<string, string> ToEnvironment()
    {
        Uri? address = _baseAddress;
        if (address is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LocalChannelNames.UrlVariable] = address.AbsoluteUri,
            [LocalChannelNames.TokenVariable] = Token,
        };
    }

    /// <summary>Writes address and secret to a file readable by the owner only (mode 0600 on Unix; the secrets directory is restricted to administrators on Windows, S09).</summary>
    /// <param name="path">The file.</param>
    /// <param name="files">The file system; null uses the real one.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="InvalidOperationException">The address is not known yet.</exception>
    public Task WriteFileAsync(string path, IFileOperations? files, CancellationToken cancellationToken)
    {
        Uri address = _baseAddress ?? throw new InvalidOperationException("The local channel has no address yet.");

        var document = new JsonObject
        {
            ["url"] = address.AbsoluteUri,
            ["token"] = Token,
        };
        byte[] bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        return AtomicFile.WriteAsync(files ?? FileOperations.Instance, path, bytes, ownerOnly: true, cancellationToken);
    }

    /// <summary>Deletes the file; nothing happens when it does not exist.</summary>
    /// <param name="path">The file.</param>
    public static void DeleteFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale file holds a secret that no longer matches any running agent.
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return "LocalChannel(" + (_baseAddress?.AbsoluteUri ?? "not listening") + ")";
    }
}

/// <summary>Where a client finds the local channel: the address and the secret.</summary>
public sealed class LocalChannelEndpoint
{
    /// <summary>Creates an endpoint.</summary>
    /// <param name="baseAddress">The loopback address, for example <c>http://127.0.0.1:8480/</c>.</param>
    /// <param name="token">The secret.</param>
    public LocalChannelEndpoint(Uri baseAddress, string token)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrEmpty(token);

        BaseAddress = baseAddress;
        Token = token;
    }

    /// <summary>The loopback address.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The secret. Do not log it.</summary>
    public string Token { get; }

    /// <summary>Reads the endpoint from the environment of a process that the agent started.</summary>
    /// <param name="getVariable">Reads an environment variable; <see cref="Environment.GetEnvironmentVariable(string)"/> in production.</param>
    /// <returns>The endpoint, or null when the variables are missing or the address is not a valid absolute URI.</returns>
    public static LocalChannelEndpoint? FromEnvironment(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);

        return Create(getVariable(LocalChannelNames.UrlVariable), getVariable(LocalChannelNames.TokenVariable));
    }

    /// <summary>Reads the endpoint from the file that the agent wrote.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The endpoint, or null when the file does not exist, cannot be read by the caller or is malformed.</returns>
    public static async Task<LocalChannelEndpoint?> FromFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("url", out JsonElement url) && url.ValueKind == JsonValueKind.String
                && root.TryGetProperty("token", out JsonElement token) && token.ValueKind == JsonValueKind.String)
            {
                return Create(url.GetString(), token.GetString());
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return "LocalChannelEndpoint(" + BaseAddress.AbsoluteUri + ")";
    }

    private static LocalChannelEndpoint? Create(string? address, string? token)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrEmpty(token)
            || !Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return new LocalChannelEndpoint(uri, token);
    }
}
