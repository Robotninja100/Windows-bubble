namespace CursorBubble.Settings;

/// <summary>
/// The rows the settings window binds to. Records rather than fields on the
/// window, because a data template can only bind to properties on something.
///
/// Everything here is display-ready text: the formatting is done once, where the
/// numbers are, instead of by a converter that would have to be found and read
/// before the page makes sense.
/// </summary>
/// <param name="Glyph">A Segoe Fluent Icons codepoint, decorative only.</param>
/// <param name="Title">The page's name, in the sidebar and as the page heading.</param>
/// <param name="Subtitle">What the page is for, shown under its heading.</param>
internal sealed record NavPage(string Glyph, string Title, string Subtitle);

/// <summary>One counter on the Overview page.</summary>
/// <param name="Glyph">A Segoe Fluent Icons codepoint, decorative only.</param>
/// <param name="Caption">What is being counted.</param>
/// <param name="Value">The number itself, already formatted.</param>
/// <param name="Detail">A second line putting the number in context.</param>
internal sealed record StatTileItem(string Glyph, string Caption, string Value, string Detail);

/// <summary>One bar in the "what you actually use" chart.</summary>
/// <param name="Label">The segment's name, on one line.</param>
/// <param name="BarWidth">
/// Width of the filled part in device-independent pixels, measured against the
/// fixed track width in the template.
/// </param>
/// <param name="CountText">Runs and share, as shown at the end of the bar.</param>
internal sealed record SegmentBarItem(string Label, double BarWidth, string CountText);

/// <summary>One group of settings, as a row that navigates to its page.</summary>
/// <param name="Key">The <see cref="Config.Tunables"/> key, mapped back to a page on click.</param>
/// <param name="Glyph">A Segoe Fluent Icons codepoint, decorative only.</param>
/// <param name="Title">The group's name.</param>
/// <param name="Summary">What it holds, in one line.</param>
/// <param name="CountText">How many things it holds.</param>
/// <param name="NavigateHint">What the row does, for a screen reader.</param>
internal sealed record TuningRow(
    string Key, string Glyph, string Title, string Summary, string CountText, string NavigateHint);
