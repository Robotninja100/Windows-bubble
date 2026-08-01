using System.Windows.Input;
using CursorBubble.Input;
using Xunit;

namespace CursorBubble.Tests;

public class HotkeySpecTests
{
    [Fact]
    public void The_modifier_flags_are_the_Win32_MOD_constants()
    {
        // ToWin32 casts the flags straight to the fsModifiers argument of
        // RegisterHotKey. If these drift the app ships a silently wrong hotkey —
        // it registers successfully, just for the wrong combination — so the
        // values are pinned here against the documented constants.
        Assert.Equal(0x0001u, (uint)HotkeyModifiers.Alt);      // MOD_ALT
        Assert.Equal(0x0002u, (uint)HotkeyModifiers.Control);  // MOD_CONTROL
        Assert.Equal(0x0004u, (uint)HotkeyModifiers.Shift);    // MOD_SHIFT
        Assert.Equal(0x0008u, (uint)HotkeyModifiers.Windows);  // MOD_WIN

        Assert.Equal(0x0003u, HotkeySpec.Default.ToWin32());   // MOD_CONTROL | MOD_ALT
    }

    [Fact]
    public void The_default_is_Ctrl_Alt_Space()
    {
        Assert.Equal("Ctrl+Alt+Space", HotkeySpec.Default.ToString());
        Assert.Equal(Key.Space, HotkeySpec.Default.Key);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+Shift+F9")]
    [InlineData("Win+B")]
    [InlineData("Alt+7")]
    [InlineData("Ctrl+Alt+Shift+Win+Home")]
    public void Text_survives_a_round_trip(string text)
    {
        Assert.True(HotkeySpec.TryParse(text, out HotkeySpec spec));
        Assert.Equal(text, spec.ToString());
    }

    [Theory]
    [InlineData("control+alt+space")]
    [InlineData("CTRL + ALT + SPACE")]
    [InlineData("Alt+Ctrl+Space")]     // modifiers reordered
    [InlineData("Ctrl+Alt+space")]
    public void Aliases_case_spacing_and_order_all_land_on_the_canonical_form(string text)
    {
        Assert.True(HotkeySpec.TryParse(text, out HotkeySpec spec));

        // Whatever the user typed, the config file gets one spelling back.
        Assert.Equal("Ctrl+Alt+Space", spec.ToString());
        Assert.Equal(HotkeySpec.Default, spec);
    }

    [Theory]
    [InlineData("Windows+B")]
    [InlineData("Meta+B")]
    public void Windows_key_aliases_are_accepted(string text)
    {
        Assert.True(HotkeySpec.TryParse(text, out HotkeySpec spec));
        Assert.Equal(HotkeyModifiers.Windows, spec.Modifiers);
        Assert.Equal(Key.B, spec.Key);
    }

    [Fact]
    public void Single_characters_map_to_the_letter_and_digit_keys()
    {
        Assert.True(HotkeySpec.TryParse("Ctrl+q", out HotkeySpec letter));
        Assert.Equal(Key.Q, letter.Key);
        Assert.Equal("Ctrl+Q", letter.ToString());

        // "5" is the D5 key, not the enum member whose numeric value is 5.
        Assert.True(HotkeySpec.TryParse("Ctrl+5", out HotkeySpec digit));
        Assert.Equal(Key.D5, digit.Key);
        Assert.Equal("Ctrl+5", digit.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Space")]           // no modifier: would swallow the space bar globally
    [InlineData("F9")]              // ditto
    [InlineData("Ctrl")]            // modifier only, no key
    [InlineData("Ctrl+Alt")]        // ditto
    [InlineData("Ctrl+LeftShift")]  // a modifier used as the key cannot be registered
    [InlineData("Ctrl+System")]     // what WPF reports for an Alt combination
    [InlineData("Ctrl+Ctrl+A")]     // duplicated modifier is a typo
    [InlineData("Ctrl+A+B")]        // two keys
    [InlineData("Ctrl+Nonsense")]   // unknown key name
    [InlineData("Hyper+A")]         // unknown modifier name
    [InlineData("Ctrl+None")]       // Key.None is not a key
    public void Unusable_input_is_rejected_rather_than_throwing(string? text)
    {
        Assert.False(HotkeySpec.TryParse(text, out HotkeySpec spec));
        Assert.Equal(default, spec);
    }

    [Fact]
    public void ParseOrDefault_falls_back_instead_of_leaving_no_hotkey()
    {
        Assert.Equal(HotkeySpec.Default, HotkeySpec.ParseOrDefault("nonsense", HotkeySpec.Default));
        Assert.Equal(HotkeySpec.Default, HotkeySpec.ParseOrDefault(null, HotkeySpec.Default));
        Assert.Equal(HotkeySpec.Default, HotkeySpec.ParseOrDefault("", HotkeySpec.Default));

        Assert.True(HotkeySpec.TryParse("Ctrl+Shift+B", out HotkeySpec valid));
        Assert.Equal(valid, HotkeySpec.ParseOrDefault("Ctrl+Shift+B", HotkeySpec.Default));
    }

    [Fact]
    public void The_virtual_key_code_is_the_Win32_one()
    {
        // RegisterHotKey takes a VK code, not a WPF Key value; VK_SPACE is 0x20.
        Assert.Equal(0x20u, HotkeySpec.Default.VirtualKey());

        Assert.True(HotkeySpec.TryParse("Ctrl+A", out HotkeySpec a));
        Assert.Equal(0x41u, a.VirtualKey()); // VK_A
    }
}
