using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Pages.CommandEditor;
using QoffeeAutomat.Ui.Components.Pages.Modes;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.Modes;

/// <summary>The mode group draft (T717): it turns the form into a document and keeps the default mode pointing at a mode.</summary>
[Trait("Category", "Modes")]
public sealed class ModeGroupDraftTests
{
    [Fact]
    public void A_new_draft_is_new_and_the_first_mode_becomes_the_default()
    {
        var draft = new ModeGroupDraft();
        Assert.True(draft.IsNew);
        Assert.Equal(-1, draft.DefaultIndex);

        draft.AddMode().Id = "normal";
        draft.AddMode().Id = "study";

        Assert.Equal(0, draft.DefaultIndex);
        Assert.Equal("0", draft.DefaultChoice);
    }

    [Fact]
    public void A_complete_draft_builds_a_document_with_the_steps_of_every_mode()
    {
        var draft = new ModeGroupDraft { Id = " pc ", Name = " PC modes ", OnBoot = ModeBootBehaviors.ApplyDefault, OnError = SequenceErrorModes.Continue };
        ModeDraft normal = draft.AddMode();
        normal.Id = "normal";
        normal.Name = "Normal";
        normal.Enter.Add(new StepDraft { CommandId = "restore", VarsText = "{\"Profile\":\"Desk\"}" });
        normal.Exit.Add(new StepDraft { Kind = StepKinds.Delay, DelayText = "2" });
        ModeDraft study = draft.AddMode();
        study.Id = "study";
        study.Name = "Study";

        ModeGroupBuild build = draft.Build();

        Assert.Empty(build.Problems);
        ModeGroupDocument group = build.Document;
        Assert.Equal("pc", group.Id);
        Assert.Equal("PC modes", group.Name);
        Assert.Equal("normal", group.DefaultMode);
        Assert.Equal(ModeBootBehaviors.ApplyDefault, group.OnBoot);
        Assert.Equal(SequenceErrorModes.Continue, group.OnError);
        Assert.Equal(["normal", "study"], group.Modes.Select(mode => mode.Id).ToArray());
        SequenceStep enter = Assert.Single(group.Modes[0].Enter!);
        Assert.Equal("restore", enter.Command);
        Assert.Equal("Desk", enter.Vars!["Profile"].GetString());
        Assert.Equal(2.0, Assert.Single(group.Modes[0].Exit!).DelaySeconds);
        Assert.Empty(group.Modes[1].Enter!);
    }

    [Fact]
    public void A_stored_group_round_trips_with_its_revision_and_is_not_new()
    {
        ModeGroupDocument stored = Samples.ModeGroup("pc", "PC modes", true, "normal", "study", "tv") with { DefaultMode = "study", Revision = 7 };

        var draft = ModeGroupDraft.From(stored);
        ModeGroupBuild build = draft.Build();

        Assert.False(draft.IsNew);
        Assert.Equal(1, draft.DefaultIndex);
        Assert.Empty(build.Problems);
        Assert.Equal(7, build.Document.Revision);
        Assert.Equal("study", build.Document.DefaultMode);
        Assert.Equal(stored.Modes.Select(mode => mode.Id).ToArray(), build.Document.Modes.Select(mode => mode.Id).ToArray());
    }

    [Fact]
    public void The_default_follows_its_mode_when_the_mode_is_renamed_or_moved()
    {
        var draft = ModeGroupDraft.From(Samples.ModeGroup("pc", "PC", true, "normal", "study", "tv"));
        draft.DefaultChoice = "1";

        draft.Modes[1].Id = "learn";
        draft.MoveMode(1, -1);

        Assert.Equal(0, draft.DefaultIndex);
        Assert.Equal("learn", draft.Build().Document.DefaultMode);
    }

    [Fact]
    public void Removing_the_default_mode_makes_the_first_remaining_mode_the_default()
    {
        var draft = ModeGroupDraft.From(Samples.ModeGroup("pc", "PC", true, "normal", "study", "tv"));
        draft.DefaultChoice = "2";

        draft.RemoveMode(2);

        Assert.Equal(0, draft.DefaultIndex);
        Assert.Equal("normal", draft.Build().Document.DefaultMode);

        draft.RemoveMode(0);
        draft.RemoveMode(0);
        Assert.Equal(-1, draft.DefaultIndex);
    }

    [Fact]
    public void A_group_without_a_mode_is_a_problem_at_the_modes()
    {
        ModeGroupBuild build = new ModeGroupDraft { Id = "pc", Name = "PC" }.Build();

        ApiValidationError problem = Assert.Single(build.Problems);
        Assert.Equal("/modes", problem.Path);
        Assert.Equal(ModeGroupDraft.NoModesCode, problem.Code);
    }

    [Fact]
    public void Two_modes_with_the_same_id_are_a_problem_at_the_second_one()
    {
        var draft = new ModeGroupDraft { Id = "pc", Name = "PC" };
        draft.AddMode().Id = "normal";
        draft.AddMode().Id = "normal";

        ModeGroupBuild build = draft.Build();

        ApiValidationError problem = Assert.Single(build.Problems);
        Assert.Equal("/modes/1/id", problem.Path);
        Assert.Equal(ModeGroupDraft.DuplicateModeCode, problem.Code);
    }

    [Fact]
    public void A_default_that_is_none_of_the_modes_is_a_problem_at_the_default()
    {
        var draft = ModeGroupDraft.From(Samples.ModeGroup("pc", "PC", true, "normal", "study") with { DefaultMode = "gone" });

        ModeGroupBuild build = draft.Build();

        Assert.Equal(-1, draft.DefaultIndex);
        ApiValidationError problem = Assert.Single(build.Problems);
        Assert.Equal("/defaultMode", problem.Path);
        Assert.Equal(ModeGroupDraft.UnknownDefaultCode, problem.Code);
        Assert.Equal("gone", build.Document.DefaultMode);

        draft.DefaultChoice = "1";
        Assert.Empty(draft.Build().Problems);
    }

    [Fact]
    public void A_step_that_cannot_be_read_is_reported_at_its_place_in_the_mode()
    {
        var draft = new ModeGroupDraft { Id = "pc", Name = "PC" };
        ModeDraft mode = draft.AddMode();
        mode.Id = "normal";
        mode.Exit.Add(new StepDraft { CommandId = "restore" });
        mode.Exit.Add(new StepDraft { CommandId = "restore", VarsText = "[1]" });

        ModeGroupBuild build = draft.Build();

        ApiValidationError problem = Assert.Single(build.Problems);
        Assert.Equal("/modes/0/exit/1/vars", problem.Path);
        Assert.Equal(StepDraft.InvalidJsonCode, problem.Code);
    }

    [Fact]
    public void Empty_description_and_icon_are_left_out()
    {
        var draft = new ModeGroupDraft { Id = "pc", Name = "PC", Description = "  ", Icon = "" };
        draft.AddMode().Id = "normal";

        ModeGroupDocument group = draft.Build().Document;

        Assert.Null(group.Description);
        Assert.Null(group.Icon);
    }
}
