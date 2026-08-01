using System.IO;
using System.Text;

namespace CursorBubble.Storage;

/// <summary>
/// Writes a file so that a reader never observes a half-written one.
///
/// Every store in this app previously wrote straight onto its destination with
/// <see cref="File.WriteAllText(string, string)"/>. That is fine until the
/// process is killed, the machine loses power, or a virus scanner holds the
/// handle at the wrong moment — at which point the destination is left
/// truncated. For <c>config.json</c> that cost the user every setting they had
/// (the loader treats an unparseable file as "start from defaults"), and for
/// <c>~/.claude/settings.json</c> it damaged a file this app does not own.
///
/// The fix is the usual one: write a temporary file beside the destination,
/// flush it to disk, then replace the destination in a single move. A reader
/// sees either the old file or the new one, never a mixture.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Write <paramref name="contents"/> to <paramref name="path"/>, replacing
    /// any existing file only once the new content is safely on disk.
    /// </summary>
    /// <param name="encoding">Defaults to UTF-8 without a byte-order mark.</param>
    public static void WriteAllText(string path, string contents, Encoding? encoding = null)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // The temporary file must sit next to the destination: a move is only
        // atomic within one volume, and %TEMP% is regularly on another one.
        // The process id keeps two writers apart — the --hook process and the
        // app itself both write to the inbox directory.
        string temp = $"{path}.{Environment.ProcessId}.tmp";

        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();

                // Flush the OS buffers too. Without this the move can complete
                // while the new content is still only in the page cache, which
                // is precisely the case a power cut turns into a zero-length
                // file — the failure this class exists to prevent.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Leave the destination as it was, and do not leave litter behind.
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort. A stray .tmp file is harmless; the next write to the
            // same path from this process overwrites it.
        }
    }
}
