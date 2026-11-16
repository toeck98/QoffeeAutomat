using System.Text.Json;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Validation;
using QoffeeAutomat.Platform.Windows.Actions;

namespace QoffeeAutomat.Platform.Windows.Tests.Actions;

/// <summary>
/// The default commands of the power and session actions (PLAN.md S11, T306): created once on the first start with an empty
/// configuration, disabled and not exposed, and never created again, not even after the user deleted them.
/// </summary>
[Trait("Category", "Actions")]
public sealed class DefaultCommandSeederTests : IDisposable
{
    private static readonly string[] DefaultIds =
    [
        "power_shutdown",
        "power_restart",
        "power_sleep",
        "power_hibernate",
        "session_lock",
        "session_logoff",
    ];

    private readonly TempDirectory _temp = new();
    private readonly ManualTime _time = new();

    private string MarkerPath => Path.Combine(_temp.Path, "data", DefaultCommandSeeder.MarkerFileName);

    public void Dispose() => _temp.Dispose();

    private async Task<ConfigStore> OpenStoreAsync()
    {
        var store = new ConfigStore(
            new ConfigStoreOptions
            {
                ConfigDirectory = Path.Combine(_temp.Path, "config"),
                ScriptsDirectory = Path.Combine(_temp.Path, "scripts"),
                EnableWatcher = false,
            },
            _time);
        await store.LoadAsync(CancellationToken.None);
        return store;
    }

    private DefaultCommandSeeder NewSeeder(IConfigStore store) => new(store, MarkerPath, _time);

    private static CommandDocument UserCommand(string id = "my_command") => new(
        DocumentSchema.CurrentVersion,
        id,
        "My command",
        Enabled: true,
        ExposeToHa: false,
        RunAsValues.System,
        new BuiltinAction(WindowsActionNames.Sleep));

