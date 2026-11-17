using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using QoffeeAutomat.Agent.Updates;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Updates;

namespace QoffeeAutomat.Agent.Tests.Updates;

/// <summary>
/// The self-update, <c>agent.update</c> (PLAN.md S09 item 13, T704, D-046): checksum, signature, <c>bye</c>, silent installer. Nothing
/// here touches the network, starts a process or checks a real signature: the release source, the verifier, the launcher and the
/// <c>bye</c> are fakes that record what the updater does.
/// </summary>
[Trait("Category", "Updates")]
public sealed class UpdaterTests
{
    private const string SignedExecutable = "C:\\Program Files\\QoffeeAutomat\\QoffeeAutomat.Agent.exe";

    [Fact]
    public async Task An_installer_with_the_right_checksum_is_started_silently_after_bye_update()
    {
        using var harness = new UpdaterHarness();

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Contains(UpdateSamples.LatestVersion, result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal([$"bye:{ByeReasons.Update}", "launch"], harness.Events.Log);
        Assert.Equal([ByeReasons.Update], harness.Bye.Reasons);

        (string runAs, ProcessLaunchRequest request) = Assert.Single(harness.Launcher.Launches);
        Assert.Equal(RunAsValues.System, runAs);
        Assert.Equal(Path.Combine(Assert.Single(Directory.GetDirectories(harness.UpdatesDirectory)), UpdateSamples.InstallerName), request.FileName);
        Assert.False(request.WaitForExit);
        Assert.False(request.CaptureOutput);

        // The installer that runs is the file that was checked, with the content that was downloaded.
        Assert.Equal(UpdateSamples.InstallerBytes, await File.ReadAllBytesAsync(request.FileName));
        Assert.Equal(1, harness.Releases.FileCalls);
    }

    [Fact]
    public async Task The_installer_gets_fixed_arguments_as_separate_entries_and_nothing_from_outside()
    {
        using var harness = new UpdaterHarness();

        await harness.Updater.UpdateAsync(CancellationToken.None);

        ProcessLaunchRequest request = Assert.Single(harness.Launcher.Launches).Request;
        Assert.Equal(
            ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + Path.Combine(harness.Paths.LogsDirectory, "update-installer.log")],
            request.Arguments);
        Assert.Empty(request.EnvironmentVariables);
        Assert.Equal(Path.GetDirectoryName(request.FileName), request.WorkingDirectory);
    }

