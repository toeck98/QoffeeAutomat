using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Updates;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Updates;

namespace QoffeeAutomat.Agent.Tests.Updates;

/// <summary>The facts that the update tests share: a release with an installer and the checksum file that belongs to it.</summary>
internal static class UpdateSamples
{
    public const string InstalledVersion = "1.1.0";
    public const string LatestVersion = "1.2.0";
    public const string InstallerName = "QoffeeAutomat-Setup-1.2.0-x64.exe";
    public const string DownloadHost = "https://github.com/toeck98/QoffeeAutomat/releases/download/v1.2.0/";

    public static byte[] InstallerBytes { get; } = Encoding.UTF8.GetBytes("pretend this is the installer of QoffeeAutomat 1.2.0");

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sums(string fileName, byte[] bytes) => Sha256(bytes) + "  " + fileName + "\n";

    public static UpdateOptions Options(bool supported = true, string? executable = null, TimeSpan? initialDelay = null) => new()
    {
        Supported = supported,
        InstalledVersion = InstalledVersion,
        InstallerArchitecture = "x64",
        RunningExecutablePath = executable,
        InitialDelay = initialDelay ?? TimeSpan.Zero,
        SettingsPollInterval = TimeSpan.FromMilliseconds(40),
    };

    public static ReleaseInfo Release(string version = LatestVersion, bool installer = true, bool checksums = true)
    {
        string name = ReleaseParser.InstallerName(version, "x64");
        Assert.True(SemanticVersion.TryParse(version, out SemanticVersion? parsed));
        return new ReleaseInfo(
            parsed,
            "v" + version,
            "https://github.com/toeck98/QoffeeAutomat/releases/tag/v" + version,
            "Fixes and a new update entity.",
            installer ? new ReleaseAsset(name, new Uri(DownloadHost + name), 1000) : null,
            checksums ? new ReleaseAsset(UpdateOptions.ChecksumsAssetName, new Uri(DownloadHost + UpdateOptions.ChecksumsAssetName), 100) : null);
    }

    /// <summary>The JSON that the GitHub API answers for the release, in its own snake_case.</summary>
    public static string ReleaseJson(string tag = "v" + LatestVersion, bool draft = false, bool prerelease = false, bool withAssets = true)
    {
        string version = tag.TrimStart('v');
        string assets = withAssets
            ? $$"""
                [
                  {"name":"QoffeeAutomat-Setup-{{version}}-x64.exe","browser_download_url":"{{DownloadHost}}QoffeeAutomat-Setup-{{version}}-x64.exe","size":1000,"state":"uploaded"},
                  {"name":"QoffeeAutomat-Setup-{{version}}-arm64.exe","browser_download_url":"{{DownloadHost}}QoffeeAutomat-Setup-{{version}}-arm64.exe","size":1000},
                  {"name":"SHA256SUMS.txt","browser_download_url":"{{DownloadHost}}SHA256SUMS.txt","size":200},
                  {"name":"sbom.spdx.json","browser_download_url":"{{DownloadHost}}sbom.spdx.json","size":50}
                ]
                """
            : "[]";
        return $$"""
            {
              "url":"https://api.github.com/repos/toeck98/QoffeeAutomat/releases/1",
              "tag_name":"{{tag}}",
              "name":"QoffeeAutomat {{version}}",
              "draft":{{(draft ? "true" : "false")}},
              "prerelease":{{(prerelease ? "true" : "false")}},
              "published_at":"2026-11-17T09:00:00Z",
              "html_url":"https://github.com/toeck98/QoffeeAutomat/releases/tag/{{tag}}",
              "body":"Fixes and a new update entity.",
              "assets":{{assets}}
            }
            """;
    }

    public static IAppPaths Paths(string root) => AppPaths.ForRoot(root);

    public static UpdateChecker Checker(IReleaseSource releases, IUpdateSettingsSource settings, UpdateOptions options, TimeProvider? time = null)
    {
        return new UpdateChecker(releases, settings, options, time ?? TimeProvider.System, NullLogger<UpdateChecker>.Instance);
    }
}

/// <summary>A clock that moves when the test says so.</summary>
internal sealed class UpdateClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The choices of the user, set by the test.</summary>
internal sealed class FakeUpdateSettings : IUpdateSettingsSource
{
    public bool CheckEnabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>An HTTP handler that answers from a function, records every request and never touches the network.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    private readonly object _gate = new();
    private readonly List<Uri> _requests = [];

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public Uri[] Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>The headers of every request, as one text each, to check that nothing is sent that should not be.</summary>
    public List<string> Headers { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public static HttpResponseMessage Bytes(byte[] bytes)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request.RequestUri!);
            Headers.Add(request.Headers.ToString());
        }

        HttpResponseMessage response = _respond(request);
        response.RequestMessage ??= request;
        return Task.FromResult(response);
    }
}

