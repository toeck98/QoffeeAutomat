using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Manifest;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Simulated;
using LinkOptions = QoffeeAutomat.Agent.Link.LinkOptions;

namespace QoffeeAutomat.Agent.Tests.Link;

/// <summary>
/// Replaces the launcher of processes: every start is recorded and no process runs, so a test can tell whether
/// a command was started. A test can hold the launches back to keep a run in progress.
/// </summary>
internal sealed class RecordingProcessLauncher : IProcessLauncher
{
    private readonly object _gate = new();
    private readonly List<ProcessLaunchRequest> _launches = [];
    private TaskCompletionSource? _hold;

    /// <summary>The requests that reached the launcher, in order.</summary>
    public ProcessLaunchRequest[] Launches
    {
        get
        {
            lock (_gate)
            {
                return [.. _launches];
            }
        }
    }

    /// <summary>Keeps every launch that follows waiting until <see cref="Release"/> is called.</summary>
    public void Hold()
    {
        lock (_gate)
        {
            _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Lets the held launches complete.</summary>
    public void Release()
    {
        lock (_gate)
        {
            _hold?.TrySetResult();
        }
    }

    public async ValueTask<ExecutionResult> LaunchAsync(string runAs, ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        Task? hold;
        lock (_gate)
        {
            _launches.Add(request);
            hold = _hold?.Task;
        }

        if (hold is not null)
        {
            await hold.WaitAsync(cancellationToken);
        }

        return new ExecutionResult(0, "ran " + string.Join(' ', request.Arguments), string.Empty);
    }
}

/// <summary>
/// The agent with the simulated platform in a temporary directory, a paired link client on demand and the
/// handles that the link tests need. Dispose the clients before the host.
/// </summary>
internal sealed class LinkTestHost : IAsyncDisposable
{
    public const string CommandId = "set_level";

    private readonly TempDataDirectory _data = new();
    private int _clients;
    private bool _disposed;

    /// <param name="options">Limits and timings of the link server; null keeps the defaults of the specification.</param>
    public LinkTestHost(LinkOptions? options = null)
    {
        Agent = new TestAgent(_data.Path, services =>
        {
            services.RemoveAll<IProcessLauncher>();
            services.AddSingleton<IProcessLauncher>(Launcher);
            if (options is not null)
            {
                services.RemoveAll<LinkOptions>();
                services.AddSingleton(options);
            }
        });
    }

    public TestAgent Agent { get; }

    public RecordingProcessLauncher Launcher { get; } = new();

    public LinkServer Server => Agent.Services.GetRequiredService<LinkServer>();

    public PairingService Pairing => Agent.Services.GetRequiredService<PairingService>();

    public SimulatedPlatform Platform => Agent.Services.GetRequiredService<SimulatedPlatform>();

    public IEventBus Events => Agent.Services.GetRequiredService<IEventBus>();

    public IManifestSource ManifestSource => Agent.Services.GetRequiredService<IManifestSource>();

    public IConfigStore Config => Agent.Services.GetRequiredService<IConfigStore>();

    /// <summary>Pairs a new client through the pairing service, as the pairing endpoint does.</summary>
    /// <returns>The link token and the client record.</returns>
    public async Task<(string Token, LinkClient Client)> PairAsync()
    {
        int number = Interlocked.Increment(ref _clients);
        PairingCode code = Pairing.StartPairing();
        PairResult paired = await Pairing.PairAsync(
            code.Code,
            new PairClientInfo("homeassistant", "Client " + number.ToString(CultureInfo.InvariantCulture)),
            CancellationToken.None);
        Assert.True(paired.Succeeded, "Pairing failed: " + paired.Attempt);
        return (paired.Token!, paired.Client!);
    }

    /// <summary>Pairs a new client, connects it and answers <c>hello</c>.</summary>
    public async Task<LinkTestClient> ConnectAsync()
    {
        (string token, _) = await PairAsync();
        return await LinkTestClient.ConnectAndGreetAsync(Agent, token);
    }

    /// <summary>Saves an enabled, exposed process command with one integer variable <c>Level</c> between 1 and 5.</summary>
    /// <returns>The revision of the manifest after the command exists.</returns>
    public async Task<string> AddCommandAsync()
    {
        var command = new CommandDocument(
            SchemaVersion: 1,
            Id: CommandId,
            Name: "Set level",
            Enabled: true,
            ExposeToHa: true,
            RunAs: RunAsValues.System,
            Action: new ProcessAction("level-tool", ["--level", "{{Level}}"]),
            Parameters: [new ParameterDefinition("Level", ParameterTypes.WholeNumber, Required: true, Min: 1, Max: 5)],
            CaptureOutput: true);
        SaveResult<CommandDocument> saved = await Config.Local.SaveAsync(command, null, CancellationToken.None);
        Assert.True(saved.IsApplied, "The command was not saved: " + saved.Status + " " + string.Join(", ", saved.Errors.Select(error => error.Code)));
        return ManifestSource.Current.Rev;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Agent.DisposeAsync();
        _data.Dispose();
    }
}
