namespace CursorBubble.Overlay;

/// <summary>
/// How a particular opening of the bubble is being driven. The two modes need
/// opposite things from the window: the mouse gesture requires a window that can
/// never take focus and never swallow a click, while the keyboard requires one
/// that can do both.
/// </summary>
public enum MenuInputMode
{
    /// <summary>
    /// Opened by the right-drag gesture. The window is click-through and
    /// non-activating; selection follows the global cursor position.
    /// </summary>
    Mouse,

    /// <summary>
    /// Opened by the global hotkey. The window takes focus, reads the keyboard,
    /// and hands focus back to wherever it came from when it closes.
    /// </summary>
    Keyboard
}
