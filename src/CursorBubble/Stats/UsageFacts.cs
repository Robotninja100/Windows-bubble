using System.Globalization;
using CursorBubble.Config;
using CursorBubble.Controls;

namespace CursorBubble.Stats;

/// <summary>
/// One line for the Overview page: an icon and a sentence.
/// </summary>
/// <param name="Glyph">A Segoe Fluent Icons codepoint, decorative only.</param>
/// <param name="Text">The sentence itself.</param>
public sealed record UsageFact(string Glyph, string Text);

/// <summary>
/// Turns <see cref="UsageStats"/> into the sentences shown on the Overview page.
///
/// Separated from the window for one reason: a fact that divides by a counter is
/// a crash on a fresh install, and this is the shape that can be tested without
/// a message pump. Every sentence here is skipped when the number behind it does
/// not exist yet, so the list simply grows as the app gets used rather than
/// showing a row of zeroes on day one.
/// </summary>
public static class UsageFacts
{
    /// <summary>
    /// Build the facts worth showing, most interesting first.
    /// </summary>
    /// <param name="stats">What has been recorded.</param>
    /// <param name="config">The configuration being edited, for the "what you have set up" lines.</param>
    /// <param name="nowLocal">The current local time, passed in so this is testable.</param>
    public static IReadOnlyList<UsageFact> For(UsageStats stats, AppConfig config, DateTime nowLocal)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(config);

        var facts = new List<UsageFact>();

        if (stats.IsEmpty)
        {
            facts.Add(new UsageFact(Glyphs.Lightbulb,
                "Nothing recorded yet. Hold the right mouse button, click left, and this page " +
                "starts filling itself in."));
            facts.Add(new UsageFact(Glyphs.Settings,
                $"You have {config.Segments.Count} segments on the ring and " +
                $"{Tunables.TotalSettings} things you can change in here."));
            return facts;
        }

        if (stats.MenuOpens > 0)
        {
            facts.Add(new UsageFact(Glyphs.View,
                $"The bubble has been opened {Count(stats.MenuOpens, "time")} — " +
                $"{stats.MouseOpens:N0} with the mouse gesture and {stats.HotkeyOpens:N0} with the shortcut."));
        }

        if (stats.FavouriteSegment is SegmentUse favourite)
        {
            facts.Add(new UsageFact(Glyphs.Star,
                $"“{favourite.Label}” is your favourite: {Count(favourite.Count, "run")}, " +
                $"{Percent(favourite.Share)} of everything you have launched."));
        }

        int streak = stats.CurrentStreakDays(nowLocal);
        if (streak > 1)
        {
            facts.Add(new UsageFact(Glyphs.Calendar,
                $"You are on a {streak}-day streak — your longest so far is {stats.LongestStreakDays} days."));
        }

        if (stats.ActionsRun > 0)
        {
            facts.Add(new UsageFact(Glyphs.Stopwatch,
                $"That is roughly {Duration(stats.EstimatedTimeSaved)} not spent hunting through menus, " +
                $"at a generous {UsageStats.SecondsSavedPerAction} seconds a shortcut."));
        }

        if (stats.MenuOpens >= 10 && stats.Cancelled > 0)
        {
            facts.Add(new UsageFact(Glyphs.Cancel,
                $"{Percent(stats.CancelRate)} of your opens end in the middle of the ring. " +
                "That is the cancel zone doing its job."));
        }

        if (stats.DaysUsed > 1)
        {
            facts.Add(new UsageFact(Glyphs.Play,
                $"Used on {Count(stats.DaysUsed, "day")}, averaging " +
                $"{stats.ActionsPerActiveDay.ToString("0.#", CultureInfo.CurrentCulture)} actions on each of them."));
        }

        if (stats.AiScriptsGenerated > 0)
        {
            facts.Add(new UsageFact(Glyphs.Lightbulb,
                $"Claude has written {Count(stats.AiScriptsGenerated, "script")} for you, straight onto a segment."));
        }

        int age = stats.DaysSinceFirstUse(nowLocal);
        if (age > 0 && stats.FirstUsedOn is DateTime first)
        {
            facts.Add(new UsageFact(Glyphs.Calendar,
                $"Counting since {first.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}, " +
                $"which is {Count(age, "day")} ago."));
        }

        return facts;
    }

    /// <summary>“1 run” / “14 runs”, with thousands separated.</summary>
    internal static string Count(int value, string noun) =>
        value == 1 ? $"1 {noun}" : $"{value:N0} {noun}s";

    /// <summary>A share of a whole, as a rounded percentage.</summary>
    internal static string Percent(double share) =>
        share.ToString("P0", CultureInfo.CurrentCulture);

    /// <summary>
    /// A duration in the largest unit that still says something. "0.03 hours" is
    /// not a fun fact; "two minutes" is.
    /// </summary>
    internal static string Duration(TimeSpan span)
    {
        if (span.TotalMinutes < 1)
            return Count((int)Math.Round(span.TotalSeconds), "second");
        if (span.TotalHours < 1)
            return Count((int)Math.Round(span.TotalMinutes), "minute");
        return $"{span.TotalHours.ToString("0.#", CultureInfo.CurrentCulture)} hours";
    }
}
