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
            switch (segment.Action)
            {
                case ActionType.OpenPath:
                    OpenPath(Expand(segment.Target));
                    break;

                case ActionType.LaunchProgram:
                    LaunchProgram(Expand(segment.Target), Expand(segment.Arguments));
                    break;

                case ActionType.RunScript:
                    RunScript(Expand(segment.Target), Expand(segment.Arguments));
                    break;
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"'{segment.Label}' failed: {ex.Message}";
        }
    }

    private static void OpenPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("No target set.");

        // ShellExecute handles files, folders and URLs with their default app.
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private static void LaunchProgram(string target, string? args)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("No program set.");

        var psi = new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            Arguments = args ?? string.Empty
        };
        Process.Start(psi);
    }

    private static void RunScript(string target, string? args)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("No script/command set.");

        string ext = Path.GetExtension(target).ToLowerInvariant();

        ProcessStartInfo psi = ext switch
        {
            ".ps1" => new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-ExecutionPolicy Bypass -File \"{target}\" {args}",
                UseShellExecute = false
            },
            ".bat" or ".cmd" => new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c \"{target}\" {args}",
                UseShellExecute = false
            },
            // No known script extension: treat the target as an inline shell
            // command line (e.g. "powershell -Command ...").
            _ => new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c {target} {args}",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        Process.Start(psi);
    }

    /// <summary>Expand %ENV% variables so config can use paths like %USERPROFILE%.</summary>
    private static string Expand(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : Environment.ExpandEnvironmentVariables(value);
}
