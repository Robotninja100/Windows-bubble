using System.IO;
using CursorBubble.Config;
using CursorBubble.Stats;
using Xunit;
// WPF's implicit usings bring in System.Windows.Shapes.Path, so plain "Path"
// would be ambiguous in this project.
using Path = System.IO.Path;

namespace CursorBubble.Tests;

/// <summary>
/// The counters behind the Overview page. All pure: every method takes the time
/// rather than reading the clock, which is what makes a fortnight of use a
/// millisecond of test.
/// </summary>
public class UsageStatsTests
{
    private static readonly DateTime Monday = new(2026, 3, 2, 9, 0, 0, DateTimeKind.Local);

    [Fact]
    public void An_action_is_counted_by_segment_and_by_kind()
    {
        var stats = new UsageStats();

        stats.RecordAction("Calculator", ActionType.LaunchProgram, Monday);
        stats.RecordAction("Calculator", ActionType.LaunchProgram, Monday);
        stats.RecordAction("Open\nBrowser", ActionType.OpenPath, Monday);

        Assert.Equal(3, stats.ActionsRun);
        Assert.Equal(2, stats.PerSegment["Calculator"]);
        Assert.Equal(2, stats.PerAction[nameof(ActionType.LaunchProgram)]);
        Assert.Equal(1, stats.PerAction[nameof(ActionType.OpenPath)]);
    }

    [Fact]
    public void A_two_line_label_is_counted_on_one_line()
    {
        // Labels are laid out on a ring, so they carry newlines. A ranking that
        // showed them would break its own rows in half.
        var stats = new UsageStats();

        stats.RecordAction("Open\nDocuments", ActionType.OpenPath, Monday);

        Assert.Equal("Open Documents", stats.TopSegments(1)[0].Label);
    }

    [Fact]
    public void A_segment_with_no_name_still_has_somewhere_to_be_counted()
    {
        var stats = new UsageStats();

        stats.RecordAction("   ", ActionType.OpenPath, Monday);

        Assert.Equal(UsageStats.UnnamedSegment, stats.TopSegments(1)[0].Label);
    }

    [Fact]
    public void Opens_are_split_by_how_they_were_opened()
    {
        var stats = new UsageStats();

        stats.RecordOpen(OpenSource.Mouse, Monday);
        stats.RecordOpen(OpenSource.Hotkey, Monday);
        stats.RecordOpen(OpenSource.Hotkey, Monday);

        Assert.Equal(3, stats.MenuOpens);
        Assert.Equal(1, stats.MouseOpens);
        Assert.Equal(2, stats.HotkeyOpens);
    }

    [Fact]
    public void The_cancel_rate_is_zero_rather_than_undefined_on_a_fresh_install()
    {
        var stats = new UsageStats();

        Assert.Equal(0d, stats.CancelRate);
        Assert.Equal(0d, stats.ActionsPerActiveDay);
        Assert.True(stats.IsEmpty);
        Assert.Null(stats.FavouriteSegment);
        Assert.Empty(stats.TopSegments(5));
    }

    [Fact]
    public void Consecutive_days_make_a_streak_and_a_gap_ends_it()
    {
        var stats = new UsageStats();

        stats.RecordOpen(OpenSource.Mouse, Monday);
        stats.RecordOpen(OpenSource.Mouse, Monday.AddDays(1));
        stats.RecordOpen(OpenSource.Mouse, Monday.AddDays(2));

        Assert.Equal(3, stats.CurrentStreakDays(Monday.AddDays(2)));
        Assert.Equal(3, stats.DaysUsed);

        // Two days of silence: the streak is over, but the record of it is not.
        Assert.Equal(0, stats.CurrentStreakDays(Monday.AddDays(5)));
        Assert.Equal(3, stats.LongestStreakDays);
    }

    [Fact]
    public void A_streak_that_reached_yesterday_survives_until_the_day_is_over()
    {
        // Otherwise a counter resets at midnight and tells someone their streak
        // broke while they were asleep.
        var stats = new UsageStats();

        stats.RecordOpen(OpenSource.Mouse, Monday);
        stats.RecordOpen(OpenSource.Mouse, Monday.AddDays(1));

        Assert.Equal(2, stats.CurrentStreakDays(Monday.AddDays(2)));
    }

    [Fact]
    public void Using_it_twice_in_a_day_is_still_one_day()
    {
        var stats = new UsageStats();

        stats.RecordOpen(OpenSource.Mouse, Monday);
        stats.RecordOpen(OpenSource.Mouse, Monday.AddHours(6));
        stats.RecordAction("Calculator", ActionType.LaunchProgram, Monday.AddHours(7));

        Assert.Single(stats.ActiveDays);
        Assert.Equal(1, stats.DaysUsed);
    }

