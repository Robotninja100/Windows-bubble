using CursorBubble.Config;
using CursorBubble.Input;
using Xunit;

namespace CursorBubble.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void Round_trip_preserves_the_configuration()
    {
        AppConfig original = AppConfig.CreateDefault();
        original.Style.OuterRadius = 275;
        original.Style.SegmentCornerRadius = 41;
        original.StartWithWindows = true;

        AppConfig? loaded = ConfigStore.Deserialize(ConfigStore.Serialize(original));

        Assert.NotNull(loaded);
        Assert.Equal(original.Segments.Count, loaded!.Segments.Count);
        Assert.Equal(275, loaded.Style.OuterRadius);
        Assert.Equal(41, loaded.Style.SegmentCornerRadius);
        Assert.True(loaded.StartWithWindows);
        Assert.Equal(original.Segments[0].Label, loaded.Segments[0].Label);
        Assert.Equal(original.Segments[0].Glyph, loaded.Segments[0].Glyph);
    }

    [Fact]
    public void Actions_persist_as_names_not_numbers()
    {
        // Enum-as-string keeps the file readable and survives reordering the enum.
        AppConfig config = AppConfig.CreateDefault();
        string json = ConfigStore.Serialize(config);

        Assert.Contains("\"RunScript\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ClaudeInbox\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_config_from_an_older_build_still_loads()
    {
        // SegmentOpacity was removed when TintOpacity became the single glass
        // knob. An existing config.json must keep working rather than resetting
        // the user to defaults.
        const string legacy = """
        {
          "Segments": [
            { "Label": "Old", "Action": "OpenPath", "Target": "C:\\Temp" }
          ],
          "Style": { "TintOpacity": 0.5, "SegmentOpacity": 0.9, "SomethingInvented": 3 },
          "StartWithWindows": true
        }
        """;

        AppConfig? loaded = ConfigStore.Deserialize(legacy);

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Segments);
        Assert.Equal("Old", loaded.Segments[0].Label);
        Assert.Equal(0.5, loaded.Style.TintOpacity);
        Assert.True(loaded.StartWithWindows);
    }

    [Fact]
    public void Property_names_are_matched_case_insensitively()
    {
        AppConfig? loaded = ConfigStore.Deserialize("""{ "style": { "outerradius": 123 } }""");

        Assert.NotNull(loaded);
        Assert.Equal(123, loaded!.Style.OuterRadius);
    }

    [Fact]
    public void The_plain_api_key_is_never_written_to_disk()
    {
        var config = new AppConfig { AiApiKey = "sk-ant-secret-value" };

        string json = ConfigStore.Serialize(config);

        Assert.DoesNotContain("sk-ant-secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"AiApiKey\":", json, StringComparison.Ordinal);
        Assert.Contains("AiApiKeyProtected", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_configuration_is_usable_as_shipped()
    {
        AppConfig config = AppConfig.CreateDefault();

        Assert.NotEmpty(config.Segments);
        Assert.All(config.Segments, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));

        // Every default segment carries an icon; "Claude Code" used to be bare.
        Assert.All(config.Segments, s => Assert.False(string.IsNullOrWhiteSpace(s.Glyph)));

        // Two neighbouring segments sharing a glyph look like a duplicate.
        string[] glyphs = config.Segments.Select(s => s.Glyph!).ToArray();
        Assert.Equal(glyphs.Length, glyphs.Distinct().Count());

        // Everything except the inbox needs somewhere to go.
        Assert.All(config.Segments.Where(s => s.Action != ActionType.ClaudeInbox),
                   s => Assert.False(string.IsNullOrWhiteSpace(s.Target)));
    }

    [Fact]
    public void A_config_written_before_the_hotkey_existed_still_gets_one()
    {
        // This is what protects existing users: their config.json has no
        // MenuHotkey key at all, and they must not end up with a blank one.
        const string json = """
            { "StartWithWindows": true, "AiModel": "claude-opus-5", "Segments": [] }
            """;

        AppConfig? loaded = ConfigStore.Deserialize(json);

        Assert.NotNull(loaded);
        Assert.Equal("Ctrl+Alt+Space", loaded!.MenuHotkey);
        Assert.True(HotkeySpec.TryParse(loaded.MenuHotkey, out HotkeySpec spec));
        Assert.Equal(HotkeySpec.Default, spec);
    }

    [Fact]
    public void The_hotkey_persists_as_readable_text()
    {
        AppConfig original = AppConfig.CreateDefault();
        original.MenuHotkey = "Ctrl+Shift+F9";

        string json = ConfigStore.Serialize(original);

        // Not asserted as a literal substring: System.Text.Json's default encoder
        // escapes '+', so what lands in the file is "Ctrl+Shift+F9".
        // Still a plain string anyone can hand-edit, and a hand-written
        // "Ctrl+Shift+F9" reads back identically — which is the part that matters.
        Assert.Contains("MenuHotkey", json, StringComparison.Ordinal);

        AppConfig? loaded = ConfigStore.Deserialize(json);
        Assert.Equal("Ctrl+Shift+F9", loaded!.MenuHotkey);
        Assert.Equal("Ctrl+Shift+F9", ConfigStore.Deserialize(
            """{ "MenuHotkey": "Ctrl+Shift+F9" }""")!.MenuHotkey);
    }
}