/// <summary>A release source that answers from memory and counts its calls.</summary>
internal sealed class FakeReleaseSource : IReleaseSource
{
    public ReleaseInfo? Release { get; set; } = UpdateSamples.Release();

    public string Sums { get; set; } = UpdateSamples.Sums(UpdateSamples.InstallerName, UpdateSamples.InstallerBytes);

    public byte[] Installer { get; set; } = UpdateSamples.InstallerBytes;

    public Exception? Failure { get; set; }

    public int LatestCalls { get; private set; }

    public int TextCalls { get; private set; }

    public int FileCalls { get; private set; }

    public int TotalCalls => LatestCalls + TextCalls + FileCalls;

    public Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        LatestCalls++;
        return Failure is null ? Task.FromResult(Release) : Task.FromException<ReleaseInfo?>(Failure);
    }

    public Task<string> DownloadTextAsync(ReleaseAsset asset, int maxBytes, CancellationToken cancellationToken)
    {
        TextCalls++;
        return Task.FromResult(Sums);
    }

    public async Task DownloadFileAsync(ReleaseAsset asset, string destination, long maxBytes, CancellationToken cancellationToken)
    {
        FileCalls++;
        await File.WriteAllBytesAsync(destination, Installer, cancellationToken);
    }
}

/// <summary>An Authenticode verifier that says what the test wants and records which files it was asked about.</summary>
internal sealed class FakeAuthenticode : IAuthenticodeVerifier
{
    public bool BuildIsSigned { get; set; }

    public AuthenticodeResult Result { get; set; } = AuthenticodeResult.Trusted;

    public List<string> Verified { get; } = [];

    public bool IsSigned(string path) => BuildIsSigned;

    public AuthenticodeResult Verify(string path)
    {
        Verified.Add(path);
        return Result;
    }
}

/// <summary>Records the order of what the updater does: the bye and the launch.</summary>
internal sealed class UpdateEvents
{
    public List<string> Log { get; } = [];
}

/// <summary>Tells nobody anything and records that it was asked.</summary>
internal sealed class RecordingBye : IByeAnnouncer
{
    private readonly UpdateEvents _events;

    public RecordingBye(UpdateEvents events)
    {
        _events = events;
    }

    public List<string> Reasons { get; } = [];

    public Task SayByeAsync(string reason, CancellationToken cancellationToken)
    {
        Reasons.Add(reason);
        _events.Log.Add("bye:" + reason);
        return Task.CompletedTask;
    }
}

/// <summary>Starts nothing and records the request: no installer runs in a test.</summary>
internal sealed class RecordingInstallerLauncher : IProcessLauncher
{
    private readonly UpdateEvents _events;

    public RecordingInstallerLauncher(UpdateEvents events)
    {
        _events = events;
    }

    public ExecutionResult Result { get; set; } = new(0, string.Empty, string.Empty);

    public List<(string RunAs, ProcessLaunchRequest Request)> Launches { get; } = [];

    public ValueTask<ExecutionResult> LaunchAsync(string runAs, ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        Launches.Add((runAs, request));
        _events.Log.Add("launch");
        return ValueTask.FromResult(Result);
    }
}

/// <summary>The updater with every collaborator replaced, in a temporary data directory.</summary>
internal sealed class UpdaterHarness : IDisposable
{
    private readonly TempDataDirectory _data = new();

    /// <param name="supported">Whether the agent can update itself.</param>
    /// <param name="executable">The path of the running executable; null means the agent is not signed, because the fake verifier is not asked.</param>
    /// <param name="source">Another source of releases, for example the real client over a fake HTTP handler; null uses <see cref="Releases"/>.</param>
    public UpdaterHarness(bool supported = true, string? executable = null, IReleaseSource? source = null)
    {
        Options = UpdateSamples.Options(supported, executable);
        Bye = new RecordingBye(Events);
        Launcher = new RecordingInstallerLauncher(Events);
        Paths = UpdateSamples.Paths(_data.Path);
        Updater = new AgentUpdater(source ?? Releases, Authenticode, Launcher, Bye, Paths, Options, NullLogger<AgentUpdater>.Instance);
    }

    public UpdateOptions Options { get; }

    public FakeReleaseSource Releases { get; } = new();

    public FakeAuthenticode Authenticode { get; } = new();

    public UpdateEvents Events { get; } = new();

    public RecordingBye Bye { get; }

    public RecordingInstallerLauncher Launcher { get; }

    public IAppPaths Paths { get; }

    public AgentUpdater Updater { get; }

    public string UpdatesDirectory => Path.Combine(Paths.DataDirectory, "updates");

    public void Dispose() => _data.Dispose();
}
