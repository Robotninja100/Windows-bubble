using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CursorBubble.Accessibility;
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
        Title = items.Count > 0 ? $"Claude Code — {items.Count} pending" : "Claude Code";

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
        MessageBox.Text = string.IsNullOrWhiteSpace(rec.Message) ? "(no text provided)" : rec.Message;
        StatusText.Text = "";
        ReplyBox.Clear();
        ReplyBox.Focus();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => ReloadInbox();

    /// <summary>
    /// Ctrl+Enter sends. The reply box takes plain Enter as a newline, so the Send
    /// button cannot be <c>IsDefault</c> — this is the keyboard path in its place.
    /// </summary>
    private void ReplyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

        e.Handled = true;
        if (SendBtn.IsEnabled) SendBtn_Click(SendBtn, new RoutedEventArgs());
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F5) return;

        e.Handled = true;
        ReloadInbox();
    }

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
            Announce.Text(StatusText, "Type a reply first.");
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
            Announce.Text(StatusText, "Could not use the clipboard.");
            return;
        }

        SendBtn.IsEnabled = false;
        Announce.Text(StatusText, "Sending…");

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
            Announce.Text(StatusText, "Reply sent.");
        }
        else
        {
            // Nothing was typed — keep the session pending and leave the reply on
            // the clipboard so the user can paste it themselves.
            Show();
            Activate();
            Announce.Text(StatusText,
                "Could not bring this session's window to the front, so nothing was typed. " +
                "Your reply is on the clipboard — paste it into the session yourself with Ctrl+V.");
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
