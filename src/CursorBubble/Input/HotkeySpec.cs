using System.Globalization;
using System.Text;
using System.Windows.Input;

namespace CursorBubble.Input;

/// <summary>
/// Modifier keys of a global hotkey.
/// </summary>
/// <remarks>
/// The values are deliberately the Win32 <c>MOD_*</c> constants, so
/// <see cref="HotkeySpec.ToWin32"/> can hand the flags straight to
/// <c>RegisterHotKey</c> without a lookup table that could drift out of step.
/// A test pins the values against the documented constants.
/// </remarks>
[Flags]
public enum HotkeyModifiers
{
    None = 0,

    /// <summary>MOD_ALT.</summary>
    Alt = 0x0001,

    /// <summary>MOD_CONTROL.</summary>
    Control = 0x0002,

    /// <summary>MOD_SHIFT.</summary>
    Shift = 0x0004,

    /// <summary>MOD_WIN. The Windows key.</summary>
    Windows = 0x0008
}

/// <summary>
/// A global hotkey as the user wrote it: one or more modifiers plus one key.
///
/// Persisted as a string in config.json so the file stays hand-editable and
/// <c>ConfigStore</c> needs no custom JSON converter. Parsing therefore happens
/// at the edge, and a malformed value falls back to <see cref="Default"/>
/// rather than leaving the user with no hotkey at all.
///
/// This type is deliberately free of any Win32 or window plumbing: it is pure
/// data, so the round-trip and the flag values can be tested on any machine.
/// </summary>
public readonly record struct HotkeySpec(HotkeyModifiers Modifiers, Key Key)
{
    /// <summary>Ctrl+Alt+Space — the shipped default for opening the bubble.</summary>
    public static HotkeySpec Default => new(HotkeyModifiers.Control | HotkeyModifiers.Alt, Key.Space);

    /// <summary>Modifiers as the <c>fsModifiers</c> argument of <c>RegisterHotKey</c>.</summary>
    public uint ToWin32() => (uint)Modifiers;

    /// <summary>The key as a Win32 virtual-key code, for <c>RegisterHotKey</c>.</summary>
    public uint VirtualKey() => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>
    /// Canonical text form, e.g. <c>"Ctrl+Alt+Space"</c>. Modifiers are always
    /// written in this order regardless of how the user typed them, so a
    /// round-trip through the config file is stable.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) sb.Append("Win+");
        sb.Append(NameOf(Key));
        return sb.ToString();
    }

    /// <summary>
    /// Parse a text form such as <c>"Ctrl+Alt+Space"</c>. Returns false — rather
    /// than throwing — for anything unusable, including:
    /// <list type="bullet">
    /// <item>no modifier at all, which would swallow a plain key globally;</item>
    /// <item>a modifier used as the key itself, which cannot be registered;</item>
    /// <item>unknown key or modifier names.</item>
    /// </list>
    /// </summary>
    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        Key? key = null;

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            if (part.Length == 0) return false;

            HotkeyModifiers m = ModifierFor(part);
            if (m != HotkeyModifiers.None)
            {
                // "Ctrl+Ctrl+A" is a typo, not a hotkey.
                if (modifiers.HasFlag(m)) return false;
                modifiers |= m;
                continue;
            }

            // Everything that is not a modifier is the key, and there is exactly one.
            if (key is not null) return false;
            if (!TryParseKey(part, out Key parsed)) return false;
            key = parsed;
        }

        if (key is null || modifiers == HotkeyModifiers.None) return false;
        if (IsModifierKey(key.Value)) return false;

        spec = new HotkeySpec(modifiers, key.Value);
        return true;
    }

    /// <summary>
    /// Parse, or fall back to <paramref name="fallback"/>. Used at startup, where
    /// a bad config value must not leave the user without a way to open the bubble.
    /// </summary>
    public static HotkeySpec ParseOrDefault(string? text, HotkeySpec fallback)
        => TryParse(text, out HotkeySpec spec) ? spec : fallback;

    private static HotkeyModifiers ModifierFor(string part) => part.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => HotkeyModifiers.Control,
        "ALT" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        "WIN" or "WINDOWS" or "META" => HotkeyModifiers.Windows,
        _ => HotkeyModifiers.None
    };

    private static bool TryParseKey(string part, out Key key)
    {
        // Enum.TryParse would accept "None" and every modifier name, and it would
        // also accept a bare number as an enum value. Handle the two friendly
        // shapes explicitly and let the enum cover the rest.
        if (part.Length == 1)
        {
            char c = char.ToUpperInvariant(part[0]);
            if (c is >= 'A' and <= 'Z')
            {
                key = Key.A + (c - 'A');
                return true;
            }
            if (c is >= '0' and <= '9')
            {
                key = Key.D0 + (c - '0');
                return true;
            }
        }

        if (Enum.TryParse(part, ignoreCase: true, out Key parsed)
            && parsed != Key.None
            && Enum.IsDefined(parsed)
            // A digit string like "5" parses as the enum *value* 5, not the D5 key.
            && !int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            key = parsed;
            return true;
        }

        key = Key.None;
        return false;
    }

    private static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System;

    /// <summary>Friendly name for a key, inverting the shorthand accepted by TryParse.</summary>
    private static string NameOf(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        return key.ToString();
    }
}
