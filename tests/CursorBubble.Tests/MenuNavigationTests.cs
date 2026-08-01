using CursorBubble.Overlay;
using Xunit;

namespace CursorBubble.Tests;

public class StepSelectionTests
{
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 2)]
    [InlineData(5, 1, 0)]   // wraps past the end
    [InlineData(0, -1, 5)]  // wraps past the start
    [InlineData(5, -1, 4)]
    public void A_step_moves_one_place_and_wraps(int current, int step, int expected)
        => Assert.Equal(expected, RadialMath.StepSelection(current, step, 6));

    [Fact]
    public void From_nothing_selected_it_enters_the_ring_at_the_end_it_came_from()
    {
        // The bubble opens on Cancel, so this is the very first arrow key press.
        Assert.Equal(0, RadialMath.StepSelection(-1, 1, 6));
        Assert.Equal(5, RadialMath.StepSelection(-1, -1, 6));
    }

    [Fact]
    public void An_out_of_range_selection_is_treated_as_nothing_selected()
    {
        // Can happen when the config shrinks while the bubble is open.
        Assert.Equal(0, RadialMath.StepSelection(99, 1, 6));
        Assert.Equal(5, RadialMath.StepSelection(99, -1, 6));
    }

    [Fact]
    public void A_single_segment_ring_stays_on_that_segment()
    {
        Assert.Equal(0, RadialMath.StepSelection(0, 1, 1));
        Assert.Equal(0, RadialMath.StepSelection(0, -1, 1));
        Assert.Equal(0, RadialMath.StepSelection(-1, 1, 1));
    }

    [Fact]
    public void An_empty_ring_has_nothing_to_select()
    {
        Assert.Equal(-1, RadialMath.StepSelection(-1, 1, 0));
        Assert.Equal(-1, RadialMath.StepSelection(0, -1, 0));
    }

    [Fact]
    public void A_zero_step_changes_nothing()
    {
        Assert.Equal(3, RadialMath.StepSelection(3, 0, 6));
        Assert.Equal(-1, RadialMath.StepSelection(-1, 0, 6));
    }

    [Fact]
    public void Stepping_all_the_way_round_returns_to_the_start()
    {
        int index = 0;
        for (int i = 0; i < 6; i++)
            index = RadialMath.StepSelection(index, 1, 6);

        Assert.Equal(0, index);
    }
}

public class IndexForDigitTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(6, 5)]
    public void A_digit_selects_the_segment_with_that_position(int digit, int expected)
        => Assert.Equal(expected, RadialMath.IndexForDigit(digit, 6));

    [Theory]
    [InlineData(7)]  // past the end of a six-segment ring
    [InlineData(9)]
    [InlineData(0)]  // there is no "segment zero"; the ring is 1-based to the user
    [InlineData(-1)]
    [InlineData(10)]
    public void A_digit_that_names_no_segment_selects_nothing(int digit)
        => Assert.Equal(-1, RadialMath.IndexForDigit(digit, 6));

    [Fact]
    public void Nine_is_the_highest_reachable_segment()
    {
        Assert.Equal(8, RadialMath.IndexForDigit(9, 12));

        // A ring with more than nine segments is still fully reachable with the
        // arrow keys; only the shortcut runs out.
        Assert.Equal(-1, RadialMath.IndexForDigit(10, 12));
    }
}

public class MenuAnnouncementTests
{
    [Fact]
    public void The_opening_announcement_states_the_size_and_the_keys()
    {
        string text = MenuAnnouncement.Opened(6);

        Assert.Contains("6 shortcuts", text, StringComparison.Ordinal);
        Assert.Contains("Enter", text, StringComparison.Ordinal);
        Assert.Contains("Escape", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_opening_announcement_is_grammatical_for_one_and_for_none()
    {
        Assert.Contains("1 shortcut.", MenuAnnouncement.Opened(1), StringComparison.Ordinal);
        Assert.DoesNotContain("1 shortcuts", MenuAnnouncement.Opened(1), StringComparison.Ordinal);

        Assert.Contains("No shortcuts configured", MenuAnnouncement.Opened(0), StringComparison.Ordinal);
    }

    [Fact]
    public void A_selection_is_announced_as_a_position_in_a_list()
    {
        Assert.Equal("Settings, 2 of 7.", MenuAnnouncement.Selected(1, 7, "Settings"));
        Assert.Equal("Settings, 1 of 7.", MenuAnnouncement.Selected(0, 7, "Settings"));
        Assert.Equal("Settings, 7 of 7.", MenuAnnouncement.Selected(6, 7, "Settings"));
    }

    [Fact]
    public void Nothing_selected_announces_cancel()
    {
        // The bubble opens here on purpose: a reflexive Enter must cancel, not
        // run whatever happened to be first.
        Assert.Equal("Cancel. Nothing selected.", MenuAnnouncement.Selected(-1, 7, null));
        Assert.Equal("Cancel. Nothing selected.", MenuAnnouncement.Selected(7, 7, "Settings"));
    }

    [Theory]
    [InlineData("Open\nDocuments", "Open Documents")]
    [InlineData("Open\r\nDocuments", "Open Documents")]
    [InlineData("  Open   Documents  ", "Open Documents")]
    [InlineData("Settings", "Settings")]
    public void Labels_are_flattened_to_one_line(string label, string expected)
        => Assert.Equal(expected, MenuAnnouncement.FlattenLabel(label));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void A_label_that_says_nothing_still_announces_something(string? label)
        => Assert.Equal("Unnamed shortcut", MenuAnnouncement.FlattenLabel(label));

    [Fact]
    public void A_wrapped_label_reaches_the_selection_announcement_flattened()
    {
        Assert.Equal("Open Documents, 1 of 6.", MenuAnnouncement.Selected(0, 6, "Open\nDocuments"));
    }
}
