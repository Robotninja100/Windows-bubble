using System.IO;
using System.Text.Json;
using CursorBubble.Native;

namespace CursorBubble.ClaudeCode;

/// <summary>
/// Runs when CursorBubble is launched by a Claude Code hook (<c>--hook</c>).
/// Reads the hook JSON from stdin and records the session in the inbox. This
/// path must not start any UI.
/// </summary>
public static class HookHandler
{
    public static int Run()
    {
        try
        {
            string json = Console.In.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json))
                return 0;

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string sessionId = GetString(root, "session_id");
            if (string.IsNullOrEmpty(sessionId))
                return 0;

            string eventName = GetString(root, "hook_event_name");
            string cwd = GetString(root, "cwd");
            string? notificationType = HasString(root, "notification_type") ? GetString(root, "notification_type") : null;

            string message = GetString(root, "last_assistant_message");
            if (string.IsNullOrWhiteSpace(message))
                message = GetString(root, "message");

            SessionState state = eventName.Equals("Notification", StringComparison.OrdinalIgnoreCase)
                ? SessionState.Waiting
                : SessionState.Stopped;

            WindowInput.CaptureOwnerWindow(out long handle, out string title, out string processName);

            var record = new InboxRecord
            {
                SessionId = sessionId,
                Cwd = cwd,
                ProjectName = ProjectNameFrom(cwd),
                State = state,
                Message = message.Trim(),
                NotificationType = notificationType,
                WindowHandle = handle,
                WindowTitle = title,
                ProcessName = processName,
                Timestamp = DateTime.UtcNow.ToString("o")
            };

            InboxStore.Save(record);
        }
        catch
        {
            // A hook must never disrupt the Claude Code session — swallow errors.
        }

        return 0;
    }

    private static string ProjectNameFrom(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return "";
        try
        {
            string trimmed = cwd.TrimEnd('/', '\\');
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
        catch
        {
            return cwd;
        }
    }

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? ""
            : "";

    private static bool HasString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String;
}
