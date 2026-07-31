using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CursorBubble.Ai;
using CursorBubble.ClaudeCode;
using CursorBubble.Config;
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
        new("Claude Opus 5 — beste kwaliteit", "claude-opus-5"),
        new("Claude Sonnet 5 — sneller & goedkoper", "claude-sonnet-5"),
        new("Claude Haiku 4.5 — goedkoopst", "claude-haiku-4-5"),
    };

    // Curated icons from the Segoe Fluent Icons / Segoe MDL2 Assets system font.
    private static readonly IconOption[] IconOptions =
    {
        new("Geen", ""),
        new("Map", ""),
        new("Wereld / Browser", ""),
        new("Instellingen", ""),
        new("Document", ""),
        new("Opslaan", ""),
        new("Mail", ""),
        new("Agenda", ""),
        new("Afspelen", ""),
        new("Camera", ""),
        new("Foto", ""),
        new("Muziek", ""),
        new("Terminal", ""),
        new("Rekenmachine", ""),
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
        ConfigPathText.Text = "Instellingen worden bewaard in: " + ConfigStore.ConfigPath;

        ActionBox.ItemsSource = new[]
        {
            new ActionOption("Openen (bestand / map / URL)", ActionType.OpenPath),
            new ActionOption("Programma starten", ActionType.LaunchProgram),
            new ActionOption("Script / commando uitvoeren", ActionType.RunScript),
            new ActionOption("Claude Code inbox openen", ActionType.ClaudeInbox),
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

        AiApiKeyBox.Password = _working.AiApiKey;
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
        if (PanelAlgemeen is null)
            return; // during initial template load

        PanelAlgemeen.Visibility = Visibility.Collapsed;
        PanelLayout.Visibility = Visibility.Collapsed;
        PanelSegmenten.Visibility = Visibility.Collapsed;
        PanelStijl.Visibility = Visibility.Collapsed;
        PanelAi.Visibility = Visibility.Collapsed;
        PanelClaude.Visibility = Visibility.Collapsed;

        switch (NavList.SelectedIndex)
        {
            case 1: PanelLayout.Visibility = Visibility.Visible; break;
            case 2: PanelSegmenten.Visibility = Visibility.Visible; break;
            case 3: PanelStijl.Visibility = Visibility.Visible; break;
            case 4: PanelAi.Visibility = Visibility.Visible; break;
            case 5: PanelClaude.Visibility = Visibility.Visible; UpdateClaudeStatus(); break;
            default: PanelAlgemeen.Visibility = Visibility.Visible; break;
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

    private static Brush BrushFrom(string hex)
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

    private void AiApiKey_Changed(object sender, RoutedEventArgs e)
    {
        if (_suspend) return;
        _working.AiApiKey = AiApiKeyBox.Password;
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
            MessageBox.Show(this, "Kies eerst een segment (of voeg er een toe).", "CursorBubble");
            return;
        }

        if (string.IsNullOrWhiteSpace(_working.AiApiKey))
        {
            MessageBox.Show(this,
                "Stel eerst je Anthropic API-sleutel in bij Instellingen → AI.",
                "CursorBubble");
            NavList.SelectedIndex = 4;
            return;
        }

        var dialog = new AiScriptDialog(_working.AiApiKey, _working.AiModel) { Owner = this };
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
        var seg = new SegmentConfig { Label = "Nieuw", Action = ActionType.OpenPath, Target = "" };
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
        var dlg = new OpenFileDialog { Title = "Kies een programma of bestand" };
        if (dlg.ShowDialog(this) == true)
            TargetBox.Text = dlg.FileName;
    }

    private void BrowseIconBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Kies een icoon",
            Filter = "Afbeeldingen (*.png;*.ico;*.jpg)|*.png;*.ico;*.jpg|Alle bestanden (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            IconBox.Text = dlg.FileName;
    }

    // ---- Claude Code linking ------------------------------------------------

    private void UpdateClaudeStatus()
    {
        bool linked = HookInstaller.IsInstalled();
        ClaudeStatusText.Text = linked
            ? "Status: gekoppeld. Nieuwe (of herstarte) Claude Code-sessies melden zich in de bubbel."
            : "Status: niet gekoppeld.";
        LinkClaudeBtn.IsEnabled = !linked;
        UnlinkClaudeBtn.IsEnabled = linked;
    }

    private void LinkClaude_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            HookInstaller.Install();
            ClaudeStatusText.Text = "Gekoppeld! Herstart lopende Claude Code-sessies zodat de hooks actief worden.";
        }
        catch (Exception ex)
        {
            ClaudeStatusText.Text = "Koppelen mislukt: " + ex.Message;
        }
        UpdateClaudeStatus();
    }

    private void UnlinkClaude_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            HookInstaller.Uninstall();
        }
        catch (Exception ex)
        {
            ClaudeStatusText.Text = "Ontkoppelen mislukt: " + ex.Message;
        }
        UpdateClaudeStatus();
    }

    // ---- preview & save -----------------------------------------------------

    private void RebuildPreview()
    {
        _preview.Build(_working);
        // Show an example highlight so the accent colour is visible in the preview.
        _preview.SetHighlight(_working.Segments.Count > 1 ? 1 : (_working.Segments.Count == 1 ? 0 : -1));
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        _working.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
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
