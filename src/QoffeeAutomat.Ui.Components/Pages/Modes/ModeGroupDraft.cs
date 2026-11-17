using System.Globalization;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Pages.CommandEditor;

namespace QoffeeAutomat.Ui.Components.Pages.Modes;

/// <summary>The result of turning a draft into a document.</summary>
/// <param name="Document">The document; meaningful only when <paramref name="Problems"/> is empty.</param>
/// <param name="Problems">What the editor finds wrong by itself, as JSON pointers into the document; the agent reports the other problems.</param>
public sealed record ModeGroupBuild(ModeGroupDocument Document, IReadOnlyList<ApiValidationError> Problems);

/// <summary>One mode of a group while it is edited.</summary>
public sealed class ModeDraft
{
    /// <summary>A key that stays the same while the mode moves or is renamed, for the renderer and for the default mode.</summary>
    public Guid Key { get; } = Guid.NewGuid();

    /// <summary>The mode id; it is the value of the variable <c>Mode</c> and of the metric.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>True once the id was typed or the mode was stored: the id then no longer follows the name.</summary>
    public bool IdTouched { get; set; }

    /// <summary>The steps that run when the mode is entered.</summary>
    public List<StepDraft> Enter { get; } = [];

    /// <summary>The steps that run when the mode is left.</summary>
    public List<StepDraft> Exit { get; } = [];

    /// <summary>The name to show in a list of modes: the name, else the id.</summary>
    public string Label => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>Creates the draft of a stored mode.</summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The draft.</returns>
    public static ModeDraft From(ModeDefinition mode)
    {
        ArgumentNullException.ThrowIfNull(mode);

        var draft = new ModeDraft { Id = mode.Id, Name = mode.Name, IdTouched = true };
        draft.Enter.AddRange((mode.Enter ?? []).Select(StepDraft.From));
        draft.Exit.AddRange((mode.Exit ?? []).Select(StepDraft.From));
        return draft;
    }
}

/// <summary>
/// The mode group that is being edited, as the form holds it. The default mode is held as a reference to one of the mode
/// drafts, so that renaming, moving or removing a mode cannot leave it pointing at an id that no longer exists unnoticed.
/// </summary>
public sealed class ModeGroupDraft
{
    /// <summary>The problem code for a default mode that is none of the modes.</summary>
    public const string UnknownDefaultCode = "unknown_default_mode";

    /// <summary>The problem code for a group without a mode.</summary>
    public const string NoModesCode = "no_modes";

    /// <summary>The problem code for two modes with the same id.</summary>
    public const string DuplicateModeCode = "duplicate_mode_id";

    private ModeGroupDocument? _original;
    private Guid? _defaultKey;
    private string _unknownDefault = string.Empty;

    /// <summary>True until the group was saved once.</summary>
    public bool IsNew => _original is null;

    /// <summary>The group id; fixed once the group exists.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The icon; empty for none.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>Whether the group can be switched.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether Home Assistant sees the group.</summary>
    public bool ExposeToHa { get; set; } = true;

    /// <summary>What happens after an operating-system start, see <see cref="ModeBootBehaviors"/>.</summary>
    public string OnBoot { get; set; } = ModeBootBehaviors.Reset;

    /// <summary>What a failing step does, see <see cref="SequenceErrorModes"/>.</summary>
    public string OnError { get; set; } = SequenceErrorModes.Stop;

    /// <summary>The modes, in the order that the form shows them.</summary>
    public List<ModeDraft> Modes { get; } = [];

    /// <summary>The position of the default mode in <see cref="Modes"/>, as the text of the select; empty when it is none of them.</summary>
    public string DefaultChoice
    {
        get
        {
            int index = DefaultIndex;
            return index < 0 ? string.Empty : index.ToString(CultureInfo.InvariantCulture);
        }

        set
        {
            _unknownDefault = string.Empty;
            _defaultKey = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index >= 0 && index < Modes.Count
                ? Modes[index].Key
                : null;
        }
    }

    /// <summary>The position of the default mode in <see cref="Modes"/>; -1 when it is none of them.</summary>
    public int DefaultIndex => _defaultKey is { } key ? Modes.FindIndex(mode => mode.Key == key) : -1;

    /// <summary>Starts a draft from a stored group.</summary>
    /// <param name="group">The group.</param>
    /// <returns>The draft; it remembers the revision to save against.</returns>
    public static ModeGroupDraft From(ModeGroupDocument group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var draft = new ModeGroupDraft();
        draft.Adopt(group);
        return draft;
    }

