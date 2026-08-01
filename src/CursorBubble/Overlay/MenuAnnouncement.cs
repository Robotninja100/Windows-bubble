namespace CursorBubble.Overlay;

/// <summary>
/// The sentences a screen reader hears while the bubble is open.
///
/// Kept free of WPF types so the wording — including the awkward parts, like a
/// label with a newline in it, or a ring with a single segment — can be tested
/// without a UI. <c>RadialMenuWindow</c> is the only caller.
/// </summary>
internal static class MenuAnnouncement
{
    /// <summary>
    /// What is said when the bubble opens. Nothing is selected at that point, so
    /// this states the size of the ring and how to use it, and — deliberately —
    /// that Enter right now would cancel rather than run something.
    /// </summary>
    public static string Opened(int count)
    {
        if (count <= 0)
            return "Shortcut bubble. No shortcuts configured. Press Escape to close.";

        string plural = count == 1 ? "shortcut" : "shortcuts";
        return $"Shortcut bubble, {count} {plural}. "
             + "Use the arrow keys or the number keys to choose, Enter to run, Escape to cancel.";
    }

    /// <summary>
    /// What is said for a selection. <paramref name="index"/> is zero-based;
    /// -1 means nothing is selected, which is the cancel state the bubble opens in.
    /// </summary>
    public static string Selected(int index, int count, string? label)
    {
        if (index < 0 || index >= count)
            return "Cancel. Nothing selected.";

        // "2 of 7" is what a list normally announces, and it is also the number
        // key that jumps straight back here.
        return $"{FlattenLabel(label)}, {index + 1} of {count}.";
    }

    /// <summary>
    /// A segment label as one line. Labels wrap across lines on the bubble
    /// ("Open\nDocuments"), and a raw newline in an automation name is read as
    /// two unrelated fragments.
    /// </summary>
    public static string FlattenLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "Unnamed shortcut";

        return string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
