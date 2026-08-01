using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CursorBubble.Storage;

namespace CursorBubble.ClaudeCode;

/// <summary>
/// Stores pending Claude Code sessions as one JSON file per session under
/// <c>%APPDATA%\CursorBubble\inbox\</c>. The hook process writes here; the app
/// reads/removes.
/// </summary>
public static class InboxStore
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CursorBubble", "inbox");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Write (or overwrite) the record for its session.</summary>
    public static void Save(InboxRecord record)
    {
        Directory.CreateDirectory(Dir);
        AtomicFile.WriteAllText(PathFor(record.SessionId), JsonSerializer.Serialize(record, Options));
    }

    /// <summary>
    /// Read a single record, retrying briefly. Returns null if unreadable.
    ///
    /// The retry no longer exists to skip past a partially written file — since
    /// <see cref="Save"/> became atomic a reader sees either the whole previous
    /// record or the whole new one. What remains is the sharing violation that a
    /// reader can still hit during the instant the writer replaces the file, and
    /// the watcher firing on the temporary file's own move. Both clear in
    /// milliseconds. Call this off the UI thread.
    /// </summary>
    public static InboxRecord? TryLoad(string path, int attempts = 6)
    {
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                string text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    InboxRecord? r = JsonSerializer.Deserialize<InboxRecord>(text, Options);
                    if (r is not null && !string.IsNullOrEmpty(r.SessionId))
                        return r;
                }
            }
            catch
            {
                // still being written / locked — retry
            }

            Thread.Sleep(60);
        }
        return null;
    }

    /// <summary>All pending records, newest first. Corrupt files are skipped.</summary>
    public static List<InboxRecord> LoadAll()
    {
        var list = new List<InboxRecord>();
        if (!Directory.Exists(Dir))
            return list;

        foreach (string file in Directory.EnumerateFiles(Dir, "*.json"))
        {
            try
            {
                InboxRecord? r = JsonSerializer.Deserialize<InboxRecord>(File.ReadAllText(file), Options);
                if (r is not null && !string.IsNullOrEmpty(r.SessionId))
                    list.Add(r);
            }
            catch
            {
                // ignore unreadable/partial files
            }
        }

        list.Sort((a, b) => string.CompareOrdinal(b.Timestamp, a.Timestamp));
        return list;
    }

    public static int UnansweredCount()
    {
        if (!Directory.Exists(Dir))
            return 0;
        try
        {
            return Directory.EnumerateFiles(Dir, "*.json").Count();
        }
        catch
        {
            return 0;
        }
    }

    public static void Remove(string sessionId)
    {
        try
        {
            string path = PathFor(sessionId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static string PathFor(string sessionId)
    {
        string safe = string.Concat(sessionId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        if (string.IsNullOrEmpty(safe))
            safe = "session";
        return Path.Combine(Dir, safe + ".json");
    }
}
