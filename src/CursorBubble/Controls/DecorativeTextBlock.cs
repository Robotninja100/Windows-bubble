using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace CursorBubble.Controls;

/// <summary>
/// A <see cref="TextBlock"/> that stays out of the automation tree.
///
/// For text that is meaningless when read aloud — chiefly the icon previews,
/// which render a private-use-area codepoint from the Segoe Fluent Icons font and
/// would otherwise be announced as garbage in front of the real label.
/// </summary>
public class DecorativeTextBlock : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);

    private sealed class DecorativePeer : TextBlockAutomationPeer
    {
        public DecorativePeer(TextBlock owner) : base(owner)
        {
        }

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}
