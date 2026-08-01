using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CursorBubble.Native;

namespace CursorBubble.Config;

/// <summary>
/// One slice of the radial menu.
/// </summary>
public sealed class SegmentConfig
{
    /// <summary>Text shown on the segment.</summary>
    public string Label { get; set; } = "New";

    /// <summary>
    /// Optional built-in icon: a single glyph from the Segoe Fluent Icons /
    /// Segoe MDL2 Assets system font (e.g. "" for a folder). Empty = none.
    /// Ignored when <see cref="IconPath"/> points at an existing image.
    /// </summary>
    public string? Glyph { get; set; }

    /// <summary>Optional path to a custom icon image (png/ico); takes priority over <see cref="Glyph"/>.</summary>
    public string? IconPath { get; set; }

    public ActionType Action { get; set; } = ActionType.OpenPath;

    /// <summary>
    /// The action target: a path/URL for OpenPath, an exe path for LaunchProgram,
    /// or a script path / inline command for RunScript.
    /// </summary>
    public string Target { get; set; } = "";

    /// <summary>Extra command-line arguments (LaunchProgram / script file).</summary>
    public string? Arguments { get; set; }
}

/// <summary>
/// The visual style of the glass bubble. Defaults reproduce the clean, light
/// frosted-glass look from the design reference.
/// </summary>
public sealed class StyleConfig
{
    /// <summary>Glass tint colour as #RRGGBB.</summary>
    public string TintColor { get; set; } = "#FFFFFF";

    /// <summary>
    /// Opacity of the glass itself (0..1): the tint laid over the blurred
    /// desktop inside each segment. Lower = more see-through.
    /// </summary>
    public double TintOpacity { get; set; } = 0.42;

    /// <summary>Accent colour for the highlighted segment as #RRGGBB.</summary>
    public string HighlightColor { get; set; } = "#EAF2FF";

    /// <summary>Colour of the segment labels / centre text as #RRGGBB.</summary>
    public string LabelColor { get; set; } = "#20242C";

    /// <summary>Use acrylic blur behind the bubble. Falls back to a flat tint if off.</summary>
    public bool UseAcrylicBlur { get; set; } = true;

    /// <summary>
    /// Play the umbrella open animation and the hover lift. The property name is
    /// part of the persisted JSON, so it stays "Animate" even though it now covers
    /// both effects.
    /// </summary>
    public bool Animate { get; set; } = true;

    // ---- Layout (device-independent pixels) ----
    /// <summary>Outer radius of the ring.</summary>
    public double OuterRadius { get; set; } = 200;

    /// <summary>Inner radius (dead zone in the centre = cancel).</summary>
    public double InnerRadius { get; set; } = 60;

    /// <summary>Angular gap between segments, in degrees (visual separation).</summary>
    public double SegmentGap { get; set; } = 5;

    /// <summary>Corner rounding of a segment, in DIPs. 0 = sharp wedges.</summary>
    public double SegmentCornerRadius { get; set; } = 34;

    /// <summary>Angle (degrees, clockwise from the top) where the first segment starts.</summary>
    public double StartAngle { get; set; }
}

/// <summary>
/// Root configuration object, persisted as JSON.
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// The layout this file was written with. Bumped only when a change cannot
    /// be expressed additively — a rename, a restructure, a changed meaning.
    ///
    /// Everything so far has been additive, so a file with no version at all is
    /// simply version 1: <c>ConfigStore</c> reads unknown properties as absent
    /// and missing ones as their defaults, which is why adding a setting has
    /// never needed a migration. The number exists so the first change that
    /// <em>does</em> break that has somewhere to hook in, instead of silently
    /// resetting settings someone spent time on.
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>The version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    public ObservableCollection<SegmentConfig> Segments { get; set; } = new();

    public StyleConfig Style { get; set; } = new();

    /// <summary>Launch CursorBubble automatically when Windows starts.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Encrypted (DPAPI, per-user) Anthropic API key as persisted in config.json.
    /// Use <see cref="AiApiKey"/> to read/write the plain-text value.
    /// </summary>
    public string AiApiKeyProtected { get; set; } = "";

    /// <summary>
    /// Plain-text Anthropic API key for the "Generate with AI" feature. Not
    /// serialized — the value is stored encrypted via <see cref="AiApiKeyProtected"/>.
    ///
    /// Reads as empty when the stored value cannot be decrypted — a config
    /// carried over from another pc or Windows account. That is the truthful
    /// answer: there is no key this machine can use, and the AI screen already
    /// says so clearly. The setter throws
    /// (<see cref="System.Security.Cryptography.CryptographicException"/>) if
    /// the key cannot be encrypted, so a caller never believes it saved one.
    /// </summary>
    [JsonIgnore]
    public string AiApiKey
    {
        get => DataProtection.TryUnprotect(AiApiKeyProtected, out string plain) ? plain : "";
        set => AiApiKeyProtected = DataProtection.Protect(value);
    }

    /// <summary>Claude model used to generate scripts.</summary>
    public string AiModel { get; set; } = "claude-opus-5";

    /// <summary>
    /// Global hotkey that opens the bubble at the centre of the active screen,
    /// in keyboard mode. Stored as text (e.g. "Ctrl+Alt+Space") so config.json
    /// stays hand-editable; parsed with <c>HotkeySpec.ParseOrDefault</c>, which
    /// falls back to the default rather than leaving the user with no hotkey.
    /// </summary>
    public string MenuHotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>
    /// A sensible starter configuration so the bubble is useful on first run.
    /// </summary>
    public static AppConfig CreateDefault()
    {
        var cfg = new AppConfig();
        cfg.Segments.Add(new SegmentConfig
        {
            Label = "Open\nDocuments", Glyph = "",
            Action = ActionType.OpenPath, Target = "%USERPROFILE%\\Documents"
        });
        cfg.Segments.Add(new SegmentConfig
        {
            Label = "Open\nBrowser", Glyph = "",
            Action = ActionType.OpenPath, Target = "https://www.google.com"
        });
        cfg.Segments.Add(new SegmentConfig
        {
            Label = "Calculator", Glyph = "",
            Action = ActionType.LaunchProgram, Target = "calc.exe"
        });
        cfg.Segments.Add(new SegmentConfig
        {
            Label = "Run Backup\nScript", Glyph = "",
            Action = ActionType.RunScript,
            Target = "powershell -NoProfile -Command \"Write-Host 'Replace this with your own backup script'\""
        });
        cfg.Segments.Add(new SegmentConfig
        {
            Label = "Open\nPowerShell", Glyph = "",
            Action = ActionType.LaunchProgram, Target = "powershell.exe"
        });
        cfg.Segments.Add(new SegmentConfig
        {
            Label = "Open\nSettings", Glyph = "",
            Action = ActionType.OpenPath, Target = "ms-settings:"
        });
        cfg.Segments.Add(new SegmentConfig
        {
            Label = "Claude Code", Glyph = "",
            Action = ActionType.ClaudeInbox
        });
        return cfg;
    }
}