    [Fact]
    public void The_day_list_stops_growing()
    {
        // It is read on every page draw and written on every open; a year of
        // days is plenty and an unbounded list is a file that only grows.
        var stats = new UsageStats();

        for (int day = 0; day < UsageStats.MaxActiveDays + 50; day++)
            stats.RecordOpen(OpenSource.Mouse, Monday.AddDays(day));

        Assert.Equal(UsageStats.MaxActiveDays, stats.ActiveDays.Count);
    }

    [Fact]
    public void A_clock_that_went_backwards_does_not_write_the_same_day_twice()
    {
        var stats = new UsageStats();

        stats.RecordOpen(OpenSource.Mouse, Monday);
        stats.RecordOpen(OpenSource.Mouse, Monday.AddDays(1));
        stats.RecordOpen(OpenSource.Mouse, Monday);

        Assert.Equal(2, stats.ActiveDays.Count);
    }

    [Fact]
    public void The_ranking_is_busiest_first_and_shares_add_up()
    {
        var stats = new UsageStats();

        for (int i = 0; i < 6; i++)
            stats.RecordAction("Browser", ActionType.OpenPath, Monday);
        for (int i = 0; i < 3; i++)
            stats.RecordAction("Terminal", ActionType.LaunchProgram, Monday);
        stats.RecordAction("Notes", ActionType.OpenPath, Monday);

        IReadOnlyList<SegmentUse> top = stats.TopSegments(2);

        Assert.Equal(2, top.Count);
        Assert.Equal("Browser", top[0].Label);
        Assert.Equal(6, top[0].Count);
        Assert.Equal(0.6, top[0].Share, 3);
        Assert.Equal("Terminal", top[1].Label);
        Assert.Equal("Browser", stats.FavouriteSegment!.Label);
    }

    [Fact]
    public void Equally_used_segments_keep_a_stable_order()
    {
        // Otherwise the chart reshuffles itself every time the page is opened.
        var stats = new UsageStats();
        stats.RecordAction("Beta", ActionType.OpenPath, Monday);
        stats.RecordAction("Alpha", ActionType.OpenPath, Monday);

        Assert.Equal("Alpha", stats.TopSegments(2)[0].Label);
    }

    [Fact]
    public void Time_saved_follows_the_number_of_actions()
    {
        var stats = new UsageStats();
        for (int i = 0; i < 10; i++)
            stats.RecordAction("Browser", ActionType.OpenPath, Monday);

        Assert.Equal(10d * UsageStats.SecondsSavedPerAction, stats.EstimatedTimeSaved.TotalSeconds);
    }
}

/// <summary>
/// The sentences on the Overview page. They divide by counters, so the case
/// worth pinning down is the one where the counters are all zero.
/// </summary>
public class UsageFactsTests
{
    private static readonly DateTime Monday = new(2026, 3, 2, 9, 0, 0, DateTimeKind.Local);

    [Fact]
    public void A_fresh_install_gets_an_invitation_rather_than_a_row_of_zeroes()
    {
        IReadOnlyList<UsageFact> facts =
            UsageFacts.For(new UsageStats(), AppConfig.CreateDefault(), Monday);

        Assert.NotEmpty(facts);
        Assert.All(facts, fact => Assert.False(string.IsNullOrWhiteSpace(fact.Text)));
        Assert.Contains(facts, fact => fact.Text.Contains("Nothing recorded yet", StringComparison.Ordinal));
    }

