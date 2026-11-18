using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.IO;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Linux.Actions;

/// <summary>What <see cref="LinuxDefaultCommandSeeder.SeedAsync"/> did.</summary>
public enum LinuxSeedStatus
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
/// <param name="Problem">Why the status is <see cref="LinuxSeedStatus.Failed"/>.</param>
public sealed record LinuxSeedOutcome(LinuxSeedStatus Status, IReadOnlyList<string> Created, string? Problem = null);

/// <summary>
/// Creates the default command documents of the Linux actions on the first start with an empty configuration (PLAN.md S11, S17, T803): the
/// power and session actions through logind, the display and the media key of the desktop. Every one is disabled and not exposed to Home
/// Assistant, so the user only has to switch it on. It happens once; a marker file in the data directory records that the defaults were
/// offered, so a command that the user deletes later is not created again, however often the agent starts. A configuration that was
/// moved from Windows keeps its commands (the Windows-only ones are flagged, never removed): the seeder never touches a configuration
/// that holds a command it did not create.
/// </summary>
public sealed class LinuxDefaultCommandSeeder
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
    public LinuxDefaultCommandSeeder(IConfigStore store, string markerPath, TimeProvider? time = null, IFileOperations? files = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);

        _store = store;
        _markerPath = markerPath;
        _time = time ?? TimeProvider.System;
        _files = files ?? FileOperations.Instance;
    }

    /// <summary>The default commands: the power and session actions run in the service, the desktop actions in the session helper.</summary>
    public static IReadOnlyList<CommandDocument> DefaultCommands { get; } =
    [
        // The commands that can wait carry all variables of their action (Force, DelaySeconds, Message), so an automation can pass a delay;
        // their timeout is longer than the longest delay, because the run waits for it.
        Command(LinuxActionNames.Shutdown, "power_shutdown", "Shut down", "Shuts the computer down.", "mdi:power", RunAsValues.System, withParameters: true),
        Command(LinuxActionNames.Restart, "power_restart", "Restart", "Restarts the computer.", "mdi:restart", RunAsValues.System, withParameters: true),
        Command(LinuxActionNames.Sleep, "power_sleep", "Sleep", "Puts the computer to sleep.", "mdi:power-sleep", RunAsValues.System, withParameters: true),
        Command(LinuxActionNames.Hibernate, "power_hibernate", "Hibernate", "Hibernates the computer.", "mdi:power-sleep", RunAsValues.System, withParameters: true),
        Command(LinuxActionNames.CancelPending, "power_cancel", "Cancel pending power action", "Cancels the delayed shutdown, restart, sleep, hibernate or log off that is waiting.", "mdi:cancel", RunAsValues.System, withParameters: false),
        Command(LinuxActionNames.LockSession, "session_lock", "Lock", "Locks the session of the logged-in user.", "mdi:lock", RunAsValues.System, withParameters: false),
        Command(LinuxActionNames.Logoff, "session_logoff", "Log off", "Logs the logged-in user off.", "mdi:logout", RunAsValues.System, withParameters: true),
        Command(LinuxActionNames.DisplayOff, "display_off", "Turn displays off", "Turns the displays of the logged-in user off (GNOME, or X11 with xset).", "mdi:monitor-off", RunAsValues.User, withParameters: false),
        MediaPlayPause(),
    ];

    /// <summary>Creates the default commands when this is the first start with an empty configuration.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What happened.</returns>
    public async Task<LinuxSeedOutcome> SeedAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_markerPath))
        {
            return new LinuxSeedOutcome(LinuxSeedStatus.AlreadySeeded, []);
        }

        HashSet<string> defaultIds = [.. DefaultCommands.Select(command => command.Id)];
        IReadOnlyList<CommandDocument> existing = _store.Effective.List<CommandDocument>();

        // A first start that was interrupted left some of the defaults but no marker: finish it. Any other command means that the user has
        // been here (or brought a configuration from another computer), and the defaults are not wanted any more.
        if (existing.Any(command => !defaultIds.Contains(command.Id)))
        {
            await WriteMarkerAsync([], cancellationToken).ConfigureAwait(false);
            return new LinuxSeedOutcome(LinuxSeedStatus.SkippedNotEmpty, []);
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
                return new LinuxSeedOutcome(LinuxSeedStatus.Failed, created, $"The default command '{command.Id}' could not be stored: {reason}");
            }
        }

        await WriteMarkerAsync(DefaultCommands.Select(command => command.Id), cancellationToken).ConfigureAwait(false);
        return new LinuxSeedOutcome(LinuxSeedStatus.Seeded, created);
    }

    /// <summary>The command <c>media_play_pause</c>: a button whose key defaults to play and pause.</summary>
    private static CommandDocument MediaPlayPause()
    {
        BuiltinActionDescriptor descriptor = Descriptor(LinuxActionNames.MediaKey);
        ParameterDefinition key = descriptor.Parameters[0] with
        {
            Required = false,
            Default = JsonString(LinuxMediaKeyNames.PlayPause),
        };
        return new CommandDocument(
            DocumentSchema.CommandVersion,
            "media_play_pause",
            "Play or pause",
            Enabled: false,
            ExposeToHa: false,
            RunAsValues.User,
            new BuiltinAction(LinuxActionNames.MediaKey),
            "Plays or pauses the media player of the logged-in user (needs playerctl).",
            "mdi:play-pause",
            Parameters: [key],
            HaControl: new HaControl(HaControlTypes.Button),
            TimeoutSeconds: 30,
            Concurrency: ConcurrencyModes.Exclusive,
            CooldownSeconds: 0,
            CaptureOutput: true,
            Origin: DocumentOrigins.Local);
    }

    private static BuiltinActionDescriptor Descriptor(string action)
    {
        return LinuxActionCatalog.Find(action)
            ?? throw new InvalidOperationException($"The built-in action '{action}' is not in the catalog.");
    }

    private static JsonElement JsonString(string value)
    {
        // The values are the constant media key names: plain ASCII without characters that JSON escapes.
        using JsonDocument document = JsonDocument.Parse("\"" + value + "\"");
        return document.RootElement.Clone();
    }

    private static CommandDocument Command(string action, string id, string name, string description, string icon, string runAs, bool withParameters)
    {
        BuiltinActionDescriptor descriptor = Descriptor(action);
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

/// <summary>Source-generated log messages of the Linux actions and their default commands (event ids 3360 to 3369).</summary>
internal static partial class LinuxActionLog
{
    [LoggerMessage(EventId = 3360, Level = LogLevel.Information, Message = "First start: created {Count} default commands (power, session and desktop actions). They are disabled; switch them on in the UI.")]
    public static partial void DefaultCommandsCreated(ILogger logger, int count);

    [LoggerMessage(EventId = 3361, Level = LogLevel.Warning, Message = "The default commands could not be created: {Reason}")]
    public static partial void DefaultCommandsFailed(ILogger logger, string reason);
}

/// <summary>
/// Creates the default commands once the configuration is loaded (T803). It is registered after the service that loads the configuration; a
/// failure is logged and never prevents the agent from starting.
/// </summary>
public sealed class LinuxDefaultCommandSeedService : IHostedService
{
    private readonly LinuxDefaultCommandSeeder _seeder;
    private readonly ILogger _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="seeder">Creates the commands.</param>
    /// <param name="logger">Receives the messages.</param>
    public LinuxDefaultCommandSeedService(LinuxDefaultCommandSeeder seeder, ILogger logger)
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
            LinuxSeedOutcome outcome = await _seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
            if (outcome.Status == LinuxSeedStatus.Seeded)
            {
                LinuxActionLog.DefaultCommandsCreated(_logger, outcome.Created.Count);
            }
            else if (outcome.Status == LinuxSeedStatus.Failed)
            {
                LinuxActionLog.DefaultCommandsFailed(_logger, outcome.Problem ?? "unknown");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LinuxActionLog.DefaultCommandsFailed(_logger, exception.Message);
        }
#pragma warning restore CA1031
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
