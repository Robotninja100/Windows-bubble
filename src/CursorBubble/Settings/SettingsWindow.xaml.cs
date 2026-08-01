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
using CursorBubble.Input;
using CursorBubble.Native;
using CursorBubble.Overlay;
using Microsoft.Win32;

namespace CursorBubble.Settings;

/// <summary>
/// The in-app configuration window: edit segments, layout and glass style with a
/// live preview of the bubble. On save it raises <see cref="Saved"/> with the
/// updated configuration; the app persists it and rebuilds the overlay.
/// </summary>
public partial class SettingsWindow : Window
{
    private sealed record ActionOption(string Display, ActionType Value);

    private sealed record IconOption(string Name, string Glyph);

    private sealed record AiModelOption(string Display, string Id);

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
        new("Folder", ""),
        new("Globe / Browser", ""),
        new("Settings", ""),
        new("Document", ""),
        new("Save", ""),
        new("Mail", ""),
        new("Calendar", ""),
        new("Play", ""),
        new("Camera", ""),
        new("Photo", ""),
        new("Music", ""),
        new("Terminal", ""),
        new("Message / Chat", ""),
        new("Calculator", ""),
        new("Home", ""),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppConfig _working;
    private readonly RadialMenuControl _preview = new();
    private bool _suspend;

    /// <summary>Raised when the user saves; carries the edited configuration.</summary>
    public event Action<AppConfig>? Saved;

    public SettingsWindow(AppConfig current)
    {
        InitializeComponent();
        _working = Clone(current);

        PreviewBox.Child = _preview;
        ConfigPathText.Text = "Settings are stored in: " + ConfigStore.ConfigPath;

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

        LoadFromConfig();
        WireSliders();

        if (_working.Segments.Count > 0)
            SegmentsList.SelectedIndex = 0;

        RebuildPreview();
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

        PanelGeneral.Visibility = Visibility.Collapsed;
        PanelLayout.Visibility = Visibility.Collapsed;
        PanelSegments.Visibility = Visibility.Collapsed;
        PanelStyle.Visibility = Visibility.Collapsed;
        PanelAi.Visibility = Visibility.Collapsed;
        PanelClaude.Visibility = Visibility.Collapsed;

        switch (NavList.SelectedIndex)
        {
            case 1: PanelLayout.Visibility = Visibility.Visible; break;
            case 2: PanelSegments.Visibility = Visibility.Visible; break;
            case 3: PanelStyle.Visibility = Visibility.Visible; break;
            case 4: PanelAi.Visibility = Visibility.Visible; break;
            case 5: PanelClaude.Visibility = Visibility.Visible; UpdateClaudeStatus(); break;
            default: PanelGeneral.Visibility = Visibility.Visible; break;
        }
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
    }

    private void OnStyleChanged()
    {
        if (_suspend) return;
        StyleConfig s = _working.Style;
        s.TintOpacity = TintOpacitySlider.Value;
        UpdateValueLabels();
        RebuildPreview();
    }

    private void Acrylic_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        _working.Style.UseAcrylicBlur = AcrylicCheck.IsChecked == true;
        RebuildPreview();
    }

    private void Animate_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        _working.Style.Animate = AnimateCheck.IsChecked == true;
    }

    private void Color_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suspend) return;
        _working.Style.TintColor = TintColorBox.Text;
        _working.Style.HighlightColor = HighlightColorBox.Text;
        _working.Style.LabelColor = LabelColorBox.Text;
        UpdateSwatches();
        RebuildPreview();
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
    }

    private void ActionBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        SegmentConfig? seg = Selected;
        if (seg is null) return;
        if (ActionBox.SelectedValue is ActionType t)
            seg.Action = t;

        UpdateArgumentsHint();
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
    }

    private void AiModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        if (AiModelBox.SelectedValue is string id)
            _working.AiModel = id;
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
            NavList.SelectedIndex = 4;
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

            LoadSegmentDetail();      // reflect the new target/action in the fields
            SegmentsList.Items.Refresh();
            RebuildPreview();
        }
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        var seg = new SegmentConfig { Label = "New", Action = ActionType.OpenPath, Target = "" };
        _working.Segments.Add(seg);
        SegmentsList.SelectedItem = seg;
        RebuildPreview();
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
        Announce.Text(HotkeyHint, $"Shortcut reset to {HotkeySpec.Default}.");
    }

    private const string HotkeyDescription =
        "Opens the bubble in the middle of the screen. Arrow keys or 1-9 to choose, " +
        "Enter to run, Escape to cancel.";

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        _working.StartWithWindows = StartWithWindowsCheck.IsChecked == true;

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
            NavList.SelectedIndex = 4;
            return;
        }

        Saved?.Invoke(_working);
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private static AppConfig Clone(AppConfig src)
    {
        string json = JsonSerializer.Serialize(src, JsonOptions);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? AppConfig.CreateDefault();
    }
}
