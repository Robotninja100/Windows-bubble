using System.Text.Json.Nodes;
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
