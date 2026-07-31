namespace CursorBubble.Config;

/// <summary>
/// What a radial-menu segment does when chosen.
/// </summary>
public enum ActionType
{
    /// <summary>Open a file, folder or URL with its default handler (ShellExecute).</summary>
    OpenPath,

    /// <summary>Launch a specific executable, optionally with arguments.</summary>
    LaunchProgram,

    /// <summary>Run a script (.bat/.cmd/.ps1) or an inline PowerShell command.</summary>
    RunScript,

    /// <summary>Open the Claude Code inbox (pending sessions to answer).</summary>
    ClaudeInbox
}
