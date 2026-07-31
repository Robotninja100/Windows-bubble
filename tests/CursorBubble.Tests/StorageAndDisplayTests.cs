using CursorBubble.Ai;
using CursorBubble.ClaudeCode;
using CursorBubble.Native;
using Xunit;

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
        Assert.Equal(key, DataProtection.Unprotect(stored));
    }

    [Fact]
    public void Empty_stays_empty_rather_than_becoming_a_blob()
    {
        Assert.Equal("", DataProtection.Protect(null));
        Assert.Equal("", DataProtection.Protect(""));
        Assert.Equal("", DataProtection.Unprotect(null));
        Assert.Equal("", DataProtection.Unprotect(""));
    }

    [Fact]
    public void A_plain_text_key_from_an_older_config_is_returned_as_is()
    {
        // Before DPAPI the key was stored in the clear; those configs must keep working.
        Assert.Equal("sk-plain-text", DataProtection.Unprotect("sk-plain-text"));
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
}
