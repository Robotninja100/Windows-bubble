using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CursorBubble.Accessibility;
using CursorBubble.Ai;
using CursorBubble.ClaudeCode;
using CursorBubble.Config;
using CursorBubble.Controls;
using CursorBubble.Diagnostics;
using CursorBubble.Input;
using CursorBubble.Native;
using CursorBubble.Overlay;
using CursorBubble.Stats;
using Microsoft.Win32;

namespace CursorBubble.Settings;

/// <summary>
/// The app's one window: an Overview of how the bubble is actually used, and the
/// pages that change it — segments, layout, glass, the AI generator, the Claude
/// Code link — with a live preview of the ring underneath the ones it applies
/// to. On save it raises <see cref="Saved"/> with the updated configuration; the
/// app persists it and rebuilds the overlay.
/// </summary>
public partial class SettingsWindow : Window
{
    private sealed record ActionOption(string Display, ActionType Value);

    private sealed record IconOption(string Name, string Glyph);

    private sealed record AiModelOption(string Display, string Id);

    // The sidebar, and the order the pages are in. The constants below index
    // into it: a switch on a bare number is how the AI page ended up one place
    // out when Overview was added in front of it.
    private const int PageOverview = 0;
    private const int PageGeneral = 1;
    private const int PageLayout = 2;
    private const int PageSegments = 3;
    private const int PageStyle = 4;
    private const int PageAi = 5;
    private const int PageClaude = 6;
    private const int PageAbout = 7;

    private static readonly NavPage[] Pages =
    {
        new(Glyphs.Home, "Overview",
            "What the bubble has done for you so far, and everything you can change."),
        new(Glyphs.Settings, "General",
            "Startup, the keyboard shortcut, and whether any of this is counted."),
        new(Glyphs.View, "Layout",
            "How big the ring is, where it starts and how its segments are spaced."),
        new(Glyphs.Edit, "Segments",
            "The shortcuts themselves: what they are called, what they look like and what they do."),
        new(Glyphs.Color, "Style",
            "Glass, animation and the three colours the bubble is drawn with."),
        new(Glyphs.Lightbulb, "AI",
            "Let Claude write a script for a segment from a plain description."),
        new(Glyphs.Message, "Claude Code",
            "Bring Claude Code sessions into the bubble and answer them from here."),
        new(Glyphs.Info, "About",
            "Version, where your files live, and the switch that deletes your statistics."),
    };

    /// <summary>
    /// Width of the bar track in the "what you actually use" chart, matching the
    /// fixed width in the template. The filled part is measured in pixels here,
    /// so the two have to agree.
    /// </summary>
    private const double SegmentBarWidth = 180;

    private const string RepositoryUrl = "https://github.com/Robotninja100/Windows-bubble";

    private static readonly AiModelOption[] AiModels =
    {
        new("Claude Opus 5 — best quality", "claude-opus-5"),
        new("Claude Sonnet 5 — faster & cheaper", "claude-sonnet-5"),
        new("Claude Haiku 4.5 — cheapest", "claude-haiku-4-5"),
    };

