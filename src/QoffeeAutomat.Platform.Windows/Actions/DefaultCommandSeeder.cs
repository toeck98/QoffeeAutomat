using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Session;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.IO;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Windows.Actions;

/// <summary>What <see cref="DefaultCommandSeeder.SeedAsync"/> did.</summary>
public enum SeedStatus
{
    /// <summary>The marker file exists: the defaults were offered before, so nothing was created, even when the user deleted them.</summary>
    AlreadySeeded,

    /// <summary>The configuration was empty: the default commands were created and the marker was written.</summary>
    Seeded,

    /// <summary>The configuration already holds commands of the user: nothing was created and the marker was written, so the defaults never appear later.</summary>
    SkippedNotEmpty,

    /// <summary>A default command could not be stored; nothing was marked, so the next start tries again.</summary>
    Failed,
}

/// <summary>The outcome of seeding.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Created">The ids of the commands that were created.</param>
/// <param name="Problem">Why the status is <see cref="SeedStatus.Failed"/>.</param>
public sealed record SeedOutcome(SeedStatus Status, IReadOnlyList<string> Created, string? Problem = null);

/// <summary>
/// Creates the default command documents of the power and session actions on the first start with an empty
/// configuration (PLAN.md S11, T306): every one is disabled and not exposed to Home Assistant, so the user only has to switch
/// it on. It happens once. A marker file in the data directory records that the defaults were offered, so a command that
/// the user deletes later is not created again, however often the agent starts.
/// </summary>
public sealed class DefaultCommandSeeder
{
    /// <summary>The name of the marker file in the data directory.</summary>
    public const string MarkerFileName = "seeded-defaults.json";

    /// <summary>The version of the marker file format.</summary>
    public const int MarkerVersion = 1;

    private readonly IConfigStore _store;
    private readonly string _markerPath;
    private readonly TimeProvider _time;
    private readonly IFileOperations _files;

    /// <summary>Creates the seeder.</summary>
    /// <param name="store">The configuration; it must have been loaded, or an empty view would be mistaken for an empty configuration.</param>
    /// <param name="markerPath">The full path of the marker file, see <see cref="MarkerFileName"/>.</param>
    /// <param name="time">The clock for the time in the marker file.</param>
    /// <param name="files">File operations for writing the marker; null uses the real file system.</param>
    public DefaultCommandSeeder(IConfigStore store, string markerPath, TimeProvider? time = null, IFileOperations? files = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);

