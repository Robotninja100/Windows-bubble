using System.IO;
using CursorBubble.Ai;
using CursorBubble.ClaudeCode;
using CursorBubble.Config;
using CursorBubble.Native;
using Xunit;
// WPF's implicit usings bring in System.Windows.Shapes.Path, so plain "Path"
// would be ambiguous in this project.
using Path = System.IO.Path;

namespace CursorBubble.Tests;

public class ScriptStoreTests
{
    [Theory]
    [InlineData("Run Backup Script", "Run-Backup-Script")]
    [InlineData("Open\nDocuments", "OpenDocuments")]
    [InlineData("weird/\\:*?\"<>|chars", "weirdchars")]
    [InlineData("  padded  ", "padded")]
    [InlineData("---", "")]
    public void Labels_are_reduced_to_a_safe_file_name(string label, string expected)
    {
        Assert.Equal(expected, ScriptStore.Sanitize(label));
    }

    [Fact]
    public void No_path_separator_can_survive_sanitising()
    {
        // A label is user input and ends up in a file path; traversal must not
        // be reachable from it.
        string safe = ScriptStore.Sanitize("../../windows/system32/evil");

        Assert.DoesNotContain("..", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("/", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", safe, StringComparison.Ordinal);
        // Fully qualified: WPF pulls in System.Windows.Shapes.Path, which would
        // otherwise make a bare "Path" ambiguous here.
        Assert.Equal(-1, safe.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()));
    }
}

public class DataProtectionTests
{
    [Fact]
    public void An_api_key_survives_a_round_trip()
    {
        const string key = "sk-ant-api03-not-a-real-key";

        string stored = DataProtection.Protect(key);

        Assert.NotEqual(key, stored);
        Assert.DoesNotContain("sk-ant", stored, StringComparison.Ordinal);

        Assert.True(DataProtection.TryUnprotect(stored, out string back));
        Assert.Equal(key, back);
    }

    [Fact]
    public void Empty_stays_empty_rather_than_becoming_a_blob()
    {
        Assert.Equal("", DataProtection.Protect(null));
        Assert.Equal("", DataProtection.Protect(""));

        Assert.True(DataProtection.TryUnprotect(null, out string fromNull));
        Assert.Equal("", fromNull);

        Assert.True(DataProtection.TryUnprotect("", out string fromEmpty));
        Assert.Equal("", fromEmpty);
    }

    [Fact]
    public void A_plain_text_key_from_an_older_config_is_returned_as_is()
    {
        // Before DPAPI the key was stored in the clear; those configs must keep working.
        Assert.True(DataProtection.TryUnprotect("sk-plain-text", out string key));
        Assert.Equal("sk-plain-text", key);
    }

    [Fact]
    public void Ciphertext_this_machine_cannot_open_is_reported_rather_than_handed_back()
    {
        // What a config copied from another pc or another Windows account looks
        // like: valid base64, but not a blob DPAPI will open for this user.
        //
        // This is the case the old Unprotect got wrong. It returned the stored
        // string unchanged, so the caller received base64 text believing it was
        // the key, sent that to the API, and got back "invalid key" — pointing
        // the user at their key rather than at the machine they copied it to.
        string foreignBlob = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        Assert.False(DataProtection.TryUnprotect(foreignBlob, out string key));
        Assert.Equal("", key);
    }

    [Fact]
    public void A_key_that_cannot_be_decrypted_reads_as_no_key_at_all()
    {
        // The consequence of the above, where the app actually sees it: the AI
        // screen shows an empty field and says a key is needed, instead of
        // failing later with a misleading error from the API.
        var config = new AppConfig
        {
            AiApiKeyProtected = Convert.ToBase64String(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 })
        };

        Assert.Equal("", config.AiApiKey);
    }
}

public class InboxRecordTests
{
    [Fact]
    public void State_is_described_in_words_for_the_list()
    {
        Assert.Equal("Waiting for you", new InboxRecord { State = SessionState.Waiting }.StateText);
        Assert.Equal("Finished", new InboxRecord { State = SessionState.Stopped }.StateText);
    }

    [Fact]
    public void A_missing_project_name_still_shows_something()
    {
        Assert.Equal("(unknown project)", new InboxRecord { ProjectName = "" }.DisplayProject);
        Assert.Equal("(unknown project)", new InboxRecord { ProjectName = "   " }.DisplayProject);
        Assert.Equal("windows-bubble", new InboxRecord { ProjectName = "windows-bubble" }.DisplayProject);
    }

    [Fact]
    public void The_snippet_is_one_line_and_bounded()
    {
        var record = new InboxRecord { Message = "line one\r\nline two\nline three" };

        Assert.DoesNotContain('\n', record.Snippet);
        Assert.DoesNotContain('\r', record.Snippet);
        Assert.Equal("line one line two line three", record.Snippet);
    }