    /// <summary>Takes over a group as stored (after a save): the draft is no longer new and carries the new revision.</summary>
    /// <param name="group">The group.</param>
    public void Adopt(ModeGroupDocument group)
    {
        ArgumentNullException.ThrowIfNull(group);

        _original = group;
        Id = group.Id;
        Name = group.Name;
        Description = group.Description ?? string.Empty;
        Icon = group.Icon ?? string.Empty;
        Enabled = group.Enabled;
        ExposeToHa = group.ExposeToHa;
        OnBoot = group.OnBoot;
        OnError = group.OnError;
        Modes.Clear();
        Modes.AddRange(group.Modes.Select(ModeDraft.From));
        int index = Modes.FindIndex(mode => string.Equals(mode.Id, group.DefaultMode, StringComparison.Ordinal));
        _defaultKey = index < 0 ? null : Modes[index].Key;
        _unknownDefault = index < 0 ? group.DefaultMode : string.Empty;
    }

    /// <summary>Adds an empty mode at the end; the first mode of a group becomes its default mode.</summary>
    /// <returns>The new mode.</returns>
    public ModeDraft AddMode()
    {
        var mode = new ModeDraft();
        Modes.Add(mode);
        if (_defaultKey is null)
        {
            _defaultKey = mode.Key;
            _unknownDefault = string.Empty;
        }

        return mode;
    }

    /// <summary>Removes a mode. When it was the default mode, the first remaining mode becomes the default.</summary>
    /// <param name="index">The position of the mode.</param>
    public void RemoveMode(int index)
    {
        if (index < 0 || index >= Modes.Count)
        {
            return;
        }

        bool wasDefault = Modes[index].Key == _defaultKey;
        Modes.RemoveAt(index);
        if (wasDefault)
        {
            _defaultKey = Modes.Count > 0 ? Modes[0].Key : null;
        }
    }

    /// <summary>Moves a mode up or down.</summary>
    /// <param name="index">The position of the mode.</param>
    /// <param name="offset">-1 for up, 1 for down.</param>
    public void MoveMode(int index, int offset)
    {
        int target = index + offset;
        if (index >= 0 && index < Modes.Count && target >= 0 && target < Modes.Count)
        {
            (Modes[index], Modes[target]) = (Modes[target], Modes[index]);
        }
    }

    /// <summary>Turns the draft into a group document.</summary>
    /// <returns>The document, and the problems that the editor finds by itself.</returns>
    public ModeGroupBuild Build()
    {
        var problems = new List<ApiValidationError>();
        var modes = new List<ModeDefinition>(Modes.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Modes.Count; i++)
        {
            ModeDraft mode = Modes[i];
            string path = "/modes/" + i.ToString(CultureInfo.InvariantCulture);
            string id = mode.Id.Trim();
            if (id.Length > 0 && !seen.Add(id))
            {
                problems.Add(new ApiValidationError(path + "/id", DuplicateModeCode, "Another mode has this id."));
            }

            modes.Add(new ModeDefinition(id, mode.Name.Trim(), BuildSteps(mode.Enter, path + "/enter", problems), BuildSteps(mode.Exit, path + "/exit", problems)));
        }

        string defaultMode;
        int defaultIndex = DefaultIndex;
        if (defaultIndex >= 0)
        {
            defaultMode = modes[defaultIndex].Id;
        }
        else
        {
            defaultMode = _unknownDefault;
            if (modes.Count > 0)
            {
                problems.Add(new ApiValidationError("/defaultMode", UnknownDefaultCode, "The default mode must be one of the modes."));
            }
        }

        if (modes.Count == 0)
        {
            problems.Add(new ApiValidationError("/modes", NoModesCode, "A group needs at least one mode."));
        }

        var document = new ModeGroupDocument(
            _original?.SchemaVersion ?? DocumentSchema.CurrentVersion,
            Id.Trim(),
            Name.Trim(),
            Enabled,
            ExposeToHa,
            defaultMode,
            modes,
            Description: NullIfEmpty(Description),
            Icon: NullIfEmpty(Icon),
            OnBoot: OnBoot,
            OnError: OnError,
            Revision: _original?.Revision,
            UpdatedAt: _original?.UpdatedAt,
            Origin: _original?.Origin);
        return new ModeGroupBuild(document, problems);
    }

    private static List<SequenceStep> BuildSteps(List<StepDraft> drafts, string path, List<ApiValidationError> problems)
    {
        var steps = new List<SequenceStep>(drafts.Count);
        for (int i = 0; i < drafts.Count; i++)
        {
            steps.Add(drafts[i].ToStep(path + "/" + i.ToString(CultureInfo.InvariantCulture), problems));
        }

        return steps;
    }

    private static string? NullIfEmpty(string text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
