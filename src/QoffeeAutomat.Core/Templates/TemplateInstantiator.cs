using System.Globalization;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Templates;

/// <summary>The outcome of <see cref="TemplateInstantiator.InstantiateAsync"/>.</summary>
public enum TemplateInstantiationStatus
{
    /// <summary>The command, sensor or mode group was created.</summary>
    Created,

    /// <summary>There is no template with this id.</summary>
    NotFound,

    /// <summary>The template could not be added; <see cref="TemplateInstantiationResult.Errors"/> says why.</summary>
    Invalid,
}

/// <summary>The result of adding a template to the configuration.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Instantiation">What was created; set when <paramref name="Status"/> is <see cref="TemplateInstantiationStatus.Created"/>.</param>
/// <param name="Errors">Why the template was not added; empty otherwise.</param>
public sealed record TemplateInstantiationResult(
    TemplateInstantiationStatus Status,
    TemplateInstantiation? Instantiation,
    IReadOnlyList<ValidationError> Errors);

/// <summary>
/// Adds a template of the gallery to the configuration as a new command, sensor or mode group (PLAN.md S08, S16, T701,
/// T717). The id is the id of the template, made unique with a numeric suffix (<c>set_volume</c>, <c>set_volume_2</c>,
/// ...); every copy gets its own copy of the script files under a unique name, so editing one command never changes
/// another. A command template whose sequence, or a mode template whose steps, run other command templates brings the
/// ones that are missing along and reuses those that already exist. Everything goes through the config store, so a copy
/// is validated like any document that the user saves. Adding is serialized, so two clicks never pick the same id.
/// </summary>
public sealed class TemplateInstantiator
{
    private const int MaxAttempts = 8;
    private const int MaxNumber = 10_000;
    private const int MaxIdLength = 63;
    private const int MaxNameLength = 100;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly TemplateCatalogSource _templates;
    private readonly IConfigStore _config;

    /// <summary>Creates an instantiator.</summary>
    /// <param name="templates">The templates.</param>
    /// <param name="config">The configuration that receives the copies.</param>
    public TemplateInstantiator(TemplateCatalogSource templates, IConfigStore config)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(config);

