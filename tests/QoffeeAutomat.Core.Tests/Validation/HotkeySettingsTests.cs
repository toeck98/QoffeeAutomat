using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Validation;

/// <summary>The key combinations and the hotkey list of the settings (PLAN.md S16, T708).</summary>
[Trait("Category", "Hotkeys")]
public sealed class HotkeySettingsTests
{
    private static AgentSettingsDocument Settings(params HotkeySettings[] hotkeys) =>
        new(DocumentSchema.CurrentVersion, "Gaming-PC", Hotkeys: hotkeys);

    [Theory]
    [InlineData("Ctrl+Alt+F9", "Ctrl+Alt+F9", 0x78)]
    [InlineData("alt+ctrl+f9", "Ctrl+Alt+F9", 0x78)]
    [InlineData(" Control + Shift + a ", "Ctrl+Shift+A", 0x41)]
    [InlineData("Win+Shift+M", "Shift+Win+M", 0x4D)]
    [InlineData("Ctrl+7", "Ctrl+7", 0x37)]
    [InlineData("Ctrl+Alt+F24", "Ctrl+Alt+F24", 0x87)]
    [InlineData("Ctrl+Num0", "Ctrl+Num0", 0x60)]
    [InlineData("Ctrl+space", "Ctrl+Space", 0x20)]
    [InlineData("Alt+mediaplaypause", "Alt+MediaPlayPause", 0xB3)]
    [InlineData("Ctrl+Alt+PageDown", "Ctrl+Alt+PageDown", 0x22)]
    public void Keys_are_parsed_case_insensitively_into_one_canonical_form(string text, string canonical, int virtualKey)
    {
        Assert.True(HotkeyGesture.TryParse(text, out HotkeyGesture? gesture, out string? error), error);

        Assert.Equal(canonical, gesture!.Canonical);
        Assert.Equal(virtualKey, gesture.VirtualKey);
    }

    [Fact]
    public void The_modifiers_are_the_flags_of_RegisterHotKey()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+Win+F1", out HotkeyGesture? gesture, out _));

        Assert.Equal(1 | 2 | 4 | 8, (int)gesture!.Modifiers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("F9")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl++F9")]
    [InlineData("+F9")]
    [InlineData("Ctrl+F9+")]
    [InlineData("Ctrl+Ctrl+F9")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+Hyper+F9")]
    [InlineData("Ctrl+Num10")]
    [InlineData("Ctrl+Alt+Ä")]
    public void Keys_that_cannot_be_a_global_hotkey_are_refused_with_a_reason(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out HotkeyGesture? gesture, out string? error));

        Assert.Null(gesture);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_key_without_a_modifier_is_refused_because_it_would_block_the_key_for_every_program()
    {
        Assert.False(HotkeyGesture.TryParse("F9", out _, out string? error));

        Assert.Contains("modifier", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_without_hotkeys_and_with_valid_hotkeys_are_valid()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateSettings(new AgentSettingsDocument(DocumentSchema.CurrentVersion, "Gaming-PC")));
        TestDocuments.AssertValid(DocumentValidator.ValidateSettings(Settings(
            new HotkeySettings("mute_mic", "Ctrl+Alt+F9"),
            new HotkeySettings("next", "Ctrl+Alt+MediaNext", Enabled: false))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Mute")]
    [InlineData("1abc")]
    [InlineData("has space")]
    public void A_hotkey_name_must_be_a_lower_case_identifier(string name)
    {
        TestDocuments.AssertError(DocumentValidator.ValidateSettings(Settings(new HotkeySettings(name, "Ctrl+Alt+F9"))), "/hotkeys/0/name", ValidationCodes.Invalid);
    }

    [Fact]
    public void Keys_that_cannot_be_parsed_are_reported_at_the_keys()
    {
        ValidationResult result = DocumentValidator.ValidateSettings(Settings(new HotkeySettings("a", "F9")));

        TestDocuments.AssertError(result, "/hotkeys/0/keys", ValidationCodes.Invalid);
    }

    [Fact]
    public void A_name_can_be_used_once()
    {
        ValidationResult result = DocumentValidator.ValidateSettings(Settings(
            new HotkeySettings("a", "Ctrl+Alt+F9"),
            new HotkeySettings("a", "Ctrl+Alt+F10")));

        TestDocuments.AssertError(result, "/hotkeys/1/name", ValidationCodes.Duplicate);
    }

    [Fact]
    public void A_combination_can_be_used_once_among_the_enabled_hotkeys_whatever_the_spelling()
    {
        ValidationResult result = DocumentValidator.ValidateSettings(Settings(
            new HotkeySettings("a", "Ctrl+Alt+F9"),
            new HotkeySettings("b", "alt + control + f9")));

        TestDocuments.AssertError(result, "/hotkeys/1/keys", ValidationCodes.Conflict);
    }

    [Fact]
    public void A_disabled_hotkey_does_not_take_the_combination()
    {
        ValidationResult result = DocumentValidator.ValidateSettings(Settings(
            new HotkeySettings("a", "Ctrl+Alt+F9", Enabled: false),
            new HotkeySettings("b", "Ctrl+Alt+F9")));

        TestDocuments.AssertValid(result);
    }

    [Fact]
    public void At_most_32_hotkeys_can_be_defined()
    {
        HotkeySettings[] many = [.. Enumerable.Range(1, 33).Select(index => new HotkeySettings("hotkey_" + index, "Ctrl+Alt+" + (index <= 24 ? "F" + index : "Num" + (index - 25))))];

        ValidationResult result = DocumentValidator.ValidateSettings(Settings(many));

        TestDocuments.AssertError(result, "/hotkeys", ValidationCodes.Invalid);
    }
}
