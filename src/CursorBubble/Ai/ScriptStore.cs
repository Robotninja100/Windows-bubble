using System.IO;
using System.Text;

namespace CursorBubble.Ai;

/// <summary>
/// Saves AI-generated scripts as .ps1 files under
/// <c>%APPDATA%\CursorBubble\scripts\</c> so a segment can point at them.
/// </summary>
public static class ScriptStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CursorBubble", "scripts");

    /// <summary>
    /// Write <paramref name="script"/> to a .ps1 file named after
    /// <paramref name="preferredName"/> and return its full path.
    /// </summary>
    public static string Save(string preferredName, string script)
    {
        Directory.CreateDirectory(Dir);

        string baseName = Sanitize(preferredName);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "script";

        string path = Path.Combine(Dir, baseName + ".ps1");
        int i = 2;
        while (File.Exists(path))
            path = Path.Combine(Dir, $"{baseName}-{i++}.ps1");

        // UTF-8 with BOM so Windows PowerShell reads non-ASCII characters correctly.
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>Internal for tests: reduce a label to something safe for a file name.</summary>
    internal static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.Trim())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_') sb.Append('-');
            // drop everything else (newlines, invalid path chars, etc.)
        }
        return sb.ToString().Trim('-');
    }
}
