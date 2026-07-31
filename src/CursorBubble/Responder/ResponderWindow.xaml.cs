using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CursorBubble.ClaudeCode;
using CursorBubble.Native;

namespace CursorBubble.Responder;

/// <summary>
/// Glass "cursor window" listing pending Claude Code sessions. Shows the
/// question/status and lets the user type a reply that is delivered to the
/// owning terminal window via clipboard + Ctrl+V + Enter.
/// </summary>
public partial class ResponderWindow : Window
{
    public ResponderWindow()
    {
        InitializeComponent();
        ReloadInbox();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (WindowBackdrop.Apply(hwnd))
            Background = Brushes.Transparent;
    }

    private InboxRecord? Selected => SessionsList.SelectedItem as InboxRecord;

    /// <summary>Reload the pending-session list from disk.</summary>
    public void ReloadInbox()
    {
        List<InboxRecord> items = InboxStore.LoadAll();
        object? previous = SessionsList.SelectedItem is InboxRecord r ? r.SessionId : null;

        SessionsList.ItemsSource = items;
        Title = items.Count > 0 ? $"Claude Code — {items.Count} openstaand" : "Claude Code";

        if (items.Count == 0)
        {
            LoadDetail();
            return;
        }

        // Keep the current selection if it still exists, else select the first.
        InboxRecord? keep = previous is string sid ? items.FirstOrDefault(x => x.SessionId == sid) : null;
        SessionsList.SelectedItem = keep ?? items[0];
    }

    private void SessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadDetail();

    private void LoadDetail()
    {
        InboxRecord? rec = Selected;
        if (rec is null)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        ProjectHeader.Text = rec.DisplayProject;
        MetaText.Text = $"{rec.StateText} · {rec.Cwd} · {rec.WhenLocal}";
        MessageBox.Text = string.IsNullOrWhiteSpace(rec.Message) ? "(geen tekst meegegeven)" : rec.Message;
        StatusText.Text = "";
        ReplyBox.Clear();
        ReplyBox.Focus();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => ReloadInbox();

    private void DismissBtn_Click(object sender, RoutedEventArgs e)
    {
        InboxRecord? rec = Selected;
        if (rec is null) return;
        InboxStore.Remove(rec.SessionId);
        ReloadInbox();
    }

    private async void SendBtn_Click(object sender, RoutedEventArgs e)
    {
        InboxRecord? rec = Selected;
        if (rec is null) return;

        string text = ReplyBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "Typ eerst een antwoord.";
            return;
        }

        // Remember what the user had on the clipboard so we can put it back.
        string? previousClipboard = TryGetClipboardText();

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            StatusText.Text = "Kon het klembord niet gebruiken.";
            return;
        }

        SendBtn.IsEnabled = false;
        StatusText.Text = "Bezig met versturen…";

        // Step aside so focus can move to the terminal. Awaiting keeps the UI
        // responsive while the (slow) focus + paste happens on a worker thread.
        Hide();
        await Task.Delay(80);

        bool delivered;
        try
        {
            delivered = await WindowInput.SendReplyAsync(rec.WindowHandle, rec.WindowTitle, rec.ProjectName);
        }
        catch
        {
            delivered = false;
        }
        finally
        {
            SendBtn.IsEnabled = true;
        }

        if (delivered)
        {
            RestoreClipboard(previousClipboard);
            InboxStore.Remove(rec.SessionId);
            ReloadInbox();

            if (SessionsList.Items.Count == 0)
            {
                Close();
                return;
            }

            Show();
            Activate();
            StatusText.Text = "Antwoord verstuurd.";
        }
        else
        {
            // Nothing was typed — keep the session pending and leave the reply on
            // the clipboard so the user can paste it themselves.
            Show();
            Activate();
            StatusText.Text = "Kon het venster van deze sessie niet activeren; er is niets getypt. " +
                              "Je antwoord staat op het klembord — plak het zelf met Ctrl+V in de sessie.";
        }
    }

    private static string? TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void RestoreClipboard(string? previous)
    {
        try
        {
            if (string.IsNullOrEmpty(previous))
                Clipboard.Clear();
            else
                Clipboard.SetText(previous);
        }
        catch
        {
            // leaving the reply on the clipboard is an acceptable fallback
        }
    }
}
