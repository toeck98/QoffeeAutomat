using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Tests.Pairing;

/// <summary>
/// A secrets directory in a temporary folder. <see cref="Open"/> returns a store over the same files
/// every time, so calling it again simulates an agent restart.
/// </summary>
internal sealed class TestSecrets : IDisposable
{
    private readonly TempDataDirectory _directory = new();
    private readonly List<IDisposable> _opened = [];

    public string Directory => _directory.Path;

    public string SecretsFile => System.IO.Path.Combine(_directory.Path, "secrets.json");

    public SecretStore Open()
    {
        var protector = new FileKeyProtector(System.IO.Path.Combine(_directory.Path, "secrets.key"));
        var store = new SecretStore(SecretsFile, protector);
        _opened.Add(protector);
        _opened.Add(store);
        return store;
    }

    public void Dispose()
    {
        foreach (IDisposable item in _opened)
        {
            item.Dispose();
        }

        _directory.Dispose();
    }
}

/// <summary>A connection that remembers how it was closed.</summary>
internal sealed class FakeLinkConnection : ILinkConnection
{
    public int? CloseCode { get; private set; }

    public string? Reason { get; private set; }

    public bool IsClosed => CloseCode is not null;

    public ValueTask CloseAsync(int closeCode, string reason, CancellationToken cancellationToken)
    {
        CloseCode = closeCode;
        Reason = reason;
        return ValueTask.CompletedTask;
    }
}
