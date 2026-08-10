namespace CursorBubble.Controls;

/// <summary>
/// The icon codepoints this app draws from the Segoe Fluent Icons / Segoe MDL2
/// Assets system font.
///
/// Written as escapes rather than as the characters themselves. They live in the
/// Unicode private use area, so pasted literally they are invisible in a diff,
/// unsearchable, and a merge that mangles one produces a box on screen that
/// nobody can trace back to a line of code. A name and a codepoint say what was
/// meant.
///
/// Every glyph is decorative: each one sits next to text that carries the
/// meaning, and the controls that render them stay out of the automation tree
/// (see <see cref="DecorativeTextBlock"/>).
/// </summary>
public static class Glyphs
{
    public const string Home = "\uE80F";
    public const string Settings = "\uE713";
    public const string View = "\uE890";
    public const string Edit = "\uE70F";
    public const string Color = "\uE790";
    public const string Lightbulb = "\uEA80";
    public const string Message = "\uE8BD";
    public const string Info = "\uE946";
    public const string Star = "\uE734";
    public const string Calendar = "\uE787";
    public const string Stopwatch = "\uE916";
    public const string Play = "\uE768";
    public const string Cancel = "\uE711";
    public const string Keyboard = "\uE765";
    public const string Folder = "\uE8B7";
    public const string Save = "\uE74E";
    public const string Calculator = "\uE1D0";
    public const string Camera = "\uE722";
    public const string Document = "\uE7C3";
    public const string Globe = "\uE774";
    public const string Mail = "\uE715";
    public const string Music = "\uE8D6";
    public const string Photo = "\uE91B";
    public const string Terminal = "\uE756";
}