    // Curated icons from the Segoe Fluent Icons / Segoe MDL2 Assets system font.
    private static readonly IconOption[] IconOptions =
    {
        new("None", ""),
        new("Folder", Glyphs.Folder),
        new("Globe / Browser", Glyphs.Globe),
        new("Settings", Glyphs.Settings),
        new("Document", Glyphs.Document),
        new("Save", Glyphs.Save),
        new("Mail", Glyphs.Mail),
        new("Calendar", Glyphs.Calendar),
        new("Play", Glyphs.Play),
        new("Camera", Glyphs.Camera),
        new("Photo", Glyphs.Photo),
        new("Music", Glyphs.Music),
        new("Terminal", Glyphs.Terminal),
        new("Message / Chat", Glyphs.Message),
        new("Calculator", Glyphs.Calculator),
        new("Home", Glyphs.Home),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppConfig _working;
    private readonly RadialMenuControl _preview = new();
    private bool _suspend;

    /// <summary>
    /// The live counters. The same instance the app records into, so a bubble
    /// opened while this window is up is already in the numbers the next time
    /// the Overview page is drawn.
    /// </summary>
    private UsageStats _stats;

    /// <summary>Something has been edited and not saved.</summary>
    private bool _dirty;

    /// <summary>
    /// The user has already said they are throwing the edits away, so closing
    /// must not ask again. Set by Cancel (and by Escape, which is the same
    /// button) and by the "discard" answer to the prompt itself.
    /// </summary>
    private bool _discarding;

    /// <summary>Raised when the user saves; carries the edited configuration.</summary>
    public event Action<AppConfig>? Saved;

    public SettingsWindow(AppConfig current)
    {
        InitializeComponent();
        _working = Clone(current);
        _stats = UsageStatsStore.Current;

        PreviewBox.Child = _preview;

        ActionBox.ItemsSource = new[]
        {
            new ActionOption("Open (file / folder / URL)", ActionType.OpenPath),
            new ActionOption("Launch program", ActionType.LaunchProgram),
            new ActionOption("Run script / command", ActionType.RunScript),
            new ActionOption("Open Claude Code inbox", ActionType.ClaudeInbox),
        };
        ActionBox.DisplayMemberPath = nameof(ActionOption.Display);
        ActionBox.SelectedValuePath = nameof(ActionOption.Value);

        IconGlyphBox.ItemsSource = IconOptions;

        AiModelBox.ItemsSource = AiModels;
        AiModelBox.DisplayMemberPath = nameof(AiModelOption.Display);
        AiModelBox.SelectedValuePath = nameof(AiModelOption.Id);

        SegmentsList.ItemsSource = _working.Segments;

        // Assigning the source clears the selection the XAML asked for, so the
        // starting page is chosen here rather than there.
        NavList.ItemsSource = Pages;
        NavList.SelectedIndex = PageOverview;

        LoadFromConfig();
        WireSliders();
        FillAboutPage();

        if (_working.Segments.Count > 0)
            SegmentsList.SelectedIndex = 0;

        RebuildPreview();
        BuildOverview();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        // Windows 11: real acrylic glass → make the WPF background transparent so
        // the backdrop shows. Windows 10: keep the solid dark fallback from XAML.
        if (WindowBackdrop.Apply(hwnd))
            Background = Brushes.Transparent;
    }

    private void LoadFromConfig()
    {
        _suspend = true;

        StartWithWindowsCheck.IsChecked = _working.StartWithWindows;
        CollectStatsCheck.IsChecked = _working.CollectUsageStats;
        HotkeyBox.Text = HotkeySpec.ParseOrDefault(_working.MenuHotkey, HotkeySpec.Default).ToString();
        HotkeyHint.Text = HotkeyDescription;

        StyleConfig s = _working.Style;
        OuterRadiusSlider.Value = s.OuterRadius;
        InnerRadiusSlider.Value = s.InnerRadius;
        StartAngleSlider.Value = s.StartAngle;
        GapSlider.Value = s.SegmentGap;
        CornerSlider.Value = s.SegmentCornerRadius;

        AcrylicCheck.IsChecked = s.UseAcrylicBlur;
        AnimateCheck.IsChecked = s.Animate;
        TintOpacitySlider.Value = s.TintOpacity;
        TintColorBox.Text = s.TintColor;
        HighlightColorBox.Text = s.HighlightColor;
        LabelColorBox.Text = s.LabelColor;

        _pendingApiKey = _working.AiApiKey;
        AiApiKeyBox.Password = _pendingApiKey;
        AiModelBox.SelectedValue = _working.AiModel;

        _suspend = false;

        UpdateValueLabels();
        UpdateSwatches();
    }

    private void WireSliders()
    {
        OuterRadiusSlider.ValueChanged += (_, _) => OnLayoutChanged();
        InnerRadiusSlider.ValueChanged += (_, _) => OnLayoutChanged();
        StartAngleSlider.ValueChanged += (_, _) => OnLayoutChanged();
        GapSlider.ValueChanged += (_, _) => OnLayoutChanged();
        CornerSlider.ValueChanged += (_, _) => OnLayoutChanged();
        TintOpacitySlider.ValueChanged += (_, _) => OnStyleChanged();
    }

    // ---- navigation ---------------------------------------------------------

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PanelGeneral is null)
            return; // during initial template load