    [Theory]
    [InlineData("a\r\nb")]        // Windows line break — the common case
    [InlineData("a\n\nb")]        // blank line between paragraphs
    [InlineData("a \t b")]        // mixed spaces and tabs
    [InlineData("a   b")]
    public void Runs_of_whitespace_collapse_to_a_single_space(string message)
    {
        // Replacing "\r" and "\n" separately used to leave a double space behind
        // for every CRLF, which is every line break a Windows session produces.
        Assert.Equal("a b", new InboxRecord { Message = message }.Snippet);
    }

    [Fact]
    public void A_long_message_is_truncated_with_an_ellipsis()
    {
        var record = new InboxRecord { Message = new string('x', 200) };

        Assert.Equal(71, record.Snippet.Length); // 70 characters plus the ellipsis
        Assert.EndsWith("…", record.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void A_timestamp_becomes_a_local_time_and_a_bad_one_stays_blank()
    {
        var record = new InboxRecord { Timestamp = new DateTime(2026, 7, 31, 14, 5, 0, DateTimeKind.Utc).ToString("o") };
        Assert.Matches(@"^\d{2}:\d{2}$", record.WhenLocal);

        Assert.Equal("", new InboxRecord { Timestamp = "not a date" }.WhenLocal);
        Assert.Equal("", new InboxRecord { Timestamp = "" }.WhenLocal);
    }

    [Fact]
    public void The_accessible_summary_is_one_punctuated_sentence()
    {
        var record = new InboxRecord
        {
            ProjectName = "windows-bubble",
            State = SessionState.Waiting,
            Message = "Which branch\r\nshould I use?",
            Timestamp = new DateTime(2026, 7, 31, 14, 5, 0, DateTimeKind.Utc).ToString("o")
        };

        string summary = record.AccessibleSummary;

        Assert.StartsWith("windows-bubble. Waiting for you, ", summary, StringComparison.Ordinal);
        Assert.EndsWith(". Which branch should I use?", summary, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', summary);
    }

    [Fact]
    public void The_accessible_summary_drops_the_time_when_there_is_none()
    {
        var record = new InboxRecord { ProjectName = "demo", Message = "done" };

        // No stray comma before the full stop when the timestamp is unparseable.
        Assert.Equal("demo. Finished. done", record.AccessibleSummary);
    }
}

/// <summary>
/// The inbox directory itself: what the badge counts, and what gets cleaned up.
/// Runs against a temp directory, never the real %APPDATA% inbox.
/// </summary>
public sealed class InboxStoreTests : IDisposable
{
    private readonly string _original;

    public InboxStoreTests()
    {
        _original = InboxStore.Dir;
        InboxStore.Dir = Path.Combine(Path.GetTempPath(), "cursorbubble-inbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(InboxStore.Dir);
    }

    public void Dispose()
    {
        string dir = InboxStore.Dir;
        InboxStore.Dir = _original;
        try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
    }

    private static void Write(string name, string json)
        => File.WriteAllText(Path.Combine(InboxStore.Dir, name + ".json"), json);

    [Fact]
    public void The_badge_counts_the_same_sessions_the_list_shows()
    {
        // The regression: the badge counted files while LoadAll skipped ones it
        // could not parse, so it could read higher than the responder's list.
        InboxStore.Save(new InboxRecord { SessionId = "a", Message = "one" });
        InboxStore.Save(new InboxRecord { SessionId = "b", Message = "two" });
        Write("corrupt", "{ this is not json");

        Assert.Equal(2, InboxStore.LoadAll().Count);
        Assert.Equal(2, InboxStore.UnansweredCount());
    }

    [Fact]
    public void A_record_with_no_session_id_counts_for_nothing()
    {
        Write("empty", """{ "Message": "orphan" }""");

        Assert.Equal(0, InboxStore.UnansweredCount());
    }

    [Fact]
    public void An_empty_inbox_counts_zero()
    {
        Assert.Equal(0, InboxStore.UnansweredCount());
    }

    [Fact]
    public void Old_records_are_pruned_and_recent_ones_are_kept()
    {
        InboxStore.Save(new InboxRecord { SessionId = "fresh", Message = "today" });
        InboxStore.Save(new InboxRecord { SessionId = "stale", Message = "ages ago" });

        string stale = Path.Combine(InboxStore.Dir, "stale.json");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));

        InboxStore.Prune(maxAgeDays: 14);

        Assert.False(File.Exists(stale));
        Assert.Single(InboxStore.LoadAll());
    }

    [Fact]
    public void Pruning_clears_out_a_file_too_corrupt_to_read()
    {
        // Nothing else ever removes these, and they are read on every open.
        Write("junk", "{ broken");
        string junk = Path.Combine(InboxStore.Dir, "junk.json");
        File.SetLastWriteTimeUtc(junk, DateTime.UtcNow.AddDays(-30));

        InboxStore.Prune(maxAgeDays: 14);

        Assert.False(File.Exists(junk));
    }

    [Fact]
    public void Pruning_a_directory_that_does_not_exist_is_a_no_op()
    {
        Directory.Delete(InboxStore.Dir, recursive: true);

        InboxStore.Prune();

        Assert.Equal(0, InboxStore.UnansweredCount());
    }
}