        _store = store;
        _markerPath = markerPath;
        _time = time ?? TimeProvider.System;
        _files = files ?? FileOperations.Instance;
    }

    /// <summary>The default commands: the power actions run in the service, the session actions in the session of the user.</summary>
    public static IReadOnlyList<CommandDocument> DefaultCommands { get; } =
    [
        // The commands that can wait (T715) carry all variables of their action (Force, DelaySeconds, Message), so an automation can pass a
        // delay; their timeout is longer than the longest delay, because the run waits for the countdown.
        Command(WindowsActionNames.Shutdown, "power_shutdown", "Shut down", "Shuts the computer down.", "mdi:power", RunAsValues.System, withParameters: true),
        Command(WindowsActionNames.Restart, "power_restart", "Restart", "Restarts the computer.", "mdi:restart", RunAsValues.System, withParameters: true),
        Command(WindowsActionNames.Sleep, "power_sleep", "Sleep", "Puts the computer to sleep.", "mdi:power-sleep", RunAsValues.System, withParameters: true),
        Command(WindowsActionNames.Hibernate, "power_hibernate", "Hibernate", "Hibernates the computer.", "mdi:power-sleep", RunAsValues.System, withParameters: true),
        Command(WindowsActionNames.CancelPending, "power_cancel", "Cancel pending power action", "Cancels the delayed shutdown, restart, sleep, hibernate or log off that is waiting.", "mdi:cancel", RunAsValues.System, withParameters: false),
        Command(WindowsActionNames.LockSession, "session_lock", "Lock", "Locks the session of the logged-in user.", "mdi:lock", RunAsValues.User, withParameters: false),
        Command(WindowsActionNames.Logoff, "session_logoff", "Log off", "Logs the logged-in user off.", "mdi:logout", RunAsValues.User, withParameters: true),

        // The keep-awake switch (T715): a switch in Home Assistant whose state is the real one, the metric power.keep_awake, so it turns
        // off by itself when the time is over. Like the others it is disabled and not exposed until the user switches it on.
        KeepAwakeSwitch(),

        // The audio device switches (T713): a select in Home Assistant that lists the active devices (optionsFrom) and shows the
        // current default device (the state metric). Like the others they are disabled and not exposed until the user switches them on.
        DeviceSelect(
            WindowsActionNames.SetDefaultOutput,
            "audio_output_device",
            "Audio output device",
            "Switches the default audio playback device. The options are the active playback devices.",
            "mdi:speaker",
            SessionMetricIds.AudioOutputDevice,
            "playback"),
        DeviceSelect(
            WindowsActionNames.SetDefaultInput,
            "audio_input_device",
            "Audio input device",
            "Switches the default audio recording device. The options are the active recording devices.",
            "mdi:microphone-settings",
            SessionMetricIds.AudioInputDevice,
            "recording"),
    ];

    /// <summary>Creates the default commands when this is the first start with an empty configuration.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What happened.</returns>
    public async Task<SeedOutcome> SeedAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_markerPath))
        {
            return new SeedOutcome(SeedStatus.AlreadySeeded, []);
        }

        HashSet<string> defaultIds = [.. DefaultCommands.Select(command => command.Id)];
        IReadOnlyList<CommandDocument> existing = _store.Effective.List<CommandDocument>();

        // A first start that was interrupted left some of the defaults but no marker: finish it. Any other command means
        // that the user has been here, and the defaults are not wanted any more.
        if (existing.Any(command => !defaultIds.Contains(command.Id)))
        {
            await WriteMarkerAsync([], cancellationToken).ConfigureAwait(false);
            return new SeedOutcome(SeedStatus.SkippedNotEmpty, []);
        }

        var created = new List<string>();
        foreach (CommandDocument command in DefaultCommands)
        {
            if (existing.Any(present => present.Id == command.Id))
            {
                continue;
            }

            SaveResult<CommandDocument> result = await _store.Local.SaveAsync(command, expectedRevision: 0, cancellationToken).ConfigureAwait(false);
            if (result.IsApplied)
            {
                created.Add(command.Id);
            }
            else if (result.Status != ChangeStatus.Conflict)
            {
                string reason = result.Errors.Count > 0 ? result.Errors[0].Message : result.Status.ToString();
                return new SeedOutcome(SeedStatus.Failed, created, $"The default command '{command.Id}' could not be stored: {reason}");
            }
        }

        await WriteMarkerAsync(DefaultCommands.Select(command => command.Id), cancellationToken).ConfigureAwait(false);
        return new SeedOutcome(SeedStatus.Seeded, created);
    }

    /// <summary>The command <c>power_keep_awake</c>: a switch on the variable <c>On</c> whose state is the metric <c>power.keep_awake</c>.</summary>
    private static CommandDocument KeepAwakeSwitch()
    {
        BuiltinActionDescriptor descriptor = WindowsActionCatalog.Find(WindowsActionNames.KeepAwake)
            ?? throw new InvalidOperationException($"The built-in action '{WindowsActionNames.KeepAwake}' is not in the catalog.");
        return new CommandDocument(
            DocumentSchema.CommandVersion,
            "power_keep_awake",
            "Keep awake",
            Enabled: false,
            ExposeToHa: false,
            RunAsValues.System,
            new BuiltinAction(WindowsActionNames.KeepAwake),
            "Keeps the computer from going to sleep while the switch is on. The request ends with the agent and is not restored after a restart.",
            "mdi:coffee",
            Parameters: descriptor.Parameters,
            HaControl: new HaControl(HaControlTypes.Switch, PowerActionSpec.On, PowerActionSpec.KeepAwakeMetric),
            TimeoutSeconds: 30,
            Concurrency: ConcurrencyModes.Exclusive,
            CooldownSeconds: 0,
            CaptureOutput: true,
            Origin: DocumentOrigins.Local);
    }

    private static CommandDocument Command(string action, string id, string name, string description, string icon, string runAs, bool withParameters)
    {
        BuiltinActionDescriptor descriptor = WindowsActionCatalog.Find(action)
            ?? throw new InvalidOperationException($"The built-in action '{action}' is not in the catalog.");
        return new CommandDocument(
            DocumentSchema.CommandVersion,
            id,
            name,
            Enabled: false,
            ExposeToHa: false,
            runAs,
            new BuiltinAction(action),
            description,
            icon,
            Parameters: withParameters ? descriptor.Parameters : null,
            HaControl: new HaControl(HaControlTypes.Button),
            TimeoutSeconds: descriptor.Parameters.Any(parameter => parameter.Name == PowerActionSpec.DelaySeconds)
                ? PowerActionSpec.MaxDelaySeconds + 60
                : 30,
            Concurrency: ConcurrencyModes.Exclusive,
            CooldownSeconds: 0,
            CaptureOutput: true,
            Origin: DocumentOrigins.Local);
    }

    /// <summary>
    /// A command that switches an audio device: the variable <c>Device</c> is an enum whose options come from the device metric
    /// (<c>optionsFrom</c>), the Home Assistant control is a <c>select</c> on that variable, and the same metric reports the state.
    /// </summary>
    private static CommandDocument DeviceSelect(string action, string id, string name, string description, string icon, string metricId, string kind)
    {
        return new CommandDocument(
            DocumentSchema.CommandVersion,
            id,
            name,
            Enabled: false,
            ExposeToHa: false,
            RunAsValues.User,
            new BuiltinAction(action),
            description,
            icon,
            Parameters:
            [
                new ParameterDefinition(
                    AudioDeviceSpec.Device,
                    ParameterTypes.Choice,
                    Required: true,
                    OptionsFrom: metricId,
                    Description: "The " + kind + " device that becomes the default."),
            ],
            HaControl: new HaControl(HaControlTypes.Select, AudioDeviceSpec.Device, metricId),
            TimeoutSeconds: 30,
            Concurrency: ConcurrencyModes.Exclusive,
            CooldownSeconds: 0,
            CaptureOutput: true,
            Origin: DocumentOrigins.Local);
    }

    private async Task WriteMarkerAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", MarkerVersion);
            writer.WriteString("seededAt", _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteStartArray("commands");
            foreach (string id in ids)
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        await AtomicFile.WriteAsync(_files, _markerPath, stream.ToArray(), ownerOnly: true, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Source-generated log messages of the built-in actions (event ids 330 to 339).</summary>
internal static partial class ActionLog
{
    [LoggerMessage(EventId = 330, Level = LogLevel.Information, Message = "First start: created {Count} default commands (power and session actions). They are disabled; switch them on in the UI.")]
    public static partial void DefaultCommandsCreated(ILogger logger, int count);

    [LoggerMessage(EventId = 331, Level = LogLevel.Warning, Message = "The default commands could not be created: {Reason}")]
    public static partial void DefaultCommandsFailed(ILogger logger, string reason);
}

/// <summary>
/// Creates the default commands once the configuration is loaded (T306). It is registered after the service that loads
/// the configuration; a failure is logged and never prevents the agent from starting.
/// </summary>
public sealed class DefaultCommandSeedService : IHostedService
{
    private readonly DefaultCommandSeeder _seeder;
    private readonly ILogger _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="seeder">Creates the commands.</param>
    /// <param name="logger">Receives the messages.</param>
    public DefaultCommandSeedService(DefaultCommandSeeder seeder, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(seeder);
        ArgumentNullException.ThrowIfNull(logger);

        _seeder = seeder;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
#pragma warning disable CA1031 // Missing defaults are an inconvenience; they must not keep the agent from starting.
        try
        {
            SeedOutcome outcome = await _seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
            if (outcome.Status == SeedStatus.Seeded)
            {
                ActionLog.DefaultCommandsCreated(_logger, outcome.Created.Count);
            }
            else if (outcome.Status == SeedStatus.Failed)
            {
                ActionLog.DefaultCommandsFailed(_logger, outcome.Problem ?? "unknown");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ActionLog.DefaultCommandsFailed(_logger, exception.Message);
        }
#pragma warning restore CA1031
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
