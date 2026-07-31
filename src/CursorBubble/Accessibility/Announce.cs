using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CursorBubble.Accessibility;

/// <summary>
/// Announces a change to a screen reader.
///
/// In WPF, setting <see cref="AutomationProperties.LiveSettingProperty"/> alone
/// announces nothing — unlike UWP, the element's automation peer has to raise
/// <see cref="AutomationEvents.LiveRegionChanged"/> every time the text changes.
/// This is the one place that happens, so no call site has to remember it.
/// </summary>
internal static class Announce
{
    /// <summary>Set a status <see cref="TextBlock"/> and announce the new text.</summary>
    public static void Text(TextBlock target, string text)
    {
        target.Text = text;
        LiveRegion(target);
    }

    /// <summary>
    /// Announce that an element's content changed. Safe to call when nothing is
    /// listening, and safe for an element that has only just become visible.
    /// </summary>
    public static void LiveRegion(UIElement element)
    {
        // Cheap early-out: with no screen reader attached there is no peer tree
        // to build and nothing to raise.
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            return;

        // CreatePeerForElement, not FromElement: FromElement returns null unless a
        // peer already exists, which is the usual reason a live region silently
        // does nothing.
        AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>
    /// Announce an element that was collapsed until a moment ago. It has no peer
    /// until a layout pass has run, so the raise is deferred one dispatcher step.
    /// </summary>
    public static void LiveRegionWhenShown(UIElement element)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            return;

        element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => LiveRegion(element));
    }
}
