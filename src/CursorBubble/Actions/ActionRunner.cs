using System.Diagnostics;
using System.IO;
using CursorBubble.Config;

namespace CursorBubble.Actions;

/// <summary>
/// Executes the action attached to a radial-menu segment.
/// </summary>
public static class ActionRunner
{
    /// <summary>
    /// Run the given segment's action. Returns null on success or an error
    /// message the caller can surface (e.g. as a tray balloon).
    /// </summary>
    public static string? Run(SegmentConfig segment)
    {
        try
        {
            Process.Start(BuildStartInfo(segment));
            return null;
        }
        catch (Exception ex)
        {
            return $"'{segment.Label}' failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Work out exactly how a segment will be launched, without launching it.
    ///
    /// Separated from <see cref="Run"/> so the decision — which executable, with
    /// which arguments — can be asserted in a test rather than only observed by
    /// running the thing.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(SegmentConfig segment)
    {
        string target = Expand(segment.Target);
        string arguments = Expand(segment.Arguments);

        return segment.Action switch
        {
            ActionType.OpenPath => OpenPath(target),
            ActionType.LaunchProgram => LaunchProgram(target, arguments),
            ActionType.RunScript => RunScript(target, arguments),
            _ => throw new InvalidOperationException($"Unsupported action: {segment.Action}.")
        };
    }

    private static ProcessStartInfo OpenPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("No target set.");

        // ShellExecute handles files, folders and URLs with their default app.
        return new ProcessStartInfo(target) { UseShellExecute = true };
    }

    private static ProcessStartInfo LaunchProgram(string target, string arguments)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("No program set.");

        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        AddArguments(psi, arguments);
        return psi;
    }

    private static ProcessStartInfo RunScript(string target, string arguments)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("No script/command set.");

        string ext = Path.GetExtension(target).ToLowerInvariant();

        switch (ext)
        {
            case ".ps1":
            {
                var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false };
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(target);
                AddArguments(psi, arguments);
                return psi;
            }

            case ".bat":
            case ".cmd":
            {
                var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(target);
                AddArguments(psi, arguments);
                return psi;
            }

            default:
            {
                // No known script extension: the target is an inline command line
                // (e.g. "powershell -Command ..."), so it goes through cmd as
                // written. Arguments are deliberately *not* appended here — the
                // user has already written a whole command line, and tacking a
                // second field onto the end of it is where the surprises live.
                // SettingsWindow warns when both are filled in.
                return new ProcessStartInfo("cmd.exe")
                {
                    Arguments = "/c " + target,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
        }
    }

    /// <summary>
    /// Append the Arguments field as separate arguments.
    ///
    /// Via <see cref="ProcessStartInfo.ArgumentList"/> rather than by building a
    /// string: the runtime then quotes each one, so an argument containing a
    /// space, a quote or a <c>&amp;</c> arrives as a single argument. Building
    /// the string by hand meant an <c>&amp;</c> in this field was a command
    /// separator to cmd, and ran a second program.
    /// </summary>
    private static void AddArguments(ProcessStartInfo psi, string arguments)
    {
        foreach (string arg in CommandLine.Split(arguments))
            psi.ArgumentList.Add(arg);
    }

    /// <summary>Expand %ENV% variables so config can use paths like %USERPROFILE%.</summary>
    private static string Expand(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : Environment.ExpandEnvironmentVariables(value);
}
