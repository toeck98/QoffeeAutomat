using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Config.HubLayer;
using QoffeeAutomat.Core.IO;

namespace QoffeeAutomat.Agent.Tests.Hub.ConfigSync;

/// <summary>Builds the documents and generations that a hub sends (PLAN.md S20, T908).</summary>
internal static class HubDocs
{
    public static readonly PolicyInfo Open = new(LocalConfigPolicies.Allow, DirectLinkPolicies.Allow);

    public static readonly PolicyInfo Locked = new(LocalConfigPolicies.Locked, DirectLinkPolicies.Allow);

    public static readonly PolicyInfo DirectDenied = new(LocalConfigPolicies.Allow, DirectLinkPolicies.Deny);

    /// <summary>A process command that passes the validators; the executable does not exist, tests that run it use a recording launcher.</summary>
    public static HubDocument Command(string id, string name = "Hub command", bool enabled = true)
    {
        return Document(
            DocumentKinds.Command,
            id,
            $$"""{"schemaVersion":2,"id":"{{id}}","name":"{{name}}","enabled":{{(enabled ? "true" : "false")}},"exposeToHa":true,"runAs":"system","action":{"type":"process","file":"hub-tool","args":["hello"]},"captureOutput":true}""");
    }

    /// <summary>A command that does not pass the validators: no name and an action without an executable.</summary>
    public static HubDocument InvalidCommand(string id)
    {
        return Document(
            DocumentKinds.Command,
            id,
            $$"""{"schemaVersion":2,"id":"{{id}}","name":"","enabled":true,"exposeToHa":true,"runAs":"system","action":{"type":"process"} }""");
    }

    /// <summary>A PowerShell command, which a Linux agent cannot run.</summary>
    public static HubDocument PowerShellCommand(string id)
    {
        return Document(
            DocumentKinds.Command,
            id,
            $$"""{"schemaVersion":2,"id":"{{id}}","name":"PowerShell","enabled":true,"exposeToHa":true,"runAs":"user","action":{"type":"powershell","source":"file","script":"tool.ps1","engine":"auto"},"captureOutput":true}""");
    }

    public static HubDocument Sensor(string id, string name = "Hub sensor")
    {
        return Document(
            DocumentKinds.Sensor,
            id,
            $$"""{"schemaVersion":1,"id":"{{id}}","name":"{{name}}","enabled":true,"exposeToHa":true,"kind":"binary_sensor","valueType":"bool","source":{"type":"process_running","process":"hub-process"} }""");
    }

    public static HubDocument MetricOverrides()
    {
        return Document(
            DocumentKinds.MetricOverrides,
            DocumentKinds.MetricOverrides,
            """{"schemaVersion":1,"overrides":{"cpu.usage":{"intervalSeconds":10}}}""");
    }

    public static HubDocument Script(string name, string text)
    {
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(text, ScriptJsonContext.Default.String));
        return DocumentHasher.CreateDocument(DocumentKinds.Script, name, body.RootElement.Clone());
    }

    public static HubDocument Variables(string id, string name, string value)
    {
        return Document(DocumentKinds.Variables, id, "{\"" + name + "\":\"" + value + "\"}");
    }

    public static HubDocument Document(string kind, string id, string json)
    {
        using JsonDocument body = JsonDocument.Parse(json);
        return DocumentHasher.CreateDocument(kind, id, body.RootElement.Clone());
    }

    /// <summary>A generation with every document sent in full.</summary>
    public static HubGeneration Generation(long number, PolicyInfo policy, params HubDocument[] documents)
    {
        return new HubGeneration(number, policy, [.. documents.Select(document => new HubGenerationEntry(document.Kind, document.Id, document.Hash, document.Body))]);
    }

    /// <summary>An entry without a body: the agent keeps the document of the applied generation.</summary>
    public static HubGenerationEntry Carried(HubDocument document) => new(document.Kind, document.Id, document.Hash);

    /// <summary>A local command, saved the way the API saves one.</summary>
    public static async Task<CommandDocument> SaveLocalCommandAsync(IConfigStore store, string id, string name)
    {
        CommandDocument? document = JsonSerializer.Deserialize(
            $$"""{"schemaVersion":2,"id":"{{id}}","name":"{{name}}","enabled":true,"exposeToHa":true,"runAs":"system","action":{"type":"process","file":"local-tool","args":["x"]},"captureOutput":true}""",
            ConfigJsonContext.Default.CommandDocument);
        SaveResult<CommandDocument> saved = await store.Local.SaveAsync(document!, 0, CancellationToken.None);
        Assert.True(saved.IsApplied, "The local command was not saved: " + saved.Status);
        return saved.Document!;
    }
}