    [Fact]
    public void The_favourite_and_the_streak_are_worth_a_sentence_each()
    {
        var stats = new UsageStats();
        stats.RecordOpen(OpenSource.Mouse, Monday);
        stats.RecordAction("Browser", ActionType.OpenPath, Monday);
        stats.RecordOpen(OpenSource.Hotkey, Monday.AddDays(1));
        stats.RecordAction("Browser", ActionType.OpenPath, Monday.AddDays(1));

        IReadOnlyList<UsageFact> facts =
            UsageFacts.For(stats, AppConfig.CreateDefault(), Monday.AddDays(1));

        Assert.Contains(facts, fact => fact.Text.Contains("Browser", StringComparison.Ordinal));
        Assert.Contains(facts, fact => fact.Text.Contains("2-day streak", StringComparison.Ordinal));
        Assert.DoesNotContain(facts, fact => fact.Text.Contains("Nothing recorded yet", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_fact_carries_an_icon()
    {
        var stats = new UsageStats();
        stats.RecordAction("Browser", ActionType.OpenPath, Monday);

        IReadOnlyList<UsageFact> facts = UsageFacts.For(stats, AppConfig.CreateDefault(), Monday);

        Assert.All(facts, fact => Assert.False(string.IsNullOrEmpty(fact.Glyph)));
    }

    [Fact]
    public void The_total_number_of_settings_is_the_sum_of_the_groups()
    {
        int summed = 0;
        foreach (TuningArea area in Tunables.Areas)
            summed += area.SettingCount;

        Assert.Equal(summed, Tunables.TotalSettings);
        Assert.NotEmpty(Tunables.Areas);
    }
}

/// <summary>
/// Reading and writing usage.json. Runs against a temp directory, never the real
/// %APPDATA% one.
/// </summary>
public sealed class UsageStatsStoreTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 3, 2, 9, 0, 0, DateTimeKind.Local);

    private readonly string _originalDir;
    private readonly bool _originalEnabled;

    public UsageStatsStoreTests()
    {
        _originalDir = UsageStatsStore.Dir;
        _originalEnabled = UsageStatsStore.Enabled;
        UsageStatsStore.Dir = Path.Combine(Path.GetTempPath(), "cursorbubble-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(UsageStatsStore.Dir);
        UsageStatsStore.InvalidateCache();
    }

    public void Dispose()
    {
        string dir = UsageStatsStore.Dir;
        UsageStatsStore.Dir = _originalDir;
        UsageStatsStore.Enabled = _originalEnabled;
        UsageStatsStore.InvalidateCache();
        try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void A_round_trip_keeps_every_counter()
    {
        var stats = new UsageStats();
        stats.RecordOpen(OpenSource.Hotkey, Monday);
        stats.RecordAction("Open\nBrowser", ActionType.OpenPath, Monday);
        stats.RecordCancelled(Monday.AddDays(1));
        stats.RecordAiScript(Monday.AddDays(1));

        UsageStats? back = UsageStatsStore.Deserialize(UsageStatsStore.Serialize(stats));

        Assert.NotNull(back);
        Assert.Equal(1, back!.MenuOpens);
        Assert.Equal(1, back.HotkeyOpens);
        Assert.Equal(1, back.ActionsRun);
        Assert.Equal(1, back.Cancelled);
        Assert.Equal(1, back.AiScriptsGenerated);
        Assert.Equal(2, back.ActiveDays.Count);
        Assert.Equal(1, back.PerSegment["Open Browser"]);
        Assert.Equal(stats.FirstUsed, back.FirstUsed);
    }

    [Fact]
    public void A_hand_written_file_missing_half_its_fields_still_loads()
    {
        UsageStats? stats = UsageStatsStore.Deserialize("""{ "MenuOpens": 4 }""");

        Assert.NotNull(stats);
        Assert.Equal(4, stats!.MenuOpens);
        // The collections are what every method here assumes exists.
        Assert.NotNull(stats.PerSegment);
        Assert.NotNull(stats.ActiveDays);
        Assert.Equal(0, stats.CurrentStreakDays(Monday));
    }

    [Fact]
    public void Recording_reaches_the_file()
    {
        UsageStatsStore.Enabled = true;

        UsageStatsStore.Record(s => s.RecordOpen(OpenSource.Mouse, Monday));
        UsageStatsStore.Flush();

        Assert.True(File.Exists(UsageStatsStore.StatsPath));

        UsageStats? written = UsageStatsStore.Deserialize(File.ReadAllText(UsageStatsStore.StatsPath));
        Assert.Equal(1, written!.MenuOpens);
    }

    [Fact]
    public void Nothing_is_counted_while_counting_is_off()
    {
        UsageStatsStore.Enabled = false;

        UsageStatsStore.Record(s => s.RecordOpen(OpenSource.Mouse, Monday));
        UsageStatsStore.Flush();

        Assert.Equal(0, UsageStatsStore.Current.MenuOpens);
        Assert.False(File.Exists(UsageStatsStore.StatsPath));
    }

    [Fact]
    public void Resetting_clears_the_counters_and_the_file()
    {
        UsageStatsStore.Enabled = true;
        UsageStatsStore.Record(s => s.RecordAction("Browser", ActionType.OpenPath, Monday));

        UsageStatsStore.Reset();
        UsageStatsStore.Flush();

        Assert.True(UsageStatsStore.Current.IsEmpty);

        UsageStats? written = UsageStatsStore.Deserialize(File.ReadAllText(UsageStatsStore.StatsPath));
        Assert.Equal(0, written!.ActionsRun);
        Assert.Empty(written.PerSegment);
    }
}
