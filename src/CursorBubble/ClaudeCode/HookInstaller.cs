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
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "settings.json");

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

    private static JsonObject Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                JsonNode? node = JsonNode.Parse(File.ReadAllText(SettingsPath));
                if (node is JsonObject obj)
                    return obj;
            }
        }
        catch
        {
            // corrupt file — start from a fresh object rather than lose the ability to link
        }
        return new JsonObject();
    }

    private static void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(SettingsPath, root.ToJsonString(options));
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
