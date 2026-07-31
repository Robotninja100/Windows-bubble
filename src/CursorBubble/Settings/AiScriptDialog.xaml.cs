using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CursorBubble.Accessibility;
using CursorBubble.Ai;
using CursorBubble.Native;

namespace CursorBubble.Settings;

/// <summary>
/// Dialog that turns a plain-language description into a PowerShell script via
/// Claude. The generated script is shown for review and only returned when the
/// user clicks "Gebruiken" — it is never run from here.
/// </summary>
public partial class AiScriptDialog : Window
{
    private readonly string _apiKey;
    private readonly string _model;

    /// <summary>The approved script text, set only when the dialog returns true.</summary>
    public string? ResultScript { get; private set; }

    public AiScriptDialog(string apiKey, string model)
    {
        InitializeComponent();
        _apiKey = apiKey;
        _model = model;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (WindowBackdrop.Apply(hwnd))
            Background = Brushes.Transparent;
    }

    private async void GenerateBtn_Click(object sender, RoutedEventArgs e)
    {
        string description = DescriptionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            Announce.Text(StatusText, "Describe what the script should do first.");
            return;
        }

        GenerateBtn.IsEnabled = false;
        UseBtn.IsEnabled = false;
        Announce.Text(StatusText, "Generating…");

        try
        {
            GeneratedScript result = await ScriptGenerator.GenerateAsync(_apiKey, _model, description);

            ExplanationText.Text = string.IsNullOrWhiteSpace(result.Explanation)
                ? "(no explanation given)"
                : result.Explanation;

            if (!string.IsNullOrWhiteSpace(result.Warnings))
            {
                WarningsText.Text = result.Warnings;
                WarningBox.Visibility = Visibility.Visible;
                // The box was Collapsed a moment ago, so it has no automation peer
                // until a layout pass has run — hence the deferred raise.
                Announce.LiveRegionWhenShown(WarningsText);
            }
            else
            {
                WarningBox.Visibility = Visibility.Collapsed;
            }

            ScriptBox.Text = result.Script;
            ResultInfo.Visibility = Visibility.Visible;
            ScriptArea.Visibility = Visibility.Visible;
            UseBtn.IsEnabled = true;
            Announce.Text(StatusText, "Done — review the script below.");
        }
        catch (Exception ex)
        {
            Announce.Text(StatusText, ex.Message);
        }
        finally
        {
            GenerateBtn.IsEnabled = true;
        }
    }

    private void UseBtn_Click(object sender, RoutedEventArgs e)
    {
        string script = ScriptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(script))
        {
            Announce.Text(StatusText, "There is no script to use.");
            return;
        }
        ResultScript = script;
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
