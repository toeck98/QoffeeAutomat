using Microsoft.Extensions.DependencyInjection;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Sessions;
using QoffeeAutomat.Platform.Windows.Tests.Actions;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>
/// The elevated launcher is wired into the Windows platform (PLAN.md S11, T311): the container gives the agent the real launcher,
/// which asks the native layer for the token, and no longer the placeholder that always answered <c>elevation_unavailable</c>.
/// </summary>
[Trait("Category", "Elevated")]
public sealed class ElevatedWiringTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeElevatedNativeApi _native = new();

    public void Dispose() => _temp.Dispose();

    private ServiceProvider Build() => WindowsPlatformTestHost.Build(_temp.Path, new FakePowerNativeApi(), _native, new FakeAcl());

    private static ProcessLaunchRequest Request() => new(
        "C:\\Tools\\admin.exe",
        ["--x"],
        null,
        new Dictionary<string, string>(),
        TimeSpan.FromSeconds(30));

    [Fact]
    public void The_container_gives_the_agent_the_real_elevated_launcher()
    {
        using ServiceProvider provider = Build();

        Assert.IsType<ElevatedLauncher>(provider.GetRequiredService<IElevatedLauncher>());
    }

    [Fact]
    public async Task A_run_reaches_the_native_layer_and_starts_the_process_with_the_token_of_the_console_user()
    {
        _native.OnStart = (process, _) => process.Exit(0);
        using ServiceProvider provider = Build();

        ExecutionResult result = await provider.GetRequiredService<IElevatedLauncher>().StartAsync(Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(_native.Launches);
        Assert.Equal(FakeElevatedNativeApi.UserSid, Assert.Single(_native.StartedWith).UserSid);
    }

    [Fact]
    public async Task A_standard_user_is_told_why_by_the_native_layer_and_not_by_a_placeholder()
    {
        _native.TokenStatus = ElevatedTokenStatus.NoLinkedToken;
        using ServiceProvider provider = Build();

        ExecutionResult result = await provider.GetRequiredService<IElevatedLauncher>().StartAsync(Request(), CancellationToken.None);

        Assert.Equal(ErrorCodes.ElevationUnavailable, result.ErrorCode);
        Assert.DoesNotContain("not available in this build", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(_native.Launches);
    }

    [Fact]
    public async Task Nobody_logged_in_is_no_session_and_not_elevation_unavailable()
    {
        _native.ConsoleSession = null;
        using ServiceProvider provider = Build();

        ExecutionResult result = await provider.GetRequiredService<IElevatedLauncher>().StartAsync(Request(), CancellationToken.None);

        Assert.Equal(ErrorCodes.NoSession, result.ErrorCode);
    }
}
