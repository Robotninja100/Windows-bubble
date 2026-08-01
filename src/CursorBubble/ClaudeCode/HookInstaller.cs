using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CursorBubble.ClaudeCode;

/// <summary>
/// Registers (or removes) the Claude Code hooks that let CursorBubble detect
/// stopped/waiting sessions, by merging into the user's
/// <c>~/.claude/settings.json</c> without disturbing existing content.
/// </summary>
public static class HookInstaller
{
    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "settings.json");

    /// <summary>
    /// The file to merge into. Settable so the tests can exercise the file-level
    /// behaviour against a temp directory instead of the developer's real
    /// Claude Code configuration — which is precisely the part that used to be
    /// untested, and precisely the part that could destroy data.
    /// </summary>
    internal static string SettingsPath { get; set; } = DefaultSettingsPath;

    /// <summary>How many backups of the user's settings.json to keep.</summary>
    private const int BackupsKept = 3;

    // Events we hook into.
    private static readonly string[] Events = { "Stop", "Notification" };

    /// <summary>The command Claude Code runs: this exe in --hook mode.</summary>
    private static string HookCommand()
    {
        string exe = Environment.ProcessPath ?? "CursorBubble.exe";
        return $"\"{exe}\" --hook";
    }

    public static bool IsInstalled()
    {
        try
        {
            JsonObject root = Load();
            return EventHasOurHook(root, "Stop");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Add our hooks to Stop and Notification (idempotent).</summary>
    public static void Install()
    {
        JsonObject root = Load();
        AddHooks(root, HookCommand());
        Save(root);
    }

    /// <summary>Internal for tests: the pure merge, with no file access.</summary>
    internal static void AddHooks(JsonObject root, string command)
    {
        JsonObject hooks = GetOrCreateObject(root, "hooks");

        foreach (string ev in Events)
        {
            JsonArray groups = GetOrCreateArray(hooks, ev);
            if (ContainsOurCommand(groups))
                continue;

            groups.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command
                })
            });
        }
    }

    /// <summary>Remove any hook groups that invoke CursorBubble.</summary>
    public static void Uninstall()
    {
        JsonObject root = Load();
        RemoveHooks(root);
        Save(root);
    }

    /// <summary>Internal for tests: the pure removal, with no file access.</summary>
    internal static void RemoveHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks)
            return;

        foreach (string ev in Events)
        {
            if (hooks[ev] is not JsonArray groups)
                continue;

            for (int i = groups.Count - 1; i >= 0; i--)
            {
                if (GroupInvokesUs(groups[i]))
                    groups.RemoveAt(i);
            }
        }
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>Internal for tests: is one of our hooks already registered?</summary>
    internal static bool EventHasOurHook(JsonObject root, string ev)
        => root["hooks"] is JsonObject hooks &&
           hooks[ev] is JsonArray groups &&
           ContainsOurCommand(groups);

    private static bool ContainsOurCommand(JsonArray groups)
    {
        foreach (JsonNode? group in groups)
            if (GroupInvokesUs(group))
                return true;
        return false;
    }

    private static bool GroupInvokesUs(JsonNode? group)
    {
        if (group is not JsonObject obj || obj["hooks"] is not JsonArray inner)
            return false;

        foreach (JsonNode? h in inner)
        {
            if (h is JsonObject ho && ho["command"] is JsonValue v &&
                v.TryGetValue(out string? cmd) && cmd is not null &&
                cmd.Contains("--hook", StringComparison.OrdinalIgnoreCase) &&
                cmd.Contains("CursorBubble", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Read the user's settings.json.
    ///
    /// A missing file is fine — that is a first run, and an empty object is the
    /// right starting point. A file that exists but cannot be read as a JSON
    /// object is <em>not</em> fine: this used to fall through to a fresh empty
    /// object, which <see cref="Save"/> then wrote straight over the top,
    /// silently replacing everything the user had in there. Refuse instead, and
    /// say why.
    /// </summary>
    private static JsonObject Load()
    {
        if (!File.Exists(SettingsPath))
            return new JsonObject();

        string text;
        try
        {
            text = File.ReadAllText(SettingsPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not read {SettingsPath}: {ex.Message}", ex);
        }

        // An empty file is a plausible half-written state, and treating it as
        // "nothing to preserve" is safe — there is nothing in it to lose.
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{SettingsPath} is not valid JSON, so it was left untouched. " +
                $"Fix or move the file and try again. ({ex.Message})", ex);
        }

        if (node is not JsonObject obj)
        {
            throw new InvalidOperationException(
                $"{SettingsPath} does not contain a JSON object, so it was left untouched. " +
                "Fix or move the file and try again.");
        }

        return obj;
    }

    /// <summary>
    /// Write the merged settings back, via a backup and a temp file.
    ///
    /// This is somebody else's configuration file, so the two failure modes
    /// worth engineering against are "we wrote the wrong thing" (recoverable
    /// from the backup) and "we died half way through the write" (impossible,
    /// because the move is atomic).
    /// </summary>
    private static void Save(JsonObject root)
    {
        string dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);

        BackUp();

        var options = new JsonSerializerOptions { WriteIndented = true };
        string temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(options));
        File.Move(temp, SettingsPath, overwrite: true);
    }

    /// <summary>
    /// Copy the current settings.json aside before overwriting it, keeping the
    /// newest <see cref="BackupsKept"/>. Never throws: failing to take a backup
    /// is not a reason to refuse to link.
    /// </summary>
    private static void BackUp()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;

            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Copy(SettingsPath, $"{SettingsPath}.cursorbubble-backup-{stamp}", overwrite: true);

            string dir = Path.GetDirectoryName(SettingsPath)!;
            string prefix = Path.GetFileName(SettingsPath) + ".cursorbubble-backup-";

            foreach (string old in Directory.EnumerateFiles(dir, prefix + "*")
                                            .OrderByDescending(p => p, StringComparer.Ordinal)
                                            .Skip(BackupsKept))
            {
                File.Delete(old);
            }
        }
        catch
        {
            // Best effort. The atomic write below is the real protection.
        }
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing)
            return existing;
        var created = new JsonArray();
        parent[key] = created;
        return created;
    }
}