        PanelOverview.Visibility = Visibility.Collapsed;
        PanelGeneral.Visibility = Visibility.Collapsed;
        PanelLayout.Visibility = Visibility.Collapsed;
        PanelSegments.Visibility = Visibility.Collapsed;
        PanelStyle.Visibility = Visibility.Collapsed;
        PanelAi.Visibility = Visibility.Collapsed;
        PanelClaude.Visibility = Visibility.Collapsed;
        PanelAbout.Visibility = Visibility.Collapsed;

        switch (NavList.SelectedIndex)
        {
            case PageGeneral: PanelGeneral.Visibility = Visibility.Visible; break;
            case PageLayout: PanelLayout.Visibility = Visibility.Visible; break;
            case PageSegments: PanelSegments.Visibility = Visibility.Visible; break;
            case PageStyle: PanelStyle.Visibility = Visibility.Visible; break;
            case PageAi: PanelAi.Visibility = Visibility.Visible; break;
            case PageClaude: PanelClaude.Visibility = Visibility.Visible; UpdateClaudeStatus(); break;
            case PageAbout: PanelAbout.Visibility = Visibility.Visible; UpdateStatsSummaryLine(); break;
            // Rebuilt on arrival rather than once: segments added and actions
            // run while this window is open both change what it says.
            default: PanelOverview.Visibility = Visibility.Visible; BuildOverview(); break;
        }

        // The preview belongs to the pages that change how the bubble looks.
        // Everywhere else it is a third of the window showing nothing new.
        PreviewCard.Visibility = NavList.SelectedIndex is PageLayout or PageSegments or PageStyle
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void GoToSegments_Click(object sender, RoutedEventArgs e) => NavList.SelectedIndex = PageSegments;

