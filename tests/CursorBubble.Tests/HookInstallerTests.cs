using System.IO;
using System.Text.Json.Nodes;
// WPF's implicit usings bring in System.Windows.Shapes.Path, so plain "Path"
// would be ambiguous in this project.
using Path = System.IO.Path;
using CursorBubble.ClaudeCode;
using Xunit;

namespace CursorBubble.Tests;

/// <summary>
/// Linking merges into the user's own ~/.claude/settings.json. Losing somebody
/// else's settings there would be a genuinely destructive bug, so the merge is
/// pinned down here.
/// </summary>
public class HookInstallerTests
{
    private const string Command = "\"C:\\Apps\\CursorBubble.exe\" --hook";

    [Fact]
    public void Adds_hooks_for_both_events()
    {
        var root = new JsonObject();

        HookInstaller.AddHooks(root, Command);

        Assert.True(HookInstaller.EventHasOurHook(root, "Stop"));
        Assert.True(HookInstaller.EventHasOurHook(root, "Notification"));
    }

    [Fact]
    public void Linking_twice_does_not_duplicate_the_hook()
    {
        var root = new JsonObject();

        HookInstaller.AddHooks(root, Command);
        HookInstaller.AddHooks(root, Command);

        JsonArray stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Single(stop);
    }

    [Fact]
    public void Unrelated_settings_are_left_alone()
    {
        JsonObject root = JsonNode.Parse("""
        {
          "model": "claude-opus-5",
          "env": { "FOO": "bar" },
          "hooks": {
            "Stop": [
              { "hooks": [ { "type": "command", "command": "echo someone-elses-tool" } ] }
            ]
          }
        }
        """)!.AsObject();

        HookInstaller.AddHooks(root, Command);
        HookInstaller.RemoveHooks(root);

        // Ours is gone; everything that was already there survives untouched.
        Assert.False(HookInstaller.EventHasOurHook(root, "Stop"));
        Assert.Equal("claude-opus-5", root["model"]!.GetValue<string>());
        Assert.Equal("bar", root["env"]!["FOO"]!.GetValue<string>());

        JsonArray stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Single(stop);
        Assert.Contains("someone-elses-tool", stop[0]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unlinking_removes_every_one_of_our_hooks()
    {
        var root = new JsonObject();
        HookInstaller.AddHooks(root, Command);

        HookInstaller.RemoveHooks(root);

        Assert.False(HookInstaller.EventHasOurHook(root, "Stop"));
        Assert.False(HookInstaller.EventHasOurHook(root, "Notification"));
    }

    [Fact]
    public void Unlinking_an_untouched_file_is_a_no_op()
    {
        JsonObject root = JsonNode.Parse("""{"model":"claude-opus-5"}""")!.AsObject();

        HookInstaller.RemoveHooks(root);

        Assert.Equal("claude-opus-5", root["model"]!.GetValue<string>());
    }

    [Fact]
    public void A_hook_from_a_different_install_path_is_still_recognised_as_ours()
    {
        // Recognition is by "--hook" + "CursorBubble", so moving the exe does not
        // leave a stale duplicate behind.
        var root = new JsonObject();
        HookInstaller.AddHooks(root, "\"D:\\Elsewhere\\CursorBubble.exe\" --hook");

        HookInstaller.AddHooks(root, Command);

        Assert.Single(root["hooks"]!["Stop"]!.AsArray());
    }

    [Fact]
    public void An_unrelated_hook_is_not_mistaken_for_ours()
    {
        JsonObject root = JsonNode.Parse("""
        { "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "notify-send done" } ] } ] } }
        """)!.AsObject();

        Assert.False(HookInstaller.EventHasOurHook(root, "Stop"));
    }
}

/// <summary>
/// The file-level half of linking, which the merge tests above never touched —
/// and which is where the damage would actually be done. These run against a
/// temp directory, never the developer's real ~/.claude/settings.json.
/// </summary>
public sealed class HookInstallerFileTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly string _original;

    public HookInstallerFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cursorbubble-hooks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");

        _original = HookInstaller.SettingsPath;
        HookInstaller.SettingsPath = _path;
    }

    public void Dispose()
    {
        HookInstaller.SettingsPath = _original;
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void Linking_with_no_settings_file_creates_one()
    {
        HookInstaller.Install();

        Assert.True(File.Exists(_path));
        Assert.True(HookInstaller.IsInstalled());
    }

    [Fact]
    public void Linking_preserves_everything_else_in_the_file()
    {
        File.WriteAllText(_path, """
        { "model": "claude-opus-5", "env": { "FOO": "bar" } }
        """);

        HookInstaller.Install();

        JsonObject root = JsonNode.Parse(File.ReadAllText(_path))!.AsObject();
        Assert.Equal("claude-opus-5", root["model"]!.GetValue<string>());
        Assert.Equal("bar", root["env"]!["FOO"]!.GetValue<string>());
        Assert.True(HookInstaller.EventHasOurHook(root, "Stop"));
    }

    [Fact]
    public void A_file_that_is_not_valid_JSON_is_refused_rather_than_overwritten()
    {
        // The regression this whole class exists for: this used to fall through
        // to an empty object and write it straight over the user's settings.
        const string garbage = "{ \"model\": \"claude-opus-5\", oops";
        File.WriteAllText(_path, garbage);

        Assert.Throws<InvalidOperationException>(HookInstaller.Install);
        Assert.Equal(garbage, File.ReadAllText(_path));
    }

    [Fact]
    public void A_file_that_is_not_a_JSON_object_is_refused_rather_than_overwritten()
    {
        const string array = """[ "not", "an", "object" ]""";
        File.WriteAllText(_path, array);

        Assert.Throws<InvalidOperationException>(HookInstaller.Install);
        Assert.Equal(array, File.ReadAllText(_path));
    }

    [Fact]
    public void Unlinking_a_corrupt_file_is_refused_too()
    {
        const string garbage = "not json at all";
        File.WriteAllText(_path, garbage);

        Assert.Throws<InvalidOperationException>(HookInstaller.Uninstall);
        Assert.Equal(garbage, File.ReadAllText(_path));
    }

    [Fact]
    public void An_empty_file_is_treated_as_a_fresh_start()
    {
        // Nothing in it to lose, so this one is safe to overwrite.
        File.WriteAllText(_path, "   ");

        HookInstaller.Install();

        Assert.True(HookInstaller.IsInstalled());
    }

    [Fact]
    public void Writing_leaves_a_backup_of_what_was_there_before()
    {
        const string before = """{ "model": "claude-opus-5" }""";
        File.WriteAllText(_path, before);

        HookInstaller.Install();

        string[] backups = Directory.GetFiles(_dir, "settings.json.cursorbubble-backup-*");
        Assert.Single(backups);
        Assert.Equal(before, File.ReadAllText(backups[0]));
    }

    [Fact]
    public void No_temp_file_is_left_behind()
    {
        HookInstaller.Install();

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void A_corrupt_file_reports_as_not_installed_instead_of_throwing()
    {
        // IsInstalled runs on every settings-panel switch; it must stay quiet.
        File.WriteAllText(_path, "not json");

        Assert.False(HookInstaller.IsInstalled());
    }
}