    private static IReadOnlyList<string> Ids(ConfigStore store) => [.. store.Effective.List<CommandDocument>().Select(command => command.Id).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task The_first_start_with_an_empty_configuration_creates_the_six_default_commands()
    {
        using ConfigStore store = await OpenStoreAsync();

        SeedOutcome outcome = await NewSeeder(store).SeedAsync(CancellationToken.None);

        Assert.Equal(SeedStatus.Seeded, outcome.Status);
        Assert.Null(outcome.Problem);
        Assert.Equal(DefaultIds, outcome.Created);
        Assert.Equal(DefaultIds.Order(StringComparer.Ordinal), Ids(store));
        Assert.True(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task Every_default_command_is_disabled_and_not_exposed_so_the_user_only_has_to_switch_it_on()
    {
        using ConfigStore store = await OpenStoreAsync();
        await NewSeeder(store).SeedAsync(CancellationToken.None);

        foreach (CommandDocument command in store.Effective.List<CommandDocument>())
        {
            Assert.False(command.Enabled, command.Id + " must be disabled.");
            Assert.False(command.ExposeToHa, command.Id + " must not be exposed to Home Assistant.");
        }
    }

    [Fact]
    public async Task Every_default_command_runs_a_real_builtin_action_in_the_context_that_the_action_needs()
    {
        using ConfigStore store = await OpenStoreAsync();
        await NewSeeder(store).SeedAsync(CancellationToken.None);

        foreach (CommandDocument command in store.Effective.List<CommandDocument>())
        {
            BuiltinAction action = Assert.IsType<BuiltinAction>(command.Action);
            BuiltinActionDescriptor? descriptor = WindowsActionCatalog.Find(action.Name);

            Assert.NotNull(descriptor);
            Assert.Equal(descriptor.RunAs, command.RunAs);
        }
    }

    [Fact]
    public async Task The_power_actions_are_defaults_for_the_system_and_the_session_actions_for_the_user()
    {
        using ConfigStore store = await OpenStoreAsync();
        await NewSeeder(store).SeedAsync(CancellationToken.None);
        IReadOnlyList<CommandDocument> commands = store.Effective.List<CommandDocument>();

        Assert.All(commands.Where(command => command.Id.StartsWith("power_", StringComparison.Ordinal)), command => Assert.Equal(RunAsValues.System, command.RunAs));
        Assert.All(commands.Where(command => command.Id.StartsWith("session_", StringComparison.Ordinal)), command => Assert.Equal(RunAsValues.User, command.RunAs));
    }

    [Fact]
    public async Task The_commands_that_can_force_carry_the_force_parameter_and_the_others_have_none()
    {
        using ConfigStore store = await OpenStoreAsync();
        await NewSeeder(store).SeedAsync(CancellationToken.None);
        IReadOnlyList<CommandDocument> commands = store.Effective.List<CommandDocument>();

        foreach (string id in new[] { "power_shutdown", "power_restart", "session_logoff" })
        {
            CommandDocument command = commands.Single(candidate => candidate.Id == id);
            Assert.Equal(WindowsActionParameters.Force, Assert.Single(command.Parameters!).Name);
        }

        foreach (string id in new[] { "power_sleep", "power_hibernate", "session_lock" })
        {
            Assert.Null(commands.Single(candidate => candidate.Id == id).Parameters);
        }
    }

    [Fact]
    public async Task A_default_command_runs_without_variables_because_every_parameter_is_optional()
    {
        using ConfigStore store = await OpenStoreAsync();
        await NewSeeder(store).SeedAsync(CancellationToken.None);

        foreach (CommandDocument command in store.Effective.List<CommandDocument>())
        {
            BindResult result = VariableBinder.Bind(command.Parameters, null);

            Assert.True(result.IsValid, command.Id + ": " + string.Join("; ", result.Errors.Select(error => error.Message)));
        }
    }

    [Fact]
    public async Task The_marker_file_records_the_version_the_time_and_the_ids()
    {
        using ConfigStore store = await OpenStoreAsync();
        await NewSeeder(store).SeedAsync(CancellationToken.None);

        using JsonDocument marker = JsonDocument.Parse(await File.ReadAllBytesAsync(MarkerPath));
        JsonElement root = marker.RootElement;

        Assert.Equal(DefaultCommandSeeder.MarkerVersion, root.GetProperty("version").GetInt32());
        Assert.StartsWith("2026-11-16T12:00:00", root.GetProperty("seededAt").GetString(), StringComparison.Ordinal);
        Assert.Equal(DefaultIds, root.GetProperty("commands").EnumerateArray().Select(entry => entry.GetString()!));
    }

    [Fact]
    public void The_marker_file_is_not_named_like_a_configuration_file()
    {
        // D-034: no config.yaml, config.yml or config.json outside hidden folders.
        Assert.DoesNotContain("config", DefaultCommandSeeder.MarkerFileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_second_start_creates_nothing()
    {
        using ConfigStore store = await OpenStoreAsync();
        await NewSeeder(store).SeedAsync(CancellationToken.None);

        SeedOutcome second = await NewSeeder(store).SeedAsync(CancellationToken.None);

        Assert.Equal(SeedStatus.AlreadySeeded, second.Status);
        Assert.Empty(second.Created);
        Assert.Equal(DefaultIds.Length, store.Effective.List<CommandDocument>().Count);
    }

    [Fact]
    public async Task A_default_command_that_the_user_deleted_is_not_created_again_after_a_restart()
    {
        using (ConfigStore first = await OpenStoreAsync())
        {
            await NewSeeder(first).SeedAsync(CancellationToken.None);
            DeleteResult deleted = await first.Local.DeleteAsync<CommandDocument>("power_shutdown", expectedRevision: null, CancellationToken.None);
            Assert.True(deleted.IsApplied);
        }

        using ConfigStore restarted = await OpenStoreAsync();
        SeedOutcome outcome = await NewSeeder(restarted).SeedAsync(CancellationToken.None);

        Assert.Equal(SeedStatus.AlreadySeeded, outcome.Status);
        Assert.Empty(outcome.Created);
        Assert.DoesNotContain("power_shutdown", Ids(restarted));
        Assert.Equal(DefaultIds.Length - 1, Ids(restarted).Count);
    }

    [Fact]
    public async Task Even_when_the_user_deleted_every_default_command_the_configuration_stays_empty()
    {
        using (ConfigStore first = await OpenStoreAsync())
        {
            await NewSeeder(first).SeedAsync(CancellationToken.None);
            foreach (string id in DefaultIds)
            {
                Assert.True((await first.Local.DeleteAsync<CommandDocument>(id, expectedRevision: null, CancellationToken.None)).IsApplied);
            }
        }

        using ConfigStore restarted = await OpenStoreAsync();
        SeedOutcome outcome = await NewSeeder(restarted).SeedAsync(CancellationToken.None);

        Assert.Equal(SeedStatus.AlreadySeeded, outcome.Status);
        Assert.Empty(Ids(restarted));
    }

    [Fact]
    public async Task A_configuration_that_already_holds_a_command_of_the_user_gets_no_defaults()
    {
        using ConfigStore store = await OpenStoreAsync();
        Assert.True((await store.Local.SaveAsync(UserCommand(), expectedRevision: 0, CancellationToken.None)).IsApplied);

        SeedOutcome outcome = await NewSeeder(store).SeedAsync(CancellationToken.None);

        Assert.Equal(SeedStatus.SkippedNotEmpty, outcome.Status);
        Assert.Empty(outcome.Created);
        string[] expectedIds = ["my_command"];
        Assert.Equal(expectedIds, Ids(store));
        Assert.True(File.Exists(MarkerPath), "The marker keeps the defaults from appearing later.");
    }

    [Fact]
    public async Task The_defaults_do_not_appear_later_when_the_only_command_of_the_user_is_deleted()
    {
        using (ConfigStore first = await OpenStoreAsync())
        {
            await first.Local.SaveAsync(UserCommand(), expectedRevision: 0, CancellationToken.None);
            await NewSeeder(first).SeedAsync(CancellationToken.None);
            await first.Local.DeleteAsync<CommandDocument>("my_command", expectedRevision: null, CancellationToken.None);
        }

        using ConfigStore restarted = await OpenStoreAsync();
        SeedOutcome outcome = await NewSeeder(restarted).SeedAsync(CancellationToken.None);

        Assert.Equal(SeedStatus.AlreadySeeded, outcome.Status);
        Assert.Empty(Ids(restarted));
    }

    [Fact]
    public async Task A_first_start_that_was_interrupted_is_finished_without_duplicates()
    {
        using ConfigStore store = await OpenStoreAsync();
        Assert.True((await store.Local.SaveAsync(DefaultCommandSeeder.DefaultCommands[0], expectedRevision: 0, CancellationToken.None)).IsApplied);
        Assert.True((await store.Local.SaveAsync(DefaultCommandSeeder.DefaultCommands[4], expectedRevision: 0, CancellationToken.None)).IsApplied);

        SeedOutcome outcome = await NewSeeder(store).SeedAsync(CancellationToken.None);

        Assert.Equal(SeedStatus.Seeded, outcome.Status);
        string[] expectedCreated = ["power_restart", "power_sleep", "power_hibernate", "session_logoff"];
        Assert.Equal(expectedCreated, outcome.Created);
        Assert.Equal(DefaultIds.Order(StringComparer.Ordinal), Ids(store));
        Assert.True(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task The_seed_service_logs_the_creation_once_and_never_throws()
    {
        using ConfigStore store = await OpenStoreAsync();
        var logger = new ListLogger();
        var service = new DefaultCommandSeedService(NewSeeder(store), logger);

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, level);
        Assert.Contains("6", message, StringComparison.Ordinal);
    }
}
