using System.IO;
using System.Text.Json;
using CursorBubble.Diagnostics;
using CursorBubble.Storage;

namespace CursorBubble.Stats;

/// <summary>
/// Keeps <see cref="UsageStats"/> in <c>%APPDATA%\CursorBubble\usage.json</c>,
/// beside the configuration.
///
/// Two rules shape this class. It never throws — a counter is not worth failing
/// an action over, so every path here degrades to "the number is a bit stale".
/// And it never writes on the caller's thread: recording happens on the bubble's
/// open path, which is the one place in this app where a few milliseconds of
/// disk latency would actually be felt. Writes are queued in order behind one
/// another instead, and <see cref="Flush"/> waits for them at shutdown.
///
/// A separate file from config.json on purpose: settings are edited by hand and
/// backed up, counters are written constantly and are worthless to keep. Losing
/// this file costs nothing but the numbers.
/// </summary>
public static class UsageStatsStore
{
    private static readonly string DefaultDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CursorBubble");

    /// <summary>
    /// Where <c>usage.json</c> lives. Settable only from the tests, so recording
    /// can be exercised against a temp directory instead of the developer's own
    /// statistics.
    /// </summary>
    public static string Dir { get; internal set; } = DefaultDir;

    /// <summary>Full path of the statistics file.</summary>
    public static string StatsPath => Path.Combine(Dir, "usage.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Guards <see cref="_current"/> and the ordering of queued writes.</summary>
    private static readonly object Gate = new();

    private static UsageStats? _current;

    /// <summary>
    /// Queued writes, chained so they land in the order they were recorded. Two
    /// independent background writes could otherwise finish out of order and
    /// leave the older snapshot on disk.
    /// </summary>
    private static Task _writes = Task.CompletedTask;

    /// <summary>
    /// Whether anything is counted at all. Mirrors
    /// <see cref="Config.AppConfig.CollectUsageStats"/>; the app sets it at
    /// startup and whenever settings are saved.
    ///
    /// Turning it off stops recording but keeps what is already there — the
    /// Overview page still shows it, and About can clear it. Deleting someone's
    /// history because they unticked a box is not what the box says.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// The statistics as they stand, loaded on first use. Never null, even when
    /// the file is missing or unreadable.
    /// </summary>
    public static UsageStats Current
    {
        get
        {
            lock (Gate)
                return _current ??= LoadOrEmpty();
        }
    }

    /// <summary>
    /// Apply <paramref name="change"/> and persist the result in the background.
    /// Does nothing when recording is off. Never throws.
    /// </summary>
    public static void Record(Action<UsageStats> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (!Enabled)
            return;

        try
        {
            lock (Gate)
            {
                UsageStats stats = _current ??= LoadOrEmpty();
                change(stats);

                // Serialised inside the lock so the snapshot matches the state
                // that was just counted, then handed to the queue as text — the
                // writer never touches the live object.
                Queue(JsonSerializer.Serialize(stats, Options));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not record usage statistics.", ex);
        }
    }

    /// <summary>
    /// Wait for queued writes to reach disk. Called on exit; safe to call when
    /// there is nothing outstanding.
    /// </summary>
    public static void Flush()
    {
        Task pending;
        lock (Gate)
            pending = _writes;

        try
        {
            // A bounded wait: shutdown must not hang on a locked file, and the
            // worst case of giving up is one lost increment.
            if (!pending.Wait(TimeSpan.FromSeconds(2)))
                Log.Warn("Gave up waiting for the usage statistics to be written.");
        }
        catch (Exception ex)
        {
            Log.Warn("Usage statistics may not have been written.", ex);
        }
    }

    /// <summary>
    /// Throw everything away and start counting again from zero. Used by the
    /// About page, where it is behind a confirmation.
    /// </summary>
    public static void Reset()
    {
        var empty = new UsageStats();

        lock (Gate)
        {
            _current = empty;
            Queue(JsonSerializer.Serialize(empty, Options));
        }
    }

    /// <summary>
    /// Add a write to the back of the queue. Call with <see cref="Gate"/> held:
    /// the chaining is what keeps the writes in order.
    ///
    /// The destination is resolved here rather than in the writer, so a queued
    /// snapshot always lands where it belonged when it was taken.
    /// </summary>
    private static void Queue(string json)
    {
        string path = StatsPath;
        _writes = _writes.ContinueWith(_ => Persist(path, json),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>
    /// Read the file, or hand back empty statistics if there is nothing readable
    /// there. A corrupt usage file is not worth a message: unlike config.json,
    /// nothing in it can be recreated by hand and nothing is lost that the user
    /// put there.
    /// </summary>
    private static UsageStats LoadOrEmpty()
    {
        try
        {
            if (File.Exists(StatsPath))
            {
                UsageStats? stats = Deserialize(File.ReadAllText(StatsPath));
                if (stats is not null)
                    return stats;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the usage statistics; starting a fresh count.", ex);
        }

        return new UsageStats();
    }

    /// <summary>Internal for tests: parse and normalise, tolerating an older or hand-edited file.</summary>
    internal static UsageStats? Deserialize(string json)
    {
        UsageStats? stats = JsonSerializer.Deserialize<UsageStats>(json, Options);
        if (stats is null)
            return null;

        // A file written by hand — or by a future build — can leave these null,
        // and every method here assumes they exist.
        stats.PerSegment ??= new Dictionary<string, int>();
        stats.PerAction ??= new Dictionary<string, int>();
        stats.ActiveDays ??= new List<string>();

        return stats;
    }

    internal static string Serialize(UsageStats stats) => JsonSerializer.Serialize(stats, Options);

    /// <summary>Forget the cached statistics so the next read comes from disk. Tests only.</summary>
    internal static void InvalidateCache()
    {
        lock (Gate)
            _current = null;
    }

    private static void Persist(string path, string json)
    {
        try
        {
            AtomicFile.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not save the usage statistics.", ex);
        }
    }
}
