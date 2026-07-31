using System.Globalization;
using System.Text.Json.Serialization;

namespace CursorBubble.ClaudeCode;

/// <summary>State of a Claude Code session as reported by a hook.</summary>
public enum SessionState
{
    /// <summary>The session finished its turn (Stop hook).</summary>
    Stopped,

    /// <summary>The session is waiting for input / asked something (Notification hook).</summary>
    Waiting
}

/// <summary>
/// One pending Claude Code session in the inbox. Persisted as JSON per session.
/// </summary>
public sealed class InboxRecord
{
    public string SessionId { get; set; } = "";

    /// <summary>Working directory of the session.</summary>
    public string Cwd { get; set; } = "";

    /// <summary>Last path segment of <see cref="Cwd"/> — shown as the project name.</summary>
    public string ProjectName { get; set; } = "";

    public SessionState State { get; set; } = SessionState.Stopped;

    /// <summary>The message/question text (last assistant message, or notification text).</summary>
    public string Message { get; set; } = "";

    /// <summary>Raw Claude Code notification type, if any (e.g. agent_needs_input).</summary>
    public string? NotificationType { get; set; }

    /// <summary>Handle of the window that owns this session's terminal (0 if unknown).</summary>
    public long WindowHandle { get; set; }

    /// <summary>Title of that window at capture time (used to re-find it later).</summary>
    public string WindowTitle { get; set; } = "";

    /// <summary>Owning process name (e.g. Code, WindowsTerminal, pwsh).</summary>
    public string ProcessName { get; set; } = "";

    /// <summary>UTC time of the latest event for this session (ISO 8601).</summary>
    public string Timestamp { get; set; } = "";

    // ---- display helpers (not persisted) ----

    [JsonIgnore]
    public string StateText => State == SessionState.Waiting ? "Waiting for you" : "Finished";

    [JsonIgnore]
    public string DisplayProject => string.IsNullOrWhiteSpace(ProjectName) ? "(unknown project)" : ProjectName;

    [JsonIgnore]
    public string Snippet
    {
        get
        {
            string oneLine = Message.Replace("\r", " ").Replace("\n", " ").Trim();
            return oneLine.Length <= 70 ? oneLine : oneLine[..70] + "…";
        }
    }

    [JsonIgnore]
    public string WhenLocal
    {
        get
        {
            if (DateTime.TryParse(Timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime dt))
                // Shown to the user, so their own clock format is the right one.
                return dt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
            return "";
        }
    }
}
