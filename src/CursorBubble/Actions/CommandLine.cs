using System.Text;

namespace CursorBubble.Actions;

/// <summary>
/// Splits the single free-text "Arguments" field into individual arguments.
///
/// The settings UI offers one text box, but <c>ProcessStartInfo.ArgumentList</c>
/// wants the arguments already separated — and using the list is the whole point:
/// it makes the runtime responsible for quoting, so an argument containing a
/// space, a quote or a <c>&amp;</c> is passed through as one argument instead of
/// being re-interpreted by the shell.
///
/// The rules are the ones Windows itself uses when parsing a command line, so
/// what the user types behaves the way it would in a terminal. Kept free of any
/// WPF or process types so it can be tested directly.
/// </summary>
internal static class CommandLine
{
    /// <summary>
    /// Split <paramref name="arguments"/> on whitespace, honouring double quotes
    /// and backslash escaping. Returns an empty array for null or blank input.
    /// </summary>
    public static string[] Split(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return Array.Empty<string>();

        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        bool started = false;   // distinguishes "" (an empty argument) from no argument
        int backslashes = 0;

        foreach (char c in arguments)
        {
            if (c == '\\')
            {
                backslashes++;
                started = true;
                continue;
            }

            if (c == '"')
            {
                // A run of backslashes before a quote: each pair is one literal
                // backslash, and an odd one out escapes the quote itself.
                current.Append('\\', backslashes / 2);
                if (backslashes % 2 == 1)
                    current.Append('"');
                else
                    inQuotes = !inQuotes;

                backslashes = 0;
                started = true;
                continue;
            }

            current.Append('\\', backslashes);
            backslashes = 0;

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (started)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
                continue;
            }

            current.Append(c);
            started = true;
        }

        current.Append('\\', backslashes);
        if (started)
            result.Add(current.ToString());

        return result.ToArray();
    }
}
