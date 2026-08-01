using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace CursorBubble.Overlay;

/// <summary>
/// Presents the bubble as a single opaque element to a screen reader.
///
/// <see cref="RadialMenuControl"/> draws itself out of <c>Path</c> geometry with
/// <c>TextBlock</c>s for the labels and the icon glyphs. Left alone, the label
/// TextBlocks each get their own peer, so the control dumps every segment label —
/// each preceded by an unreadable private-use-area glyph — into the tree. That is
/// noise in the overlay and outright wrong in the settings window, where the same
/// control is a decorative live preview.
///
/// Returning no children collapses all of it. What a screen reader should actually
/// hear about the bubble is announced separately, from
/// <see cref="RadialMenuWindow"/>, as a single sentence per selection.
/// </summary>
internal sealed class RadialMenuAutomationPeer : FrameworkElementAutomationPeer
{
    public RadialMenuAutomationPeer(RadialMenuControl owner) : base(owner)
    {
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.Group;

    protected override string GetClassNameCore() => nameof(RadialMenuControl);

    protected override List<AutomationPeer> GetChildrenCore() => new();

    protected override string GetNameCore()
    {
        string name = AutomationProperties.GetName(Owner);
        return string.IsNullOrEmpty(name) ? "Shortcut bubble" : name;
    }
}
