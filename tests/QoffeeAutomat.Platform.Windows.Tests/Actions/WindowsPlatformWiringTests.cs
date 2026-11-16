using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Windows.Actions;
using QoffeeAutomat.Platform.Windows.Security;
using QoffeeAutomat.Platform.Windows.Service;
using QoffeeAutomat.Platform.Windows.Sessions;
using QoffeeAutomat.Platform.Windows.Tests.Sessions;

namespace QoffeeAutomat.Platform.Windows.Tests.Actions;

/// <summary>
/// Builds the container of <see cref="WindowsPlatformServices.AddWindowsPlatform"/> with fakes in place of every Windows call
/// that would change the computer: power, tokens and processes, permissions, sessions and pipes.
/// </summary>
internal static class WindowsPlatformTestHost
{
    public static DataDirectories Directories(string root)
    {
        return new DataDirectories(
            root,
            Path.Combine(root, "config"),
            Path.Combine(root, "scripts"),
            Path.Combine(root, "secrets"),
            Path.Combine(root, "data"),
            Path.Combine(root, "logs"));
    }

    public static ServiceProvider Build(string root, FakePowerNativeApi power, FakeElevatedNativeApi elevated, FakeAcl acl)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IPowerNativeApi>(power);
        services.AddSingleton<IElevatedNativeApi>(elevated);
        services.AddSingleton<IFileSystemAcl>(acl);
        services.AddSingleton<ISessionUserLookup>(new FakeSessionLookup());
        services.AddSingleton<ISessionNativeApi>(new FakeSessionNativeApi());
        services.AddSingleton<IPipePeerInspector>(new FakePeerInspector());
        services.AddWindowsPlatform(_ => Directories(root), "agent.id");
        return services.BuildServiceProvider();
    }
}

/// <summary>
/// The built-in actions and their default commands are wired into the Windows platform (PLAN.md S11, T306): the container
/// gives the agent the real provider instead of a placeholder, and the default commands are created by a hosted service.
/// </summary>
[Trait("Category", "Actions")]
public sealed class WindowsPlatformWiringTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakePowerNativeApi _power = new();
    private readonly FakeElevatedNativeApi _elevated = new();
    private readonly FakeAcl _acl = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_agent_gets_the_windows_actions_and_not_a_placeholder_that_offers_nothing()
    {
        using ServiceProvider provider = WindowsPlatformTestHost.Build(_temp.Path, _power, _elevated, _acl);

        IBuiltinActionProvider actions = provider.GetRequiredService<IBuiltinActionProvider>();

        Assert.IsType<UserContextBuiltinActions>(actions);
        Assert.Equal(WindowsActionCatalog.All.Count, actions.Actions.Count);
        Assert.Contains(actions.Actions, action => action.Name == WindowsActionNames.Shutdown);
        Assert.Contains(actions.Actions, action => action.Name == WindowsActionNames.ProcessKill);
    }

    [Fact]
    public async Task A_power_action_reaches_the_native_power_api_of_the_container()
    {
        using ServiceProvider provider = WindowsPlatformTestHost.Build(_temp.Path, _power, _elevated, _acl);
        IBuiltinActionProvider actions = provider.GetRequiredService<IBuiltinActionProvider>();

        ExecutionResult result = await actions.ExecuteAsync(
            WindowsActionNames.Restart,
            ActionTestData.Vars((WindowsActionParameters.Force, true)),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        ActionTestData.AssertCalls(_power.Calls, "restart:force");
    }

    [Fact]
    public async Task An_action_of_the_desktop_is_left_to_the_session_host_and_never_reaches_the_power_api()
    {
        using ServiceProvider provider = WindowsPlatformTestHost.Build(_temp.Path, _power, _elevated, _acl);
        IBuiltinActionProvider actions = provider.GetRequiredService<IBuiltinActionProvider>();

        ExecutionResult result = await actions.ExecuteAsync(WindowsActionNames.LockSession, ActionTestData.Vars(), CancellationToken.None);

        Assert.Equal(ErrorCodes.NoSession, result.ErrorCode); // no SessionHost is connected in this container
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task The_default_commands_are_created_by_a_hosted_service_in_the_data_directory_of_the_agent()
    {
        DataDirectories directories = WindowsPlatformTestHost.Directories(_temp.Path);
        using var store = new ConfigStore(new ConfigStoreOptions
        {
            ConfigDirectory = directories.Config,
            ScriptsDirectory = directories.Scripts,
            EnableWatcher = false,
        });
        await store.LoadAsync(CancellationToken.None);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<IConfigStore>(store);
        services.AddWindowsDefaultCommands(_ => directories);
        using ServiceProvider provider = services.BuildServiceProvider();

        IHostedService seeding = Assert.Single(provider.GetServices<IHostedService>());
        Assert.IsType<DefaultCommandSeedService>(seeding);
        await seeding.StartAsync(CancellationToken.None);

        Assert.Equal(DefaultCommandSeeder.DefaultCommands.Count, store.Effective.List<CommandDocument>().Count);
        Assert.True(File.Exists(Path.Combine(directories.Data, DefaultCommandSeeder.MarkerFileName)));
    }

    [Fact]
    public async Task The_seed_service_of_the_container_does_not_create_the_commands_again_after_the_user_deleted_one()
    {
        DataDirectories directories = WindowsPlatformTestHost.Directories(_temp.Path);
        for (int start = 1; start <= 2; start++)
        {
            using var store = new ConfigStore(new ConfigStoreOptions
            {
                ConfigDirectory = directories.Config,
                ScriptsDirectory = directories.Scripts,
                EnableWatcher = false,
            });
            await store.LoadAsync(CancellationToken.None);
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton<IConfigStore>(store);
            services.AddWindowsDefaultCommands(_ => directories);
            using ServiceProvider provider = services.BuildServiceProvider();

            await provider.GetServices<IHostedService>().Single().StartAsync(CancellationToken.None);

            if (start == 1)
            {
                Assert.True((await store.Local.DeleteAsync<CommandDocument>("power_restart", expectedRevision: null, CancellationToken.None)).IsApplied);
            }
            else
            {
                Assert.Equal(DefaultCommandSeeder.DefaultCommands.Count - 1, store.Effective.List<CommandDocument>().Count);
                Assert.DoesNotContain(store.Effective.List<CommandDocument>(), command => command.Id == "power_restart");
            }
        }
    }
}
