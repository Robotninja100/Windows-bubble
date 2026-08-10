using System.Globalization;
using CursorBubble.Config;

namespace CursorBubble.Stats;

/// <summary>How a bubble came to be on screen.</summary>
public enum OpenSource
{
    /// <summary>The right-then-left mouse gesture.</summary>
    Mouse,

    /// <summary>The global keyboard shortcut.</summary>
    Hotkey
}

/// <summary>One segment and how often it has been run.</summary>
/// <param name="Label">The segment's name, on one line.</param>
/// <param name="Count">Times it has been run.</param>
/// <param name="Share">Its part of every action run, 0..1.</param>
public sealed record SegmentUse(string Label, int Count, double Share);

/// <summary>
/// Plain counters describing how CursorBubble is used, persisted next to the
/// configuration as <c>usage.json</c>.
///
/// Nothing here leaves the machine — <see cref="UsageStatsStore"/> writes a file
/// in <c>%APPDATA%</c> and that is the whole of it. It exists so the Overview
/// page can say something true about the app: which shortcut earns its place on
/// the ring, whether the keyboard or the gesture is doing the work, how long the
/// habit has held. A counter nobody can see is just overhead, so every field
/// here is shown somewhere.
///
/// The type is deliberately dumb data plus pure methods: every method takes the
/// current time rather than reading the clock, which is what makes a streak
/// across a week testable in a millisecond.
/// </summary>
public sealed class UsageStats
{
    /// <summary>The layout this build writes. Mirrors <see cref="AppConfig.SchemaVersion"/>.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// How many active days are kept. Roughly a year, which is more than any
    /// streak needs and still a file measured in kilobytes.
    /// </summary>
    internal const int MaxActiveDays = 400;

    /// <summary>
    /// Seconds an action is assumed to save over finding the same thing by hand
    /// (start menu, taskbar, or a folder three levels down).
    ///
    /// A guess, and presented as one wherever it is shown. It is deliberately
    /// conservative: the number is there to be a pleasant fact, not a claim.
    /// </summary>
    internal const int SecondsSavedPerAction = 6;

    /// <summary>Name shown for a segment whose label is blank.</summary>
    internal const string UnnamedSegment = "(no name)";

    private const string DayFormat = "yyyy-MM-dd";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Local timestamp of the first recorded use, round-trip format. Null until something happens.</summary>
    public string? FirstUsed { get; set; }

    /// <summary>Local timestamp of the most recent recorded use, round-trip format.</summary>
    public string? LastUsed { get; set; }

    /// <summary>Times the bubble has been opened, by any means.</summary>
    public int MenuOpens { get; set; }

    /// <summary>Of those, how many came from the mouse gesture.</summary>
    public int MouseOpens { get; set; }

    /// <summary>Of those, how many came from the keyboard shortcut.</summary>
    public int HotkeyOpens { get; set; }

    /// <summary>Actions actually launched from a segment.</summary>
    public int ActionsRun { get; set; }

    /// <summary>Opens that ended in the centre of the ring without running anything.</summary>
    public int Cancelled { get; set; }

    /// <summary>Scripts written by the AI generator and saved to a segment.</summary>
    public int AiScriptsGenerated { get; set; }

    /// <summary>Times this settings window has been opened.</summary>
    public int SettingsOpened { get; set; }

    /// <summary>Longest run of consecutive active days ever reached.</summary>
    public int LongestStreakDays { get; set; }

    /// <summary>Runs per segment label. The label is what the user sees, so it is what is counted.</summary>
    public Dictionary<string, int> PerSegment { get; set; } = new();

    /// <summary>Runs per <see cref="ActionType"/>, stored by name so the file stays readable.</summary>
    public Dictionary<string, int> PerAction { get; set; } = new();

    /// <summary>Days (local, <c>yyyy-MM-dd</c>) on which the app was used, oldest first.</summary>
    public List<string> ActiveDays { get; set; } = new();

    // ---- recording ----------------------------------------------------------

    /// <summary>Count a bubble open.</summary>
    public void RecordOpen(OpenSource source, DateTime nowLocal)
    {
        MenuOpens++;
        if (source == OpenSource.Hotkey)
            HotkeyOpens++;
        else
            MouseOpens++;

        Touch(nowLocal);
    }

    /// <summary>Count an action that was actually launched.</summary>
    public void RecordAction(string? label, ActionType action, DateTime nowLocal)
    {
        ActionsRun++;
        Bump(PerSegment, DisplayLabel(label));
        Bump(PerAction, action.ToString());
        Touch(nowLocal);
    }

    /// <summary>Count an open that was released in the dead zone.</summary>
    public void RecordCancelled(DateTime nowLocal)
    {
        Cancelled++;
        Touch(nowLocal);
    }

    /// <summary>Count a script the AI generator wrote and stored.</summary>
    public void RecordAiScript(DateTime nowLocal)
    {
        AiScriptsGenerated++;
        Touch(nowLocal);
    }

    /// <summary>Count an opening of the settings window.</summary>
    public void RecordSettingsOpened(DateTime nowLocal)
    {
        SettingsOpened++;
        Touch(nowLocal);
    }

