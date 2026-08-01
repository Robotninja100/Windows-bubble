using System.Diagnostics;
using CursorBubble.Actions;
using CursorBubble.Config;
using Xunit;

namespace CursorBubble.Tests;

public class CommandLineTests
{
    [Fact]
    public void Nothing_in_nothing_out()
    {
        Assert.Empty(CommandLine.Split(null));
        Assert.Empty(CommandLine.Split(""));
        Assert.Empty(CommandLine.Split("   "));
    }

    [Fact]
    public void Plain_words_split_on_whitespace()
    {
        Assert.Equal(new[] { "one", "two", "three" }, CommandLine.Split("one  two\tthree"));
    }

    [Fact]
    public void A_quoted_path_with_spaces_stays_one_argument()
    {
        Assert.Equal(
            new[] { "-o", @"C:\Program Files\thing.txt" },
            CommandLine.Split("-o \"C:\\Program Files\\thing.txt\""));
    }

    [Fact]
    public void An_escaped_quote_is_a_literal_quote()
    {
        // The user types:  "say \"hi\""
        Assert.Equal(
            new[] { "say \"hi\"" },
            CommandLine.Split("\"say \\\"hi\\\"\""));
    }

    [Fact]
    public void Trailing_backslashes_survive()
    {
        // A directory argument ending in a separator is the everyday case.
        Assert.Equal(new[] { @"C:\dir\" }, CommandLine.Split(@"C:\dir\"));

        // Quoted, the separator has to be doubled — the Windows rule.
        Assert.Equal(new[] { @"C:\dir\" }, CommandLine.Split("\"C:\\dir\\\\\""));
    }

    [Fact]
    public void An_explicitly_empty_argument_is_kept()
    {
        Assert.Equal(new[] { "-x", "" }, CommandLine.Split("-x \"\""));
    }
}

public class ActionRunnerTests
{
    private static SegmentConfig Segment(ActionType action, string target, string? arguments = null)
        => new() { Label = "Test", Action = action, Target = target, Arguments = arguments };

    [Fact]
    public void Opening_a_path_uses_the_shell()
    {
        ProcessStartInfo psi = ActionRunner.BuildStartInfo(
            Segment(ActionType.OpenPath, @"C:\Users\me\Documents"));

        Assert.Equal(@"C:\Users\me\Documents", psi.FileName);
        Assert.True(psi.UseShellExecute);
    }

    [Fact]
    public void A_PowerShell_script_runs_through_the_File_switch()
    {
        ProcessStartInfo psi = ActionRunner.BuildStartInfo(
            Segment(ActionType.RunScript, @"C:\scripts\backup.ps1"));

        Assert.Equal("powershell.exe", psi.FileName);
        Assert.Equal(
            new[] { "-ExecutionPolicy", "Bypass", "-File", @"C:\scripts\backup.ps1" },
            psi.ArgumentList);
    }

    [Fact]
    public void A_batch_file_runs_through_cmd()
    {
        ProcessStartInfo psi = ActionRunner.BuildStartInfo(
            Segment(ActionType.RunScript, @"C:\scripts\go.bat", "one two"));

        Assert.Equal("cmd.exe", psi.FileName);
        Assert.Equal(new[] { "/c", @"C:\scripts\go.bat", "one", "two" }, psi.ArgumentList);
    }

    [Fact]
    public void An_ampersand_in_the_arguments_is_one_argument_not_a_second_command()
    {
        // The regression this file exists for. Building the command line by
        // string concatenation made cmd treat '&' as a separator, so a segment
        // with these arguments launched the script *and* calc.
        ProcessStartInfo psi = ActionRunner.BuildStartInfo(
            Segment(ActionType.RunScript, @"C:\scripts\go.bat", "x & calc"));

        Assert.Equal(new[] { "/c", @"C:\scripts\go.bat", "x", "&", "calc" }, psi.ArgumentList);

        // ArgumentList quotes each entry, so the '&' is passed to the script as
        // a literal argument rather than reaching the shell as an operator.
        Assert.Empty(psi.Arguments);
    }

    [Fact]
    public void A_path_with_spaces_survives_as_one_argument()
    {
        ProcessStartInfo psi = ActionRunner.BuildStartInfo(
            Segment(ActionType.RunScript, @"C:\Program Files\my script.ps1"));

        Assert.Contains(@"C:\Program Files\my script.ps1", psi.ArgumentList);
    }

    [Fact]
    public void An_inline_command_is_passed_to_cmd_as_written()
    {
        ProcessStartInfo psi = ActionRunner.BuildStartInfo(
            Segment(ActionType.RunScript, "powershell -Command Get-Date"));

        Assert.Equal("cmd.exe", psi.FileName);
        Assert.Equal("/c powershell -Command Get-Date", psi.Arguments);
        Assert.True(psi.CreateNoWindow);
    }

    [Fact]
    public void An_inline_command_ignores_the_arguments_field()
    {
        // Deliberate: the target is already a whole command line, and appending
        // a second field to the end of it is what produced the surprises.
        ProcessStartInfo psi = ActionRunner.BuildStartInfo(
            Segment(ActionType.RunScript, "powershell -Command Get-Date", "& calc"));

        Assert.Equal("/c powershell -Command Get-Date", psi.Arguments);
    }

    [Fact]
    public void Launching_a_program_passes_its_arguments_separately()
    {
        ProcessStartInfo psi = ActionRunner.BuildStartInfo(
            Segment(ActionType.LaunchProgram, "notepad.exe", "\"C:\\my notes.txt\" -x"));

        Assert.Equal("notepad.exe", psi.FileName);
        Assert.Equal(new[] { @"C:\my notes.txt", "-x" }, psi.ArgumentList);
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        ProcessStartInfo psi = ActionRunner.BuildStartInfo(
            Segment(ActionType.OpenPath, "%SystemRoot%"));

        Assert.DoesNotContain('%', psi.FileName);
    }

    [Theory]
    [InlineData(ActionType.OpenPath)]
    [InlineData(ActionType.LaunchProgram)]
    [InlineData(ActionType.RunScript)]
    public void An_empty_target_is_reported_rather_than_launched(ActionType action)
    {
        Assert.Throws<InvalidOperationException>(
            () => ActionRunner.BuildStartInfo(Segment(action, "   ")));

        // Run turns that into a message the tray can show, not a crash.
        string? error = ActionRunner.Run(Segment(action, "   "));
        Assert.NotNull(error);
        Assert.Contains("Test", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_inbox_action_does_not_belong_here()
    {
        // App routes it to the responder window before ActionRunner sees it;
        // reaching this point at all is a bug, so it says so rather than
        // silently doing nothing the way it used to.
        Assert.Throws<InvalidOperationException>(
            () => ActionRunner.BuildStartInfo(Segment(ActionType.ClaudeInbox, "")));
    }
}
