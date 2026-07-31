using System.IO;
using System.Text;

namespace CursorBubble.Diagnostics;

/// <summary>
/// Minimal file log at <c>%APPDATA%\CursorBubble\logs\cursorbubble-yyyyMMdd.log</c>.
///
/// A tray app has no console and usually no visible window, so without this a
/// failure in the field leaves nothing behind to look at. Deliberately tiny: no
/// dependency, no background thread, no configuration. Every method is safe to
/// call from any thread and <b>never throws</b> — logging must not be able to
/// break the thing it is reporting on.
/// </summary>
public static class Log
{
    /// <summary>Log files older than this are deleted on startup.</summary>
    private const int RetentionDays = 7;

    /// <summary>Stop appending past this size so a repeating error cannot fill the disk.</summary>
    private const long MaxBytes = 5 * 1024 * 1024;

    private static readonly object Gate = new();

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CursorBubble", "logs");

    /// <summary>Full path of the log file for today.</summary>
    public static string CurrentFile =>
        Path.Combine(Dir, $"cursorbubble-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INF", message, null);

    public static void Warn(string message, Exception? ex = null) => Write("WRN", message, ex);

    public static void Error(string message, Exception? ex = null) => Write("ERR", message, ex);

    /// <summary>Delete stale log files. Call once at startup; failures are ignored.</summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(Dir))
                return;

            DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (string file in Directory.EnumerateFiles(Dir, "cursorbubble-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch
                {
                    // in use or gone already — nothing to do
                }
            }
        }
        catch
        {
            // logging must never break startup
        }
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(message);

            if (ex is not null)
                line.AppendLine().Append(ex);

            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                string path = CurrentFile;

                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    return;

                File.AppendAllText(path, line.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // A log write failing is never worth surfacing, let alone throwing.
        }
    }
}
