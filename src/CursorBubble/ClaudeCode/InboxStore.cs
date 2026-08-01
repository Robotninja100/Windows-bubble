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
    private static readonly string DefaultDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CursorBubble", "inbox");

    /// <summary>
    /// Where the pending-session records live. Settable only from the tests, so
    /// counting and pruning can be exercised against a temp directory instead of
    /// the developer's real inbox.
    /// </summary>
    public static string Dir { get; internal set; } = DefaultDir;

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

    /// <summary>
    /// How many pending sessions the badge should show.
    ///
    /// Counts records that actually load, not files on disk: <see cref="LoadAll"/>
    /// skips ones that fail to parse, so counting files made the badge read
    /// higher than the list the responder then showed. The inbox holds one small
    /// file per pending session and <see cref="Prune"/> keeps it that way, so
    /// reading them is cheap enough for the bubble-open path.
    /// </summary>
    public static int UnansweredCount() => LoadAll().Count;

    /// <summary>
    /// Delete records older than <paramref name="maxAgeDays"/>.
    ///
    /// Nothing else ever removes a session that the user did not answer or
    /// dismiss, so without this the directory only grows — and every entry in it
    /// is read on each bubble open. Mirrors <c>Log.Prune</c>; never throws.
    /// </summary>
    public static void Prune(int maxAgeDays = 14)
    {
        if (!Directory.Exists(Dir))
            return;

        DateTime cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);

        try
        {
            foreach (string file in Directory.EnumerateFiles(Dir, "*.json"))
            {
                try
                {
                    // Write time rather than the record's own timestamp: a file
                    // too corrupt to parse is exactly the one worth clearing out.
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch
                {
                    // in use, or gone already — leave it for next time
                }
            }
        }
        catch
        {
            // the directory itself became unreadable; nothing sensible to do
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
