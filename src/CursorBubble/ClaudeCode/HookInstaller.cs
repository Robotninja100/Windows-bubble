using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CursorBubble.Storage;

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
            JsonObject? root = Load();
            return root is not null && EventHasOurHook(root, "Stop");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Add our hooks to Stop and Notification (idempotent).</summary>
    public static void Install()
    {
        JsonObject root = LoadOrRefuse();
        JsonObject hooks = GetOrCreateObject(root, "hooks");
        string command = HookCommand();

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

        Save(root);
    }

    /// <summary>Remove any hook groups that invoke CursorBubble.</summary>
    public static void Uninstall()
    {
        JsonObject root = LoadOrRefuse();
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

        Save(root);
    }

    // ---- helpers -------------------------------------------------------------

    private static bool EventHasOurHook(JsonObject root, string ev)
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
    /// Read <c>~/.claude/settings.json</c>.
    ///
    /// Returns an empty object when the file does not exist yet — that is a
    /// first link, and writing a fresh object is correct. Returns <c>null</c>
    /// when the file <em>does</em> exist but cannot be read or is not a JSON
    /// object, which callers that intend to write must treat as a stop signal.
    /// </summary>
    private static JsonObject? Load()
    {
        if (!File.Exists(SettingsPath))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="Load"/> for the write paths, refusing rather than starting
    /// from a blank object.
    ///
    /// This used to fall back to an empty object on any read error, and
    /// <see cref="Install"/> then saved that over the file — so one unreadable
    /// read deleted every Claude Code setting the user had, in order to add a
    /// hook. Linking is a convenience; someone's editor config, permissions and
    /// MCP servers are not. When in doubt, change nothing.
    /// </summary>
    private static JsonObject LoadOrRefuse()
        => Load() ?? throw new InvalidOperationException(
            $"{SettingsPath} kon niet gelezen worden. Er is niets gewijzigd, zodat je " +
            "bestaande Claude Code-instellingen niet overschreven worden. Controleer of " +
            "het bestand geldige JSON bevat en probeer het opnieuw.");

    private static void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        AtomicFile.WriteAllText(SettingsPath, root.ToJsonString(options));
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