    [Fact]
    public async Task An_installer_with_a_wrong_checksum_is_refused_deleted_and_never_started()
    {
        using var harness = new UpdaterHarness();
        harness.Releases.Installer = Encoding.UTF8.GetBytes("a different file than the release lists");

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Contains("checksum", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("refused", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.Bye.Reasons);
        Assert.Empty(harness.Events.Log);
        Assert.Empty(Directory.GetFiles(harness.UpdatesDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_checksum_that_differs_in_a_single_digit_is_refused()
    {
        using var harness = new UpdaterHarness();
        string right = UpdateSamples.Sha256(UpdateSamples.InstallerBytes);
        string wrong = (right[0] == '0' ? "1" : "0") + right[1..];
        harness.Releases.Sums = wrong + "  " + UpdateSamples.InstallerName + "\n";

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.Bye.Reasons);
    }

    [Fact]
    public async Task A_checksum_in_upper_case_is_accepted()
    {
        using var harness = new UpdaterHarness();
        harness.Releases.Sums = UpdateSamples.Sha256(UpdateSamples.InstallerBytes).ToUpperInvariant() + " *" + UpdateSamples.InstallerName + "\r\n";

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Single(harness.Launcher.Launches);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a checksum file\n")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  some-other-file.zip\n")]
    [InlineData("0123456789abcdef  QoffeeAutomat-Setup-1.2.0-x64.exe\n")]
    public async Task A_checksum_file_without_a_valid_line_for_the_installer_refuses_it_before_the_download(string sums)
    {
        using var harness = new UpdaterHarness();
        harness.Releases.Sums = sums;

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(UpdateOptions.ChecksumsAssetName, result.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, harness.Releases.FileCalls);
        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.Bye.Reasons);
    }

    [Fact]
    public async Task A_release_without_a_checksum_file_is_refused()
    {
        using var harness = new UpdaterHarness();
        harness.Releases.Release = UpdateSamples.Release(checksums: false);

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(UpdateOptions.ChecksumsAssetName, result.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, harness.Releases.FileCalls);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task A_release_without_an_installer_for_this_architecture_is_refused()
    {
        using var harness = new UpdaterHarness();
        harness.Releases.Release = UpdateSamples.Release(installer: false);

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("no installer", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("x64", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task A_signed_build_refuses_an_installer_whose_signature_is_not_trusted()
    {
        using var harness = new UpdaterHarness(executable: SignedExecutable);
        harness.Authenticode.BuildIsSigned = true;
        harness.Authenticode.Result = AuthenticodeResult.Untrusted("the certificate chain is not trusted");

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Authenticode", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("the certificate chain is not trusted", result.StandardError, StringComparison.Ordinal);
        Assert.EndsWith(UpdateSamples.InstallerName, Assert.Single(harness.Authenticode.Verified), StringComparison.Ordinal);
        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.Bye.Reasons);
        Assert.Empty(Directory.GetFiles(harness.UpdatesDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_signed_build_installs_an_installer_with_a_trusted_signature()
    {
        using var harness = new UpdaterHarness(executable: SignedExecutable);
        harness.Authenticode.BuildIsSigned = true;

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Single(harness.Authenticode.Verified);
        Assert.Equal(["bye:update", "launch"], harness.Events.Log);
    }

    [Fact]
    public async Task An_unsigned_build_does_not_ask_for_a_signature()
    {
        using var harness = new UpdaterHarness(executable: SignedExecutable);
        harness.Authenticode.BuildIsSigned = false;
        harness.Authenticode.Result = AuthenticodeResult.Untrusted("would be refused if asked");

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Empty(harness.Authenticode.Verified);
    }

    [Fact]
    public async Task A_build_without_a_known_executable_path_counts_as_unsigned()
    {
        using var harness = new UpdaterHarness(executable: null);
        harness.Authenticode.BuildIsSigned = true;
        harness.Authenticode.Result = AuthenticodeResult.Untrusted("would be refused if asked");

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Empty(harness.Authenticode.Verified);
    }

    [Fact]
    public async Task The_checksum_is_checked_before_the_signature()
    {
        using var harness = new UpdaterHarness(executable: SignedExecutable);
        harness.Authenticode.BuildIsSigned = true;
        harness.Releases.Installer = Encoding.UTF8.GetBytes("tampered");

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("checksum", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Authenticode.Verified);
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.1.0")]
    [InlineData("1.0.0")]
    public async Task An_agent_that_is_not_older_than_the_release_downloads_nothing(string releaseVersion)
    {
        using var harness = new UpdaterHarness();
        harness.Releases.Release = UpdateSamples.Release(releaseVersion);
        var current = new UpdaterHarness(source: harness.Releases);
        harness.Options.GetType();
        _ = current;

        // The installed version of the sample options is 1.1.0: only 1.2.0 is newer.
        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        if (releaseVersion == UpdateSamples.LatestVersion)
        {
            Assert.True(result.Succeeded, result.StandardError);
            Assert.Single(harness.Launcher.Launches);
        }
        else
        {
            Assert.True(result.Succeeded, result.StandardError);
            Assert.Contains("already", result.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(1, harness.Releases.TotalCalls);
            Assert.Empty(harness.Launcher.Launches);
            Assert.Empty(harness.Bye.Reasons);
        }
    }

    [Fact]
    public async Task An_agent_that_cannot_update_itself_refuses_without_a_request()
    {
        using var harness = new UpdaterHarness(supported: false);

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Releases.TotalCalls);
        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.Bye.Reasons);
    }

    [Fact]
    public async Task A_project_without_a_release_is_a_refusal_not_an_exception()
    {
        using var harness = new UpdaterHarness();
        harness.Releases.Release = null;

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(1, harness.Releases.TotalCalls);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task A_network_failure_is_a_refusal_and_starts_nothing()
    {
        using var harness = new UpdaterHarness();
        harness.Releases.Failure = new HttpRequestException("no route to host");

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("no route to host", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.Bye.Reasons);
    }

    [Fact]
    public async Task An_installer_that_cannot_be_started_is_reported_after_bye_with_the_reason()
    {
        using var harness = new UpdaterHarness();
        harness.Launcher.Result = ExecutionResult.Failure(ErrorCodes.Internal, "access denied");

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("access denied", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(["bye:update", "launch"], harness.Events.Log);
    }

    [Fact]
    public async Task Directories_of_earlier_attempts_are_removed_and_each_attempt_gets_its_own()
    {
        using var harness = new UpdaterHarness();
        string old = Path.Combine(harness.UpdatesDirectory, "earlier");
        Directory.CreateDirectory(old);
        await File.WriteAllTextAsync(Path.Combine(old, "left-over.exe"), "old");

        await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(Directory.Exists(old));
        Assert.Single(Directory.GetDirectories(harness.UpdatesDirectory));
    }

    [Fact]
    public async Task A_second_update_while_one_is_running_is_refused()
    {
        var gated = new GatedReleaseSource();
        using var harness = new UpdaterHarness(source: gated);

        Task<ExecutionResult> first = harness.Updater.UpdateAsync(CancellationToken.None);
        await TestWait.WithTimeoutAsync(gated.Entered, "the first update to ask for the release");

        ExecutionResult second = await harness.Updater.UpdateAsync(CancellationToken.None);
        gated.Open();
        ExecutionResult firstResult = await TestWait.WithTimeoutAsync(first, "the first update to end");

        Assert.False(second.Succeeded);
        Assert.Contains("already running", second.StandardError, StringComparison.Ordinal);
        Assert.True(firstResult.Succeeded, firstResult.StandardError);
        Assert.Single(harness.Launcher.Launches);
    }

    [Fact]
    public async Task The_updater_shares_the_release_it_found_with_the_checker()
    {
        var releases = new FakeReleaseSource();
        var options = UpdateSamples.Options();
        var checker = UpdateSamples.Checker(releases, new FakeUpdateSettings { CheckEnabled = false }, options);
        using var data = new TempDataDirectory();
        var updater = new AgentUpdater(
            releases,
            new FakeAuthenticode(),
            new RecordingInstallerLauncher(new UpdateEvents()),
            new RecordingBye(new UpdateEvents()),
            UpdateSamples.Paths(data.Path),
            options,
            NullLogger<AgentUpdater>.Instance,
            checker);

        await updater.UpdateAsync(CancellationToken.None);

        Assert.Equal(UpdateSamples.LatestVersion, checker.Current.LatestVersion);
    }

    // -- the real release client over a fake HTTP handler ----------------------------------------------------------------

    [Fact]
    public async Task The_whole_update_works_over_http_with_only_github_addresses()
    {
        FakeHttpHandler handler = GitHubHandler(UpdateSamples.InstallerBytes);
        using var http = new HttpClient(handler);
        var source = new GitHubReleaseClient(http, UpdateSamples.Options());
        using var harness = new UpdaterHarness(source: source);

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Equal(
            [
                "https://api.github.com/repos/toeck98/QoffeeAutomat/releases/latest",
                UpdateSamples.DownloadHost + UpdateOptions.ChecksumsAssetName,
                UpdateSamples.DownloadHost + UpdateSamples.InstallerName,
            ],
            handler.Requests.Select(url => url.AbsoluteUri));
        Assert.Equal(["bye:update", "launch"], harness.Events.Log);
        string installer = Assert.Single(harness.Launcher.Launches).Request.FileName;
        Assert.Equal(UpdateSamples.InstallerBytes, await File.ReadAllBytesAsync(installer));
    }

    [Fact]
    public async Task A_download_with_a_wrong_checksum_over_http_is_refused()
    {
        FakeHttpHandler handler = GitHubHandler(Encoding.UTF8.GetBytes("this is not the installer that SHA256SUMS.txt lists"));
        using var http = new HttpClient(handler);
        var source = new GitHubReleaseClient(http, UpdateSamples.Options());
        using var harness = new UpdaterHarness(source: source);

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("checksum", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Launcher.Launches);
        Assert.Empty(harness.Bye.Reasons);
        Assert.Empty(Directory.GetFiles(harness.UpdatesDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_installer_address_outside_github_is_never_requested()
    {
        string json = UpdateSamples.ReleaseJson().Replace(
            UpdateSamples.DownloadHost + UpdateSamples.InstallerName,
            "https://downloads.example.org/" + UpdateSamples.InstallerName,
            StringComparison.Ordinal);
        var handler = new FakeHttpHandler(request => request.RequestUri!.Host == "api.github.com"
            ? FakeHttpHandler.Json(json)
            : Text(UpdateSamples.Sums(UpdateSamples.InstallerName, UpdateSamples.InstallerBytes)));
        using var http = new HttpClient(handler);
        var source = new GitHubReleaseClient(http, UpdateSamples.Options());
        using var harness = new UpdaterHarness(source: source);

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(handler.Requests, url => url.Host == "downloads.example.org");
        Assert.Empty(harness.Launcher.Launches);
    }

    [Fact]
    public async Task A_download_that_is_redirected_away_from_github_is_refused()
    {
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                return FakeHttpHandler.Json(UpdateSamples.ReleaseJson());
            }

            if (request.RequestUri.AbsoluteUri.EndsWith(UpdateOptions.ChecksumsAssetName, StringComparison.Ordinal))
            {
                return Text(UpdateSamples.Sums(UpdateSamples.InstallerName, UpdateSamples.InstallerBytes));
            }

            HttpResponseMessage response = FakeHttpHandler.Bytes(UpdateSamples.InstallerBytes);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example.org/installer.exe");
            return response;
        });
        using var http = new HttpClient(handler);
        var source = new GitHubReleaseClient(http, UpdateSamples.Options());
        using var harness = new UpdaterHarness(source: source);

        ExecutionResult result = await harness.Updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("redirected", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(harness.Launcher.Launches);
    }

    [Theory]
    [InlineData("https://github.com/x/y/releases/download/v1/a.exe", true)]
    [InlineData("https://objects.githubusercontent.com/a.exe", true)]
    [InlineData("https://GITHUB.com/x", true)]
    [InlineData("http://github.com/x/y/a.exe", false)]
    [InlineData("https://github.com.evil.example/a.exe", false)]
    [InlineData("https://evilgithub.com/a.exe", false)]
    [InlineData("https://example.org/github.com", false)]
    [InlineData("ftp://github.com/a.exe", false)]
    public void Only_https_addresses_of_github_are_trusted(string url, bool expected)
    {
        Assert.Equal(expected, GitHubReleaseClient.IsTrustedAddress(new Uri(url)));
    }

    private static FakeHttpHandler GitHubHandler(byte[] installer)
    {
        return new FakeHttpHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
            {
                return FakeHttpHandler.Json(UpdateSamples.ReleaseJson());
            }

            if (url.EndsWith(UpdateOptions.ChecksumsAssetName, StringComparison.Ordinal))
            {
                // The checksum file always describes the genuine installer.
                return Text(UpdateSamples.Sums(UpdateSamples.InstallerName, UpdateSamples.InstallerBytes));
            }

            return url.EndsWith(UpdateSamples.InstallerName, StringComparison.Ordinal)
                ? FakeHttpHandler.Bytes(installer)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static HttpResponseMessage Text(string text)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
    }
}

/// <summary>A release source that waits at the first call until the test lets it go, to hold an update in the middle.</summary>
internal sealed class GatedReleaseSource : IReleaseSource
{
    private readonly FakeReleaseSource _inner = new();
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void Open() => _gate.TrySetResult();

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _gate.Task.WaitAsync(cancellationToken);
        return await _inner.GetLatestAsync(cancellationToken);
    }

    public Task<string> DownloadTextAsync(ReleaseAsset asset, int maxBytes, CancellationToken cancellationToken)
    {
        return _inner.DownloadTextAsync(asset, maxBytes, cancellationToken);
    }

    public Task DownloadFileAsync(ReleaseAsset asset, string destination, long maxBytes, CancellationToken cancellationToken)
    {
        return _inner.DownloadFileAsync(asset, destination, maxBytes, cancellationToken);
    }
}