    /// <summary>
    /// Stamp the first/last use and mark today as active.
    ///
    /// The longest streak is refreshed here rather than computed on demand: it is
    /// the one figure that cannot be recovered from the file once the days it was
    /// made of have aged out of <see cref="ActiveDays"/>.
    /// </summary>
    private void Touch(DateTime nowLocal)
    {
        string stamp = nowLocal.ToString("o", CultureInfo.InvariantCulture);
        FirstUsed ??= stamp;
        LastUsed = stamp;

        string today = Day(nowLocal);
        // Contains rather than "is it the last one": a clock that moved
        // backwards, or a config carried over from another machine, would
        // otherwise write the same day in twice.
        if (!ActiveDays.Contains(today))
        {
            ActiveDays.Add(today);
            if (ActiveDays.Count > MaxActiveDays)
                ActiveDays.RemoveRange(0, ActiveDays.Count - MaxActiveDays);
        }

        LongestStreakDays = Math.Max(LongestStreakDays, CurrentStreakDays(nowLocal));
    }

    // ---- derived ------------------------------------------------------------

    /// <summary>True when nothing has been recorded yet.</summary>
    public bool IsEmpty => MenuOpens == 0 && ActionsRun == 0 && Cancelled == 0;

    /// <summary>Distinct days the app has been used on, within what is still kept.</summary>
    public int DaysUsed => new HashSet<string>(ActiveDays, StringComparer.Ordinal).Count;

    /// <summary>The first recorded use as a local date, or null if there is none.</summary>
    public DateTime? FirstUsedOn => ParseStamp(FirstUsed);

    /// <summary>The most recent recorded use as a local date, or null if there is none.</summary>
    public DateTime? LastUsedOn => ParseStamp(LastUsed);

    /// <summary>Whole days since the first recorded use; 0 when that was today or never.</summary>
    public int DaysSinceFirstUse(DateTime nowLocal) =>
        FirstUsedOn is DateTime first ? Math.Max(0, (nowLocal.Date - first.Date).Days) : 0;

    /// <summary>
    /// Consecutive days up to today on which the app was used.
    ///
    /// A streak that reaches yesterday but not yet today still counts: the day is
    /// not over, and a counter that resets at midnight would tell someone their
    /// streak had broken while they were asleep.
    /// </summary>
    public int CurrentStreakDays(DateTime nowLocal)
    {
        var days = new HashSet<string>(ActiveDays, StringComparer.Ordinal);

        DateTime cursor = nowLocal.Date;
        if (!days.Contains(Day(cursor)))
        {
            cursor = cursor.AddDays(-1);
            if (!days.Contains(Day(cursor)))
                return 0;
        }

        int streak = 0;
        while (days.Contains(Day(cursor)))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }
        return streak;
    }

    /// <summary>Actions per day, counted over the days the app was actually used.</summary>
    public double ActionsPerActiveDay
    {
        get
        {
            int days = DaysUsed;
            return days == 0 ? 0 : (double)ActionsRun / days;
        }
    }

    /// <summary>
    /// Share of opens that ended without running anything, 0..1. Zero when the
    /// bubble has never been opened, rather than an undefined ratio.
    /// </summary>
    public double CancelRate => MenuOpens == 0 ? 0 : (double)Cancelled / MenuOpens;

    /// <summary>A cheerful estimate; see <see cref="SecondsSavedPerAction"/>.</summary>
    public TimeSpan EstimatedTimeSaved => TimeSpan.FromSeconds((long)ActionsRun * SecondsSavedPerAction);

    /// <summary>
    /// The most-run segments, busiest first, with each one's share of every
    /// action run. Ties are broken by label so the chart does not reshuffle
    /// itself between two equal entries every time it is drawn.
    /// </summary>
    public IReadOnlyList<SegmentUse> TopSegments(int max)
    {
        if (max <= 0 || PerSegment.Count == 0)
            return Array.Empty<SegmentUse>();

        int total = 0;
        foreach (int count in PerSegment.Values)
            total += count;

        List<KeyValuePair<string, int>> ordered = new(PerSegment);
        ordered.Sort((a, b) =>
        {
            int byCount = b.Value.CompareTo(a.Value);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.Key, b.Key);
        });

        var result = new List<SegmentUse>(Math.Min(max, ordered.Count));
        foreach (KeyValuePair<string, int> entry in ordered)
        {
            if (result.Count == max)
                break;
            result.Add(new SegmentUse(entry.Key, entry.Value, total == 0 ? 0 : (double)entry.Value / total));
        }
        return result;
    }

    /// <summary>The single busiest segment, or null when nothing has been run.</summary>
    public SegmentUse? FavouriteSegment
    {
        get
        {
            IReadOnlyList<SegmentUse> top = TopSegments(1);
            return top.Count == 0 ? null : top[0];
        }
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    /// A segment label as one line of text.
    ///
    /// Labels carry newlines on purpose — they are laid out on a ring — and a
    /// two-line entry in a list or a fact sentence reads as a mistake.
    /// </summary>
    internal static string DisplayLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return UnnamedSegment;

        string flat = label.Replace('\n', ' ').Replace('\r', ' ').Trim();
        while (flat.Contains("  ", StringComparison.Ordinal))
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);

        return flat.Length == 0 ? UnnamedSegment : flat;
    }

    private static string Day(DateTime local) => local.ToString(DayFormat, CultureInfo.InvariantCulture);

    private static void Bump(Dictionary<string, int> counters, string key)
    {
        counters.TryGetValue(key, out int count);
        counters[key] = count + 1;
    }

    private static DateTime? ParseStamp(string? stamp)
    {
        if (string.IsNullOrWhiteSpace(stamp))
            return null;

        return DateTime.TryParse(stamp, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTime value)
            ? value
            : null;
    }
}