/// <summary>Source-generated serialization of a string for <see cref="HubDocs.Script"/>; no reflection is used.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class ScriptJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

/// <summary>A config store in a temporary directory that can be closed and opened again, which is what a restart of the agent looks like.</summary>
internal sealed class StoreRig : IDisposable
{
    private readonly TempDataDirectory _data = new();
    private readonly List<ConfigStore> _stores = [];
    private readonly IFileOperations? _files;
    private readonly CommandPlatformSupport? _support;

    public StoreRig(IFileOperations? files = null, CommandPlatformSupport? support = null)
    {
        _files = files;
        _support = support;
        Store = Create(files);
    }

    public ConfigStore Store { get; private set; }

    public string ConfigDirectory => Path.Combine(_data.Path, "config");

    public string HubDirectory => Path.Combine(ConfigDirectory, "hub");

    public async Task LoadAsync()
    {
        await Store.LoadAsync(CancellationToken.None);
    }

    /// <summary>Starts a second store on the same directories with the real file system: the agent after a restart.</summary>
    public async Task<ConfigStore> ReopenAsync()
    {
        Store = Create(files: null);
        await Store.LoadAsync(CancellationToken.None);
        return Store;
    }

    private ConfigStore Create(IFileOperations? files)
    {
        var store = new ConfigStore(new ConfigStoreOptions
        {
            ConfigDirectory = ConfigDirectory,
            ScriptsDirectory = Path.Combine(_data.Path, "scripts"),
            EnableWatcher = false,
            FileOperations = files ?? _files,
            PlatformSupport = _support,
        });
        _stores.Add(store);
        return store;
    }

    public void Dispose()
    {
        foreach (ConfigStore store in _stores)
        {
            store.Dispose();
        }

        _data.Dispose();
    }
}

/// <summary>
/// File operations that stop working at a chosen step, as a power failure does: the call at <see cref="FailAt"/> throws and so does every call after it, and
/// nothing is cleaned up. <see cref="Calls"/> counts the writes and renames of the apply, so a test can try every step.
/// </summary>
internal sealed class PowerFailureFileOperations : IFileOperations
{
    private int _calls;
    private bool _off;

    /// <summary>The number of the call that fails (1 is the first write or rename); 0 never fails.</summary>
    public int FailAt { get; set; }

    /// <summary>The number of writes and renames that were attempted.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Resets the counter and switches the power on again; <paramref name="failAt"/> is the step to fail at, counted from now.</summary>
    public void Arm(int failAt)
    {
        _calls = 0;
        _off = false;
        FailAt = failAt;
    }

    public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, bool ownerOnly, CancellationToken cancellationToken)
    {
        Step();
        await FileOperations.Instance.WriteAllBytesAsync(path, content, ownerOnly, cancellationToken);
    }

    public void Move(string source, string destination)
    {
        Step();
        FileOperations.Instance.Move(source, destination);
    }

    public void Delete(string path)
    {
        // A computer without power deletes nothing; the leftovers are swept at the next start.
        if (!_off)
        {
            FileOperations.Instance.Delete(path);
        }
    }

    private void Step()
    {
        int call = Interlocked.Increment(ref _calls);
        if (_off || (FailAt > 0 && call >= FailAt))
        {
            _off = true;
            throw new IOException("Simulated power failure at step " + call);
        }
    }
}
