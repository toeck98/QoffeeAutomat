using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Metrics;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Agent.Benchmarks;

/// <summary>
/// The real agent with the simulated platform in a temporary data directory, listening on free ports, with enough
/// metrics for the benchmark and a link token for the client.
/// </summary>
internal sealed class AgentUnderTest : IAsyncDisposable
{
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(30);

    private readonly WebApplication _app;
    private readonly string _directory;

    private AgentUnderTest(WebApplication app, string directory, int linkPort, string[] metrics)
    {
        _app = app;
        _directory = directory;
        LinkPort = linkPort;
        Metrics = metrics;
    }

    /// <summary>The port of the QoffeeLink listener (HTTPS).</summary>
    public int LinkPort { get; }

    /// <summary>The ids of the metrics to subscribe to.</summary>
    public string[] Metrics { get; }

    /// <summary>Starts the agent and makes sure that the catalog has at least <paramref name="metricCount"/> metrics.</summary>
    public static async Task<AgentUnderTest> StartAsync(int metricCount, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "qoffeeautomat-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int uiPort = FreePort();
        int linkPort = FreePort();
        var options = new AgentOptions(DataDir: directory, Platform: "simulated", UiPort: uiPort, LinkPort: linkPort);

        WebApplication app = AgentHost.Build(options, services =>
        {
            services.AddSingleton<IMetricProvider>(new BenchmarkMetricProvider(metricCount));
            services.Configure<LoggerFilterOptions>(filter => filter.MinLevel = LogLevel.Warning);
        });
        try
        {
            await app.StartAsync(cancellationToken);
            string[] metrics = await PrepareMetricsAsync(app, metricCount, cancellationToken);
            return new AgentUnderTest(app, directory, linkPort, metrics);
        }
        catch
        {
            await app.DisposeAsync();
            DeleteDirectory(directory);
            throw;
        }
    }

    /// <summary>Pairs a client the way the pairing endpoint does and returns its link token and the certificate fingerprint to pin.</summary>
    public async Task<(string Token, string Fingerprint)> PairAsync(CancellationToken cancellationToken)
    {
        var pairing = _app.Services.GetRequiredService<PairingService>();
        PairingCode code = pairing.StartPairing();
        PairResult paired = await pairing.PairAsync(code.Code, new PairClientInfo("tool", "Benchmark"), cancellationToken);
        if (!paired.Succeeded)
        {
            throw new InvalidOperationException("Pairing failed: " + paired.Attempt);
        }

        string fingerprint = pairing.Fingerprint ?? throw new InvalidOperationException("The agent has no TLS certificate.");
        return (paired.Token!, fingerprint);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync();
        }
        finally
        {
            await _app.DisposeAsync();
            DeleteDirectory(_directory);
        }
    }

    /// <summary>The simulated platform has a handful of metrics; custom sensors bring the catalog to the wanted number.</summary>
    private static Task<string[]> PrepareMetricsAsync(WebApplication app, int metricCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = app.Services.GetRequiredService<MetricEngine>();
        if (engine.Catalog.Count < metricCount)
        {
            throw new InvalidOperationException($"The metric catalog has {engine.Catalog.Count} metrics, {metricCount} are needed.");
        }

        return Task.FromResult<string[]>([.. engine.Catalog.Select(entry => entry.Id).Order(StringComparer.Ordinal).Take(metricCount)]);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a log file may still be open.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

internal sealed class BenchmarkMetricProvider : IMetricProvider
{
    private readonly IReadOnlyList<MetricDescriptor> _descriptors;
    private long _counter;

    public BenchmarkMetricProvider(int count)
    {
        _descriptors = [.. Enumerable.Range(0, count).Select(index => new MetricDescriptor(
            $"bench.metric_{index:D3}",
            $"Benchmark metric {index}",
            "sensor",
            "number",
            StateClass: "measurement",
            Precision: 0,
            EnabledDefault: true,
            IntervalSeconds: 5))];
    }

    public IReadOnlyList<MetricDescriptor> Describe() => _descriptors;

    public ValueTask<IReadOnlyDictionary<string, object?>> SampleAsync(IReadOnlyCollection<string> metricIds, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>(metricIds.Count, StringComparer.Ordinal);
        foreach (string id in metricIds)
        {
            values[id] = string.Equals(id, "bench.metric_000", StringComparison.Ordinal)
                ? (double)Interlocked.Increment(ref _counter)
                : 42.0;
        }

        return ValueTask.FromResult<IReadOnlyDictionary<string, object?>>(values);
    }
}
