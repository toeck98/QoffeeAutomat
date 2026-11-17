using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Tests.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Modes;

/// <summary>The validation of a mode group document (PLAN.md S06, T717).</summary>
[Trait("Category", "Modes")]
public sealed class ModeValidationTests
{
    private static readonly HashSet<string> Stored = new(["a_enter", "a_exit", "b_enter1", "b_enter2", "b_exit", "other"], StringComparer.Ordinal);

    private static CommandDocument? Lookup(string id) => Stored.Contains(id) ? ConfigTestSupport.Command(id) : null;

    private static ValidationResult Validate(ModeGroupDocument group) => DocumentValidator.ValidateModeGroup(group, Lookup);

    private static void AssertError(ValidationResult result, string path, string code)
    {
        Assert.Contains(result.Errors, error => error.Path == path && error.Code == code);
    }

    [Fact]
    public void A_correct_group_is_valid()
    {
        ValidationResult result = Validate(ModeDocs.Standard());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Path + " " + e.Code)));
    }

    [Fact]
    public void Duplicate_mode_ids_are_rejected()
    {
        ModeGroupDocument group = ModeDocs.Group(
            "room",
            "a",
            ModeBootBehaviors.Reset,
            SequenceErrorModes.Stop,
            ModeDocs.Mode("a"),
            ModeDocs.Mode("a"));

        AssertError(Validate(group), "/modes/1/id", ValidationCodes.Duplicate);
    }

    [Fact]
    public void A_step_that_calls_an_unknown_command_is_rejected_in_enter_and_exit()
    {
        ModeGroupDocument group = ModeDocs.Group(
            "room",
            "a",
            ModeBootBehaviors.Reset,
            SequenceErrorModes.Stop,
            ModeDocs.Mode("a", ["a_enter", "missing"], ["gone"]));

        ValidationResult result = Validate(group);

        AssertError(result, "/modes/0/enter/1/command", ValidationCodes.UnknownReference);
        AssertError(result, "/modes/0/exit/0/command", ValidationCodes.UnknownReference);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void A_default_mode_that_does_not_exist_is_rejected()
    {
        ModeGroupDocument group = ModeDocs.Standard() with { DefaultMode = "zzz" };

        AssertError(Validate(group), "/defaultMode", ValidationCodes.UnknownReference);
    }

    [Fact]
    public void A_missing_default_mode_is_rejected()
    {
        ModeGroupDocument group = ModeDocs.Standard() with { DefaultMode = string.Empty };

        AssertError(Validate(group), "/defaultMode", ValidationCodes.Required);
    }

    [Fact]
    public void A_group_without_modes_is_rejected()
    {
        ModeGroupDocument group = ModeDocs.Standard() with { Modes = [] };

        AssertError(Validate(group), "/modes", ValidationCodes.Required);
    }

    [Fact]
    public void A_step_cannot_switch_the_mode_of_its_own_group()
    {
        ModeGroupDocument group = ModeDocs.Group(
            "room",
            "a",
            ModeBootBehaviors.Reset,
            SequenceErrorModes.Stop,
            ModeDocs.Mode("a", ["mode_room"]));

        AssertError(Validate(group), "/modes/0/enter/0/command", ValidationCodes.Conflict);
    }

    [Fact]
    public void A_step_cannot_switch_the_mode_of_another_group_either()
    {
        ModeGroupDocument group = ModeDocs.Group(
            "room",
            "a",
            ModeBootBehaviors.Reset,
            SequenceErrorModes.Stop,
            ModeDocs.Mode("a", exit: ["mode_desk"]));

        AssertError(Validate(group), "/modes/0/exit/0/command", ValidationCodes.Conflict);
    }

    [Fact]
    public void An_unknown_on_boot_or_on_error_value_is_rejected()
    {
        ModeGroupDocument group = ModeDocs.Standard() with { OnBoot = "sometimes", OnError = "explode" };

        ValidationResult result = Validate(group);

        Assert.Contains(result.Errors, error => error.Path == "/onBoot");
        Assert.Contains(result.Errors, error => error.Path == "/onError");
    }

    [Fact]
    public void The_group_id_must_not_collide_with_a_stored_command_mode_id()
    {
        ModeGroupDocument group = ModeDocs.Standard() with { Id = "taken" };

        ValidationResult result = DocumentValidator.ValidateModeGroup(
            group,
            id => id == "mode_taken" ? ConfigTestSupport.Command(id) : Lookup(id));

        AssertError(result, "/id", ValidationCodes.Conflict);
    }
}