    private void TuningArea_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement row && row.DataContext is TuningRow area)
            NavList.SelectedIndex = PageFor(area.Key);
    }

    private static int PageFor(string key) => key switch
    {
        Tunables.GeneralKey => PageGeneral,
        Tunables.LayoutKey => PageLayout,
        Tunables.SegmentsKey => PageSegments,
        Tunables.StyleKey => PageStyle,
        Tunables.AiKey => PageAi,
        Tunables.ClaudeKey => PageClaude,
        _ => PageOverview
    };

    // ---- overview -----------------------------------------------------------

    /// <summary>
    /// Fill the Overview page from the counters as they stand.
    ///
    /// Everything on it is derived; nothing is stored twice. That is what lets
    /// this be called again whenever the page is opened without anything having
    /// to be invalidated.
    /// </summary>
    private void BuildOverview()
    {
        DateTime now = DateTime.Now;
        UsageStats stats = _stats;
        int streak = stats.CurrentStreakDays(now);
        SegmentUse? favourite = stats.FavouriteSegment;

        StatTiles.ItemsSource = new[]
        {
            new StatTileItem(Glyphs.View, "Bubble opened", Number(stats.MenuOpens),
                $"{Number(stats.MouseOpens)} by gesture · {Number(stats.HotkeyOpens)} by shortcut"),

            new StatTileItem(Glyphs.Play, "Actions run", Number(stats.ActionsRun),
                stats.Cancelled > 0
                    ? $"{Number(stats.Cancelled)} let go in the cancel zone"
                    : "Nothing cancelled yet"),

            new StatTileItem(Glyphs.Star, "Favourite",
                favourite?.Label ?? "—",
                favourite is null
                    ? "Run a segment and it turns up here"
                    : $"{UsageFacts.Count(favourite.Count, "run")}, {UsageFacts.Percent(favourite.Share)} of the total"),

            new StatTileItem(Glyphs.Calendar, "Streak", UsageFacts.Count(streak, "day"),
                stats.LongestStreakDays > 0
                    ? $"Longest so far: {UsageFacts.Count(stats.LongestStreakDays, "day")}"
                    : "Use it two days running to start one"),

            new StatTileItem(Glyphs.Stopwatch, "Time saved", UsageFacts.Duration(stats.EstimatedTimeSaved),
                $"Estimated at {UsageStats.SecondsSavedPerAction} seconds a shortcut"),

            new StatTileItem(Glyphs.Keyboard, "On the ring",
                UsageFacts.Count(_working.Segments.Count, "segment"),
                $"{Tunables.TotalSettings} settings across {Tunables.Areas.Count} pages"),
        };

        FactList.ItemsSource = UsageFacts.For(stats, _working, now);

        BuildSegmentBars(stats);

        TunablesTitle.Text = $"{Tunables.TotalSettings} things you can change in here";
        TuningAreaList.ItemsSource = Tunables.Areas
            .Select(area => new TuningRow(
                area.Key,
                area.Glyph,
                area.Title,
                area.Summary,
                area.Key == Tunables.SegmentsKey
                    ? $"{Number(_working.Segments.Count)} now"
                    : UsageFacts.Count(area.SettingCount, "setting"),
                $"Go to {area.Title}"))
            .ToList();

        UpdateRailSummary();
    }

    /// <summary>
    /// The five busiest segments, as bars measured against the busiest one — a
    /// share of the total would leave every bar short on a ring where the work
    /// is spread evenly, which is the opposite of what the chart is for.
    /// </summary>
    private void BuildSegmentBars(UsageStats stats)
    {
        IReadOnlyList<SegmentUse> top = stats.TopSegments(5);

        if (top.Count == 0)
        {
            SegmentBars.ItemsSource = Array.Empty<SegmentBarItem>();
            SegmentBars.Visibility = Visibility.Collapsed;
            SegmentBarsEmpty.Visibility = Visibility.Visible;
            return;
        }

        int busiest = Math.Max(1, top[0].Count);
        SegmentBars.ItemsSource = top
            .Select(use => new SegmentBarItem(
                use.Label,
                SegmentBarWidth * use.Count / busiest,
                $"{Number(use.Count)} · {UsageFacts.Percent(use.Share)}"))
            .ToList();

        SegmentBars.Visibility = Visibility.Visible;
        SegmentBarsEmpty.Visibility = Visibility.Collapsed;
    }

    /// <summary>The two lines at the bottom of the sidebar, visible on every page.</summary>
    private void UpdateRailSummary()
    {
        if (CollectStatsCheck.IsChecked != true)
        {
            RailStatValue.Text = "Counting is off";
            RailStatCaption.Text = $"{UsageFacts.Count(_working.Segments.Count, "segment")} on the ring";
            return;
        }

        int streak = _stats.CurrentStreakDays(DateTime.Now);
        RailStatValue.Text = UsageFacts.Count(_stats.MenuOpens, "open");
        RailStatCaption.Text = streak > 1
            ? $"{streak}-day streak · {UsageFacts.Count(_working.Segments.Count, "segment")}"
            : $"{UsageFacts.Count(_working.Segments.Count, "segment")} on the ring";
    }

    private static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    // ---- about --------------------------------------------------------------

    private void FillAboutPage()
    {
        string version = VersionLabel();
        VersionText.Text = version;
        AboutVersionText.Text = $"Version {version}, running on {Environment.OSVersion.VersionString}.";

        ConfigPathText.Text = "Settings: " + ConfigStore.ConfigPath;
        StatsPathText.Text = "Statistics: " + UsageStatsStore.StatsPath;
        LogPathText.Text = "Today's log: " + Log.CurrentFile;

        UpdateStatsSummaryLine();
    }

    private void UpdateStatsSummaryLine()
    {
        if (_stats.IsEmpty)
        {
            ResetStatsStatus.Text = "Nothing has been counted yet.";
            return;
        }

        string since = _stats.FirstUsedOn is DateTime first
            ? first.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)
            : "the first run";

        ResetStatsStatus.Text =
            $"Counting since {since}: {UsageFacts.Count(_stats.MenuOpens, "open")}, " +
            $"{UsageFacts.Count(_stats.ActionsRun, "action")}. " +
            "Deleting them cannot be undone, and nothing else is affected.";
    }

    /// <summary>
    /// The informational version when there is one (it carries the "-dev" and
    /// pre-release parts the assembly version cannot), trimmed of the build
    /// metadata the SDK appends.
    /// </summary>
    private static string VersionLabel()
    {
        Assembly assembly = typeof(App).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private void OpenRepository_Click(object sender, RoutedEventArgs e) => Launch(RepositoryUrl);

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = System.IO.Path.GetDirectoryName(ConfigStore.ConfigPath) ?? "";
        if (folder.Length == 0)
        {
            MessageBox.Show(this, "There is no settings folder yet.", "CursorBubble");
            return;
        }
        Launch(folder);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        // Nothing is logged on a quiet day, so the file genuinely may not exist.
        // Saying so is better than a shell error about a missing path.
        if (!System.IO.File.Exists(Log.CurrentFile))
        {
            MessageBox.Show(this,
                "There is no log file for today yet — nothing has needed reporting.",
                "CursorBubble");
            return;
        }
        Launch(Log.CurrentFile);
    }

    /// <summary>Hand a path or URL to the shell, and say so when that fails.</summary>
    private void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open {target}.\n\n{ex.Message}",
                "CursorBubble", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ResetStats_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Delete every counter and start again from zero?\n\n" +
                "Your segments and settings are not touched.",
                "CursorBubble", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        UsageStatsStore.Reset();
        _stats = UsageStatsStore.Current;

        BuildOverview();
        Announce.Text(ResetStatsStatus, "Statistics deleted. Counting starts again from zero.");
    }

    // ---- layout & style -----------------------------------------------------

    private void OnLayoutChanged()
    {
        if (_suspend) return;
        StyleConfig s = _working.Style;
        s.OuterRadius = OuterRadiusSlider.Value;
        s.InnerRadius = InnerRadiusSlider.Value;
        s.StartAngle = StartAngleSlider.Value;
        s.SegmentGap = GapSlider.Value;
        s.SegmentCornerRadius = CornerSlider.Value;
        UpdateValueLabels();
        RebuildPreview();
        MarkDirty();
    }

    private void OnStyleChanged()
    {
        if (_suspend) return;
        StyleConfig s = _working.Style;
        s.TintOpacity = TintOpacitySlider.Value;
        UpdateValueLabels();
        RebuildPreview();
        MarkDirty();
    }

    private void Acrylic_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        _working.Style.UseAcrylicBlur = AcrylicCheck.IsChecked == true;
        RebuildPreview();
        MarkDirty();
    }

    private void Animate_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        _working.Style.Animate = AnimateCheck.IsChecked == true;
        MarkDirty();
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        _working.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        MarkDirty();
    }

    /// <summary>
    /// The counting switch. Applied to <see cref="_working"/> immediately so the
    /// sidebar can stop quoting numbers the moment it is unticked — the counters
    /// themselves keep moving until this is saved, which is what the app does
    /// with every other setting on this window.
    /// </summary>
    private void CollectStats_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        _working.CollectUsageStats = CollectStatsCheck.IsChecked == true;
        UpdateRailSummary();
        MarkDirty();
    }

    private void Color_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suspend) return;
        _working.Style.TintColor = TintColorBox.Text;
        _working.Style.HighlightColor = HighlightColorBox.Text;
        _working.Style.LabelColor = LabelColorBox.Text;
        UpdateSwatches();
        RebuildPreview();
        MarkDirty();
    }

    private void UpdateValueLabels()
    {
        OuterRadiusValue.Text = $"{OuterRadiusSlider.Value:0}px";
        InnerRadiusValue.Text = $"{InnerRadiusSlider.Value:0}px";
        StartAngleValue.Text = $"{StartAngleSlider.Value:0}°";
        GapValue.Text = $"{GapSlider.Value:0}°";
        CornerValue.Text = $"{CornerSlider.Value:0}px";
        TintOpacityValue.Text = $"{TintOpacitySlider.Value * 100:0}%";
    }

    private void UpdateSwatches()
    {
        TintSwatch.Background = BrushFrom(TintColorBox.Text);
        HighlightSwatch.Background = BrushFrom(HighlightColorBox.Text);
        LabelSwatch.Background = BrushFrom(LabelColorBox.Text);
    }

    private static SolidColorBrush BrushFrom(string hex)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
                return new SolidColorBrush(c);
        }
        catch { /* invalid hex while typing */ }
        return Brushes.Transparent;
    }

    // ---- segments -----------------------------------------------------------

    private SegmentConfig? Selected => SegmentsList.SelectedItem as SegmentConfig;

    private void SegmentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadSegmentDetail();
    }

    private void LoadSegmentDetail()
    {
        _suspend = true;
        SegmentConfig? seg = Selected;
        bool has = seg is not null;
        SegmentDetail.IsEnabled = has;

        LabelBox.Text = seg?.Label ?? "";
        TargetBox.Text = seg?.Target ?? "";
        ArgumentsBox.Text = seg?.Arguments ?? "";
        IconBox.Text = seg?.IconPath ?? "";
        ActionBox.SelectedValue = seg?.Action ?? ActionType.OpenPath;

        string glyph = seg?.Glyph ?? "";
        IconGlyphBox.SelectedItem = Array.Find(IconOptions, o => o.Glyph == glyph) ?? IconOptions[0];
        IconPreview.Text = glyph;

        _suspend = false;

        UpdateArgumentsHint();
    }

    /// <summary>
    /// Warn when the Arguments field will be ignored.
    ///
    /// A "Run script" target with no script extension is an inline command line
    /// that already carries its own arguments, so ActionRunner does not append
    /// this field to it. Silently dropping what someone typed would be worse
    /// than saying so.
    /// </summary>
    private void UpdateArgumentsHint()
    {
        SegmentConfig? seg = Selected;

        bool ignored =
            seg is { Action: ActionType.RunScript } &&
            !string.IsNullOrWhiteSpace(ArgumentsBox.Text) &&
            System.IO.Path.GetExtension(TargetBox.Text).ToLowerInvariant()
                is not (".ps1" or ".bat" or ".cmd");

        var wanted = ignored ? Visibility.Visible : Visibility.Collapsed;
        if (ArgumentsHint.Visibility == wanted)
            return;

        ArgumentsHint.Visibility = wanted;
        if (ignored)
            Announce.LiveRegionWhenShown(ArgumentsHint);
    }

    private void Detail_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suspend) return;
        SegmentConfig? seg = Selected;
        if (seg is null) return;

        seg.Label = LabelBox.Text;
        seg.Target = TargetBox.Text;
        seg.Arguments = string.IsNullOrWhiteSpace(ArgumentsBox.Text) ? null : ArgumentsBox.Text;
        seg.IconPath = string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text;

        UpdateArgumentsHint();
        SegmentsList.Items.Refresh();
        RebuildPreview();
        MarkDirty();
    }

    private void ActionBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        SegmentConfig? seg = Selected;
        if (seg is null) return;
        if (ActionBox.SelectedValue is ActionType t)
            seg.Action = t;

        UpdateArgumentsHint();
        MarkDirty();
    }

    private void IconGlyphBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        SegmentConfig? seg = Selected;
        if (seg is null) return;
        if (IconGlyphBox.SelectedItem is IconOption opt)
        {
            seg.Glyph = string.IsNullOrEmpty(opt.Glyph) ? null : opt.Glyph;
            IconPreview.Text = opt.Glyph;
            RebuildPreview();
            MarkDirty();
        }
    }

    // ---- AI script generation ----------------------------------------------

    /// <summary>
    /// Held in plain text until Save, then encrypted once.
    ///
    /// Assigning straight to <see cref="AppConfig.AiApiKey"/> here ran DPAPI on
    /// every keystroke, and now that the setter reports failure instead of
    /// quietly storing the key unprotected, it would also have to report that
    /// failure per keystroke. Both problems go away by encrypting where the user
    /// is asking for the value to be kept.
    /// </summary>
    private string _pendingApiKey = "";

    private void AiApiKey_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        _pendingApiKey = AiApiKeyBox.Password;
        MarkDirty();
    }

    private void AiModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        if (AiModelBox.SelectedValue is string id)
            _working.AiModel = id;
        MarkDirty();
    }

    private void AiGenerateBtn_Click(object sender, RoutedEventArgs e)
    {
        SegmentConfig? seg = Selected;
        if (seg is null)
        {
            MessageBox.Show(this, "Select a segment first (or add one).", "CursorBubble");
            return;
        }

        // The key as it currently stands in the box, not as it was last saved —
        // generating should work with what the user just typed.
        if (string.IsNullOrWhiteSpace(_pendingApiKey))
        {
            MessageBox.Show(this,
                "Set your Anthropic API key first, under Settings → AI.",
                "CursorBubble");
            NavList.SelectedIndex = PageAi;
            return;
        }

        var dialog = new AiScriptDialog(_pendingApiKey, _working.AiModel) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResultScript))
        {
            string name = string.IsNullOrWhiteSpace(seg.Label) ? "script" : seg.Label.Replace("\n", " ");
            string path = ScriptStore.Save(name, dialog.ResultScript!);

            seg.Action = ActionType.RunScript;
            seg.Target = path;
            seg.Arguments = null;

            UsageStatsStore.Record(s => s.RecordAiScript(DateTime.Now));

            LoadSegmentDetail();      // reflect the new target/action in the fields
            SegmentsList.Items.Refresh();
            RebuildPreview();
            MarkDirty();
        }
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        var seg = new SegmentConfig { Label = "New", Action = ActionType.OpenPath, Target = "" };
        _working.Segments.Add(seg);
        SegmentsList.SelectedItem = seg;
        RebuildPreview();
        UpdateRailSummary();
        MarkDirty();
    }

    private void RemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        SegmentConfig? seg = Selected;
        if (seg is null) return;
        int idx = _working.Segments.IndexOf(seg);
        _working.Segments.Remove(seg);
        if (_working.Segments.Count > 0)
            SegmentsList.SelectedIndex = Math.Clamp(idx, 0, _working.Segments.Count - 1);
        RebuildPreview();
        UpdateRailSummary();
        MarkDirty();
    }

    private void UpBtn_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void DownBtn_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        SegmentConfig? seg = Selected;
        if (seg is null) return;
        int idx = _working.Segments.IndexOf(seg);
        int target = idx + delta;
        if (target < 0 || target >= _working.Segments.Count) return;
        _working.Segments.Move(idx, target);
        SegmentsList.SelectedIndex = target;
        RebuildPreview();
        MarkDirty();
    }

    private void BrowseTargetBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose a program or file" };
        if (dlg.ShowDialog(this) == true)
            TargetBox.Text = dlg.FileName;
    }

    private void BrowseIconBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose an icon",
            Filter = "Images (*.png;*.ico;*.jpg)|*.png;*.ico;*.jpg|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            IconBox.Text = dlg.FileName;
    }

    // ---- Claude Code linking ------------------------------------------------

    /// <summary>
    /// Refresh the link buttons and the status line. A <paramref name="message"/>
    /// from a click handler is the outcome of what the user just did, so it wins
    /// over the generic line — and it is the one worth announcing.
    /// </summary>
    private void UpdateClaudeStatus(string? message = null)
    {
        bool linked = HookInstaller.IsInstalled();

        if (message is null)
        {
            ClaudeStatusText.Text = linked
                ? "Status: linked. New (or restarted) Claude Code sessions will show up in the bubble."
                : "Status: not linked.";
        }
        else
        {
            Announce.Text(ClaudeStatusText, message);
        }

        LinkClaudeBtn.IsEnabled = !linked;
        UnlinkClaudeBtn.IsEnabled = linked;
    }

    private void LinkClaude_Click(object sender, RoutedEventArgs e)
    {
        string message;
        try
        {
            HookInstaller.Install();
            message = "Linked! Restart running Claude Code sessions so the hooks take effect.";
        }
        catch (Exception ex)
        {
            message = "Linking failed: " + ex.Message;
        }
        // Passed in rather than assigned here: the refresh used to overwrite it,
        // so neither the success line nor the error was ever visible.
        UpdateClaudeStatus(message);
    }

    private void UnlinkClaude_Click(object sender, RoutedEventArgs e)
    {
        string? message = null;
        try
        {
            HookInstaller.Uninstall();
        }
        catch (Exception ex)
        {
            message = "Unlinking failed: " + ex.Message;
        }
        UpdateClaudeStatus(message);
    }

    // ---- preview & save -----------------------------------------------------

    private void RebuildPreview()
    {
        _preview.Build(_working);
        // Show an example highlight so the accent colour is visible in the preview.
        _preview.SetHighlight(_working.Segments.Count > 1 ? 1 : (_working.Segments.Count == 1 ? 0 : -1));
    }

    /// <summary>Note that there is something to save, and say so in the footer.</summary>
    private void MarkDirty()
    {
        if (_dirty)
            return;

        _dirty = true;
        DirtyPill.Visibility = Visibility.Visible;
    }

    // ---- hotkey capture -----------------------------------------------------

    /// <summary>
    /// Capture the combination the user presses rather than the text they type.
    /// </summary>
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Tab must get through, or the capture box is itself a keyboard trap:
        // there would be no way to leave the field without a mouse.
        if (e.Key == Key.Tab) return;

        e.Handled = true;

        // Alt combinations arrive as Key.System with the real key in SystemKey.
        // Reading e.Key alone makes Ctrl+Alt+X — the default — uncapturable.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Windows;

        // Still holding the modifiers down and nothing else yet: show progress
        // without committing to anything.
        var candidate = new HotkeySpec(modifiers, key);
        if (!HotkeySpec.TryParse(candidate.ToString(), out HotkeySpec spec))
        {
            Announce.Text(HotkeyHint, modifiers == HotkeyModifiers.None
                ? "A shortcut needs at least Ctrl, Alt, Shift or the Windows key."
                : "Keep holding and press a letter, digit or function key.");
            return;
        }

        _working.MenuHotkey = spec.ToString();
        HotkeyBox.Text = spec.ToString();
        MarkDirty();

        // Whether it can actually be registered is only known when the app tries;
        // a conflict is reported from the tray after saving.
        Announce.Text(HotkeyHint, $"Shortcut set to {spec}. It takes effect when you save.");
    }

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => Announce.Text(HotkeyHint, "Press the combination you want. Tab moves on without changing it.");

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => HotkeyHint.Text = HotkeyDescription;

    private void HotkeyResetBtn_Click(object sender, RoutedEventArgs e)
    {
        _working.MenuHotkey = HotkeySpec.Default.ToString();
        HotkeyBox.Text = HotkeySpec.Default.ToString();
        MarkDirty();
        Announce.Text(HotkeyHint, $"Shortcut reset to {HotkeySpec.Default}.");
    }

    private const string HotkeyDescription =
        "Opens the bubble in the middle of the screen. Arrow keys or 1-9 to choose, " +
        "Enter to run, Escape to cancel.";

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (TrySave())
            Close();
    }

    /// <summary>
    /// Hand the edited configuration to the app. Returns false when nothing was
    /// saved, in which case the window must stay open on what the user typed.
    /// </summary>
    private bool TrySave()
    {
        _working.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _working.CollectUsageStats = CollectStatsCheck.IsChecked == true;

        try
        {
            _working.AiApiKey = _pendingApiKey;
        }
        catch (CryptographicException ex)
        {
            // Saving the rest and dropping the key would leave the AI screen
            // showing an empty field with no explanation. Stop here instead, so
            // the window stays open on the value the user just typed.
            MessageBox.Show(this,
                "Nothing was saved: the API key could not be encrypted.\n\n" +
                ex.Message + "\n\nClear the key field if you want to save the rest " +
                "of your settings anyway.",
                "CursorBubble", MessageBoxButton.OK, MessageBoxImage.Warning);
            NavList.SelectedIndex = PageAi;
            return false;
        }

        Saved?.Invoke(_working);

        _dirty = false;
        DirtyPill.Visibility = Visibility.Collapsed;
        return true;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        // Cancel means "throw these away", so it does not ask again on the way
        // out. Escape is this button, and means the same thing.
        _discarding = true;
        Close();
    }

    /// <summary>
    /// Closing the window from its title bar is not an answer to "save or not",
    /// so ask. Cancel and Save have both already answered it.
    /// </summary>
    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!_dirty || _discarding)
            return;

        MessageBoxResult answer = MessageBox.Show(this,
            "You have unsaved changes.\n\nSave them before closing?",
            "CursorBubble", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        switch (answer)
        {
            case MessageBoxResult.Yes:
                if (!TrySave())
                    e.Cancel = true;
                break;

            case MessageBoxResult.No:
                _discarding = true;
                break;

            default:
                e.Cancel = true;
                break;
        }
    }

    private static AppConfig Clone(AppConfig src)
    {
        string json = JsonSerializer.Serialize(src, JsonOptions);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? AppConfig.CreateDefault();
    }
}
