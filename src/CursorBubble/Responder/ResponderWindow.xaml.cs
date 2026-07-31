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

    private void SendBtn_Click(object sender, RoutedEventArgs e)
    {
        InboxRecord? rec = Selected;
        if (rec is null) return;

        string text = ReplyBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = "Typ eerst een antwoord.";
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            StatusText.Text = "Kon het klembord niet gebruiken.";
            return;
        }

        // Step aside so focus can move to the terminal, then paste + enter.
        Hide();
        bool ok = WindowInput.SendReply(rec.WindowHandle, rec.WindowTitle, rec.ProjectName);

        if (ok)
        {
            InboxStore.Remove(rec.SessionId);
            ReloadInbox();
            if (InboxStore.UnansweredCount() == 0)
            {
                Close();
            }
            else
            {
                Show();
                Activate();
            }
        }
        else
        {
            Show();
            Activate();
            StatusText.Text = "Kon het bijbehorende venster niet vinden. Je antwoord staat op het klembord — plak het zelf met Ctrl+V in de sessie.";
        }
    }
}