        _templates = templates;
        _config = config;
    }

    /// <summary>Adds a template to the configuration.</summary>
    /// <param name="templateId">The template id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result.</returns>
    public async Task<TemplateInstantiationResult> InstantiateAsync(string templateId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(templateId);

        TemplateCatalog catalog = await _templates.GetAsync(cancellationToken).ConfigureAwait(false);
        TemplateEntry? entry = catalog.Find(templateId);
        if (entry is null)
        {
            return new TemplateInstantiationResult(TemplateInstantiationStatus.NotFound, null, []);
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = new Run();
            string? id;
            if (entry.Command is not null)
            {
                id = await AddCommandAsync(catalog, entry, keepId: false, run, cancellationToken).ConfigureAwait(false);
            }
            else if (entry.Mode is not null)
            {
                id = await AddModeAsync(catalog, entry, run, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                id = await AddSensorAsync(entry, run, cancellationToken).ConfigureAwait(false);
            }

            return id is null
                ? new TemplateInstantiationResult(TemplateInstantiationStatus.Invalid, null, run.Errors)
                : new TemplateInstantiationResult(
                    TemplateInstantiationStatus.Created,
                    new TemplateInstantiation(entry.Kind, id, [.. run.Created]),
                    []);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Makes an id unique by appending <c>_2</c>, <c>_3</c>, ... and keeps it within the length of an id.</summary>
    /// <param name="baseId">The wanted id.</param>
    /// <param name="exists">Tells whether an id is taken.</param>
    /// <returns>The id and the number of the copy (1 when the wanted id was free).</returns>
    public static (string Id, int Number) UniqueId(string baseId, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(baseId);
        ArgumentNullException.ThrowIfNull(exists);

        if (!exists(baseId))
        {
            return (baseId, 1);
        }

        for (int number = 2; number <= MaxNumber; number++)
        {
            string suffix = "_" + number.ToString(CultureInfo.InvariantCulture);
            string candidate = Truncate(baseId, MaxIdLength - suffix.Length) + suffix;
            if (!exists(candidate))
            {
                return (candidate, number);
            }
        }

        throw new InvalidOperationException("No free id was found for " + baseId + ".");
    }

    private async Task<string?> AddCommandAsync(TemplateCatalog catalog, TemplateEntry entry, bool keepId, Run run, CancellationToken cancellationToken)
    {
        CommandDocument template = entry.Command!;
        if (!run.Chain.Add(entry.Id))
        {
            run.Errors.Add(new ValidationError("/action/steps", ValidationCodes.Cycle, $"The template '{entry.Id}' requires itself."));
            return null;
        }

        try
        {
            if (!await AddRequiredAsync(catalog, entry, "/action/steps", run, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            Dictionary<string, string>? scripts = await CopyScriptsAsync(entry, run, cancellationToken).ConfigureAwait(false);
            if (scripts is null)
            {
                return null;
            }

            CommandAction action = template.Action is PowerShellAction { Source: PowerShellSources.File, Script: { } scriptName } powerShell
                ? powerShell with { Script = scripts[scriptName] }
                : template.Action;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                string id;
                int number;
                if (keepId)
                {
                    if (_config.Effective.GetDocument<CommandDocument>(entry.Id) is not null)
                    {
                        // Somebody created the command in the meantime; the sequence can use it as it is.
                        await DeleteScriptsAsync(scripts.Values, run, cancellationToken).ConfigureAwait(false);
                        return entry.Id;
                    }

                    (id, number) = (entry.Id, 1);
                }
                else
                {
                    (id, number) = UniqueId(entry.Id, candidate => _config.Effective.GetDocument<CommandDocument>(candidate) is not null);
                }

                CommandDocument document = template with
                {
                    Id = id,
                    Name = CopyName(template.Name, number),
                    Action = action,
                };

                // Revision zero means "must not exist yet", so a command that appeared since the id was chosen is not overwritten.
                SaveResult<CommandDocument> saved = await _config.Local.SaveAsync(document, 0, cancellationToken).ConfigureAwait(false);
                switch (saved.Status)
                {
                    case ChangeStatus.Applied:
                        run.Created.Add(new TemplateCreatedItem(TemplateKinds.Command, id));
                        return id;
                    case ChangeStatus.Conflict:
                        continue;
                    default:
                        await DeleteScriptsAsync(scripts.Values, run, cancellationToken).ConfigureAwait(false);
                        run.Errors.AddRange(saved.Errors);
                        return null;
                }
            }

            await DeleteScriptsAsync(scripts.Values, run, cancellationToken).ConfigureAwait(false);
            run.Errors.Add(new ValidationError("/id", ValidationCodes.Conflict, "No free id was found; try again."));
            return null;
        }
        finally
        {
            run.Chain.Remove(entry.Id);
        }
    }

    /// <summary>Creates the command templates that a sequence or mode template runs and that do not exist yet.</summary>
    /// <returns>True when every required command exists afterwards; false (with errors in <paramref name="run"/>) otherwise.</returns>
    private async Task<bool> AddRequiredAsync(TemplateCatalog catalog, TemplateEntry entry, string errorPath, Run run, CancellationToken cancellationToken)
    {
        foreach (string required in entry.Requires)
        {
            if (_config.Effective.GetDocument<CommandDocument>(required) is not null)
            {
                continue;
            }

            TemplateEntry? dependency = catalog.Find(required);
            if (dependency?.Command is null)
            {
                run.Errors.Add(new ValidationError(errorPath, ValidationCodes.UnknownReference, $"The command '{required}' is not a template."));
                return false;
            }

            // The step refers to the command by id, so the dependency keeps the id of its template.
            if (await AddCommandAsync(catalog, dependency, keepId: true, run, cancellationToken).ConfigureAwait(false) is null)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<string?> AddModeAsync(TemplateCatalog catalog, TemplateEntry entry, Run run, CancellationToken cancellationToken)
    {
        ModeGroupDocument template = entry.Mode!;

        if (!await AddRequiredAsync(catalog, entry, "/modes", run, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            (string id, int number) = UniqueId(entry.Id, candidate => _config.Effective.GetDocument<ModeGroupDocument>(candidate) is not null);
            ModeGroupDocument document = template with
            {
                Id = id,
                Name = CopyName(template.Name, number),
            };

            // Revision zero means "must not exist yet", so a group that appeared since the id was chosen is not overwritten.
            SaveResult<ModeGroupDocument> saved = await _config.Local.SaveAsync(document, 0, cancellationToken).ConfigureAwait(false);
            switch (saved.Status)
            {
                case ChangeStatus.Applied:
                    run.Created.Add(new TemplateCreatedItem(TemplateKinds.Mode, id));
                    return id;
                case ChangeStatus.Conflict:
                    continue;
                default:
                    run.Errors.AddRange(saved.Errors);
                    return null;
            }
        }

        run.Errors.Add(new ValidationError("/id", ValidationCodes.Conflict, "No free id was found; try again."));
        return null;
    }

    private async Task<string?> AddSensorAsync(TemplateEntry entry, Run run, CancellationToken cancellationToken)
    {
        SensorDocument template = entry.Sensor!;

        Dictionary<string, string>? scripts = await CopyScriptsAsync(entry, run, cancellationToken).ConfigureAwait(false);
        if (scripts is null)
        {
            return null;
        }

        SensorSource source = template.Source is PowerShellSensorSource { Source: PowerShellSources.File, Script: { } scriptName } powerShell
            ? powerShell with { Script = scripts[scriptName] }
            : template.Source;

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            (string id, int number) = UniqueId(entry.Id, candidate => _config.Effective.GetDocument<SensorDocument>(candidate) is not null);
            SensorDocument document = template with
            {
                Id = id,
                Name = CopyName(template.Name, number),
                Source = source,
            };

            SaveResult<SensorDocument> saved = await _config.Local.SaveAsync(document, 0, cancellationToken).ConfigureAwait(false);
            switch (saved.Status)
            {
                case ChangeStatus.Applied:
                    run.Created.Add(new TemplateCreatedItem(TemplateKinds.Sensor, id));
                    return id;
                case ChangeStatus.Conflict:
                    continue;
                default:
                    await DeleteScriptsAsync(scripts.Values, run, cancellationToken).ConfigureAwait(false);
                    run.Errors.AddRange(saved.Errors);
                    return null;
            }
        }

        await DeleteScriptsAsync(scripts.Values, run, cancellationToken).ConfigureAwait(false);
        run.Errors.Add(new ValidationError("/id", ValidationCodes.Conflict, "No free id was found; try again."));
        return null;
    }

    /// <summary>Copies the scripts of a template under names that are not taken yet.</summary>
    /// <returns>The template script name mapped to the name of the copy; null (with errors in <paramref name="run"/>) when a script could not be written.</returns>
    private async Task<Dictionary<string, string>?> CopyScriptsAsync(TemplateEntry entry, Run run, CancellationToken cancellationToken)
    {
        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string content) in entry.Scripts)
        {
            string target = await FreeScriptNameAsync(name, cancellationToken).ConfigureAwait(false);
            ScriptResult result = await _config.Local.SaveScriptAsync(target, content, null, cancellationToken).ConfigureAwait(false);
            if (!result.IsApplied)
            {
                await DeleteScriptsAsync(copies.Values, run, cancellationToken).ConfigureAwait(false);
                if (result.Errors.Count > 0)
                {
                    run.Errors.AddRange(result.Errors);
                }
                else
                {
                    run.Errors.Add(new ValidationError("/action/script", ValidationCodes.Invalid, $"The script '{target}' could not be written."));
                }

                return null;
            }

            copies[name] = target;
            run.Created.Add(new TemplateCreatedItem(TemplateKinds.Script, target));
        }

        return copies;
    }

    private async Task<string> FreeScriptNameAsync(string name, CancellationToken cancellationToken)
    {
        if (await _config.Effective.GetScriptAsync(name, cancellationToken).ConfigureAwait(false) is null)
        {
            return name;
        }

        string extension = Path.GetExtension(name);
        string stem = name[..^extension.Length];
        for (int number = 2; number <= MaxNumber; number++)
        {
            string suffix = "_" + number.ToString(CultureInfo.InvariantCulture) + extension;
            string candidate = Truncate(stem, ScriptNames.MaxLength - suffix.Length) + suffix;
            if (await _config.Effective.GetScriptAsync(candidate, cancellationToken).ConfigureAwait(false) is null)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No free script name was found for " + name + ".");
    }

    private async Task DeleteScriptsAsync(IEnumerable<string> names, Run run, CancellationToken cancellationToken)
    {
        foreach (string name in names.ToArray())
        {
            await _config.Local.DeleteScriptAsync(name, null, cancellationToken).ConfigureAwait(false);
            run.Created.RemoveAll(item => item.Kind == TemplateKinds.Script && item.Id == name);
        }
    }

    private static string CopyName(string name, int number)
    {
        if (number <= 1)
        {
            return name;
        }

        string suffix = " (" + number.ToString(CultureInfo.InvariantCulture) + ")";
        return Truncate(name, MaxNameLength - suffix.Length) + suffix;
    }

    private static string Truncate(string value, int length)
    {
        return value.Length <= length ? value : value[..length];
    }

    private sealed class Run
    {
        public List<TemplateCreatedItem> Created { get; } = [];

        public List<ValidationError> Errors { get; } = [];

        public HashSet<string> Chain { get; } = new(StringComparer.Ordinal);
    }
}
