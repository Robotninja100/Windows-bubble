using CursorBubble.Controls;

namespace CursorBubble.Config;

/// <summary>
/// One group of settings, as advertised on the Overview page.
/// </summary>
/// <param name="Key">Stable identifier the settings window maps to a page.</param>
/// <param name="Glyph">A Segoe Fluent Icons codepoint, decorative only.</param>
/// <param name="Title">The group's name, matching its entry in the sidebar.</param>
/// <param name="Summary">What it is for, in one line.</param>
/// <param name="SettingCount">How many individual things it holds.</param>
public sealed record TuningArea(string Key, string Glyph, string Title, string Summary, int SettingCount);

/// <summary>
/// A catalogue of what this app lets you change.
///
/// It exists because "you can configure quite a lot" is not a fact — a number
/// is. The Overview page turns these into a row of shortcuts into the rest of
/// the window, and the counts add up to the one figure it quotes. Keep it in
/// step when a setting is added: it is the only place that claims a total, so it
/// is the only place that can be wrong about one.
/// </summary>
public static class Tunables
{
    /// <summary>Keys used by the settings window to jump to the matching page.</summary>
    public const string GeneralKey = "general";
    public const string LayoutKey = "layout";
    public const string SegmentsKey = "segments";
    public const string StyleKey = "style";
    public const string AiKey = "ai";
    public const string ClaudeKey = "claude";

    /// <summary>Every group, in sidebar order.</summary>
    public static IReadOnlyList<TuningArea> Areas { get; } = new[]
    {
        new TuningArea(GeneralKey, Glyphs.Settings, "General",
            "Start with Windows, the keyboard shortcut, and whether any of this is counted.", 3),
        new TuningArea(LayoutKey, Glyphs.View, "Layout",
            "Both radii, the starting angle, the gap between segments and their rounding.", 5),
        new TuningArea(SegmentsKey, Glyphs.Edit, "Segments",
            "Name, icon, action, target, arguments and a custom image — per segment, as many as you like.", 6),
        new TuningArea(StyleKey, Glyphs.Color, "Style",
            "Glass blur, animation, opacity and three colours.", 6),
        new TuningArea(AiKey, Glyphs.Lightbulb, "AI",
            "Your Anthropic key and which Claude model writes your scripts.", 2),
        new TuningArea(ClaudeKey, Glyphs.Message, "Claude Code",
            "Link the inbox so sessions reach the bubble.", 1),
    };

    /// <summary>The total the Overview page quotes.</summary>
    public static int TotalSettings
    {
        get
        {
            int total = 0;
            foreach (TuningArea area in Areas)
                total += area.SettingCount;
            return total;
        }
    }
}
