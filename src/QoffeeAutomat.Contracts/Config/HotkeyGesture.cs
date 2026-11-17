using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace QoffeeAutomat.Contracts.Config;

/// <summary>The modifier keys of a global hotkey; the values are those of <c>RegisterHotKey</c> (<c>MOD_ALT</c> and so on).</summary>
[Flags]
public enum HotkeyModifiers
{
    /// <summary>No modifier. Never valid for a global hotkey.</summary>
    None = 0,

    /// <summary>The Alt key.</summary>
    Alt = 1,

    /// <summary>The Ctrl key.</summary>
    Control = 2,

    /// <summary>The Shift key.</summary>
    Shift = 4,

    /// <summary>The Windows key.</summary>
    Windows = 8,
}

/// <summary>
/// A key combination for a global hotkey (PLAN.md S16, T708), written like <c>Ctrl+Alt+F9</c>: one or more modifiers (<c>Ctrl</c>,
/// <c>Alt</c>, <c>Shift</c>, <c>Win</c>) and exactly one key, joined by <c>+</c>, not case sensitive. A key is a letter, a digit, <c>F1</c>
/// to <c>F24</c>, a numeric keypad key (<c>Num0</c> to <c>Num9</c>), or one of the names in <see cref="KeyNames"/>. At least one
/// modifier is required: a global hotkey without one would take the key away from every program. The parser is pure and
/// platform independent; the SessionHost turns the result into a <c>RegisterHotKey</c> call and the settings validator rejects what
/// it cannot parse.
/// </summary>
public sealed record HotkeyGesture
{
    private static readonly FrozenDictionary<string, int> NamedKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Enter"] = 0x0D,
        ["Tab"] = 0x09,
        ["Escape"] = 0x1B,
        ["Backspace"] = 0x08,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["Pause"] = 0x13,
        ["PrintScreen"] = 0x2C,
        ["VolumeMute"] = 0xAD,
        ["VolumeDown"] = 0xAE,
        ["VolumeUp"] = 0xAF,
        ["MediaNext"] = 0xB0,
        ["MediaPrevious"] = 0xB1,
        ["MediaStop"] = 0xB2,
        ["MediaPlayPause"] = 0xB3,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private HotkeyGesture(HotkeyModifiers modifiers, int virtualKey, string keyName)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        KeyName = keyName;
    }

    /// <summary>The names that can be used for keys besides letters, digits, function keys and keypad digits.</summary>
    public static IReadOnlyCollection<string> KeyNames => NamedKeys.Keys;

    /// <summary>The modifiers; never <see cref="HotkeyModifiers.None"/>.</summary>
    public HotkeyModifiers Modifiers { get; }

    /// <summary>The Windows virtual-key code of the key.</summary>
    public int VirtualKey { get; }

    /// <summary>The name of the key in its canonical spelling, for example <c>F9</c> or <c>A</c>.</summary>
    public string KeyName { get; }

    /// <summary>
    /// The canonical spelling: modifiers in the order Ctrl, Alt, Shift, Win, then the key (<c>Ctrl+Alt+F9</c>). Two spellings of the
    /// same combination have the same canonical form.
    /// </summary>
    public string Canonical
    {
        get
        {
            var text = new StringBuilder();
            if (Modifiers.HasFlag(HotkeyModifiers.Control))
            {
                text.Append("Ctrl+");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Alt))
            {
                text.Append("Alt+");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Shift))
            {
                text.Append("Shift+");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Windows))
            {
                text.Append("Win+");
            }

            return text.Append(KeyName).ToString();
        }
    }

    /// <summary>Parses a key combination.</summary>
    /// <param name="text">The text, for example <c>ctrl + alt + F9</c>.</param>
    /// <param name="gesture">The combination when the text is valid; otherwise null.</param>
    /// <param name="error">Why the text is not valid; null when it is.</param>
    /// <returns>True when the text is a valid key combination.</returns>
    public static bool TryParse(string? text, out HotkeyGesture? gesture, out string? error)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Keys are required, for example Ctrl+Alt+F9.";
            return false;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        string? keyName = null;
        int virtualKey = 0;

        foreach (string rawPart in text.Split('+'))
        {
            string part = rawPart.Trim();
            if (part.Length == 0)
            {
                error = "Keys must be names joined by '+', for example Ctrl+Alt+F9.";
                return false;
            }

            HotkeyModifiers modifier = ParseModifier(part);
            if (modifier != HotkeyModifiers.None)
            {
                if (modifiers.HasFlag(modifier))
                {
                    error = "The modifier " + part + " is given twice.";
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            if (keyName is not null)
            {
                error = "Only one key can be given besides the modifiers (found " + keyName + " and " + part + ").";
                return false;
            }

            if (!TryParseKey(part, out virtualKey, out keyName))
            {
                error = "The key '" + part + "' is not known. Use a letter, a digit, F1 to F24, Num0 to Num9 or a name such as Space, Home or MediaPlayPause.";
                return false;
            }
        }

        if (keyName is null)
        {
            error = "A key is required besides the modifiers, for example Ctrl+Alt+F9.";
            return false;
        }

        if (modifiers == HotkeyModifiers.None)
        {
            error = "At least one of Ctrl, Alt, Shift or Win is required; a global hotkey without a modifier would block the key for every program.";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey, keyName);
        error = null;
        return true;
    }

    private static HotkeyModifiers ParseModifier(string part)
    {
        if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
        {
            return HotkeyModifiers.Control;
        }

        if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
        {
            return HotkeyModifiers.Alt;
        }

        if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            return HotkeyModifiers.Shift;
        }

        if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return HotkeyModifiers.Windows;
        }

        return HotkeyModifiers.None;
    }

    private static bool TryParseKey(string part, out int virtualKey, out string? name)
    {
        virtualKey = 0;
        name = null;

        if (part.Length == 1)
        {
            char c = char.ToUpperInvariant(part[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                // The virtual-key codes of letters and digits are their upper-case ASCII codes.
                virtualKey = c;
                name = new string(c, 1);
                return true;
            }

            return false;
        }

        if ((part[0] is 'F' or 'f') && int.TryParse(part.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int function) && function is >= 1 and <= 24)
        {
            virtualKey = 0x70 + function - 1;
            name = "F" + function.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (part.Length == 4
            && part.StartsWith("Num", StringComparison.OrdinalIgnoreCase)
            && part[3] is >= '0' and <= '9')
        {
            virtualKey = 0x60 + (part[3] - '0');
            name = "Num" + part[3];
            return true;
        }

        if (NamedKeys.TryGetValue(part, out virtualKey))
        {
            // The canonical spelling is the one of the table.
            name = NamedKeys.Keys.First(key => key.Equals(part, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        return false;
    }
}
