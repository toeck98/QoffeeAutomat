using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace QoffeeAutomat.Ui.E2E;

/// <summary>
/// Starts the published agent as a separate process with the simulated platform, a fresh temporary data directory and
/// ephemeral ports, and stops it again. The agent directory comes from <c>QOFFEE_E2E_AGENT_DIR</c> (CI publishes to
/// <c>artifacts/agent</c>); without it the fixture looks for a publish or build output below the repository root.
/// </summary>
public sealed partial class AgentFixture : IAsyncLifetime
{
    /// <summary>The environment variable that names the directory with <c>QoffeeAutomat.Agent.dll</c>.</summary>
    public const string AgentDirVariable = "QOFFEE_E2E_AGENT_DIR";

    private const string AgentAssembly = "QoffeeAutomat.Agent.dll";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

    private readonly ConcurrentQueue<string> _output = new();
    private readonly TaskCompletionSource<Uri> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;

    /// <summary>The temporary data directory of the agent.</summary>
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "qoffee-e2e-" + Guid.NewGuid().ToString("N"));

    /// <summary>The base address of the loopback UI listener.</summary>
    public Uri BaseUri { get; private set; } = new("http://127.0.0.1/");

    /// <summary>The first-run setup token the agent wrote into its secrets directory.</summary>
    public string SetupToken { get; private set; } = string.Empty;

    /// <summary>The console output of the agent so far, for failure messages.</summary>
    public string Log => string.Join(Environment.NewLine, _output);

    /// <summary>The directory for Playwright traces of failed runs.</summary>
    public static string TraceDirectory => Path.Combine(FindRepositoryRoot() ?? AppContext.BaseDirectory, "playwright-traces");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        string agentDirectory = ResolveAgentDirectory();
        Directory.CreateDirectory(DataDirectory);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = agentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(Path.Combine(agentDirectory, AgentAssembly));
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--platform");
        startInfo.ArgumentList.Add("simulated");
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(DataDirectory);
        startInfo.ArgumentList.Add("--ui-port");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--link-port");
        startInfo.ArgumentList.Add("0");
        startInfo.Environment["DOTNET_NOLOGO"] = "true";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "true";

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("The agent process did not start.");
        _process.OutputDataReceived += (_, e) => OnLine(e.Data);
        _process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        Task finished = Task.WhenAny(_listening.Task, _process.WaitForExitAsync(), Task.Delay(StartupTimeout));
        await finished.ConfigureAwait(false);
        if (!_listening.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("The agent did not report its listener within " + StartupTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                + " seconds. Output:" + Environment.NewLine + Log);
        }

        BaseUri = await _listening.Task.ConfigureAwait(false);
        SetupToken = await ReadSetupTokenAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                // The process is already gone or cannot be stopped; the directory cleanup below still runs.
            }
            finally
            {
                process.Dispose();
            }
        }

        DeleteDataDirectory();
    }

    private static string? FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QoffeeAutomat.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static string ResolveAgentDirectory()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(AgentDirVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            string full = Path.GetFullPath(fromEnvironment);
            if (!File.Exists(Path.Combine(full, AgentAssembly)))
            {
                throw new DirectoryNotFoundException($"{AgentDirVariable}='{full}' does not contain {AgentAssembly}.");
            }

            return full;
        }

        string? root = FindRepositoryRoot();
        if (root is not null)
        {
            string[] candidates =
            [
                Path.Combine(root, "artifacts", "agent"),
                Path.Combine(root, "src", "QoffeeAutomat.Agent", "bin", "Release", "net10.0", "publish"),
                Path.Combine(root, "src", "QoffeeAutomat.Agent", "bin", "Release", "net10.0"),
                Path.Combine(root, "src", "QoffeeAutomat.Agent", "bin", "Debug", "net10.0"),
            ];
            foreach (string candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, AgentAssembly)))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException(
            $"No published agent found. Run 'dotnet publish src/QoffeeAutomat.Agent -c Release -f net10.0 -o artifacts/agent' or set {AgentDirVariable}.");
    }

    private void OnLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        _output.Enqueue(line);
        Match match = ListeningAddress().Match(line);
        if (match.Success)
        {
            _listening.TrySetResult(new Uri(match.Value + "/"));
        }
    }

    private async Task<string> ReadSetupTokenAsync()
    {
        string path = Path.Combine(DataDirectory, "secrets", "setup-token.txt");
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    string token = (await File.ReadAllTextAsync(path).ConfigureAwait(false)).Trim();
                    if (token.Length > 0)
                    {
                        return token;
                    }
                }
                catch (IOException)
                {
                    // The agent is still writing the file.
                }
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        throw new FileNotFoundException("The agent did not write a setup token. Output:" + Environment.NewLine + Log, path);
    }

    private void DeleteDataDirectory()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(DataDirectory))
                {
                    Directory.Delete(DataDirectory, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }

    // The agent logs "Listening on http://127.0.0.1:PORT, https://...": the loopback http address is the UI.
    [GeneratedRegex(@"http://127\.0\.0\.1:\d+")]
    private static partial Regex ListeningAddress();
}
