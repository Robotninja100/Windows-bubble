using CursorBubble.Ai;
using Xunit;

namespace CursorBubble.Tests;

/// <summary>
/// Parsing what the model sends back. The model is asked for bare JSON but does
/// not always oblige, and a parsing slip here is what puts a broken script in
/// front of the user.
/// </summary>
public class ScriptGeneratorTests
{
    [Fact]
    public void Reads_a_clean_json_object()
    {
        GeneratedScript result = ScriptGenerator.ParseGeneratedJson(
            """{"script":"Get-Date","explanation":"Prints the date.","warnings":""}""");

        Assert.Equal("Get-Date", result.Script);
        Assert.Equal("Prints the date.", result.Explanation);
        Assert.Equal("", result.Warnings);
    }

    [Fact]
    public void Survives_markdown_fences_and_chatter_around_the_json()
    {
        GeneratedScript result = ScriptGenerator.ParseGeneratedJson(
            """
            Sure! Here you go:
            ```json
            {"script":"Get-ChildItem","explanation":"Lists files.","warnings":"None."}
            ```
            Let me know if you want changes.
            """);

        Assert.Equal("Get-ChildItem", result.Script);
        Assert.Equal("None.", result.Warnings);
    }

    [Fact]
    public void Missing_explanation_and_warnings_are_tolerated()
    {
        GeneratedScript result = ScriptGenerator.ParseGeneratedJson("""{"script":"Get-Date"}""");

        Assert.Equal("Get-Date", result.Script);
        Assert.Equal("", result.Explanation);
        Assert.Equal("", result.Warnings);
    }

    [Fact]
    public void Whitespace_around_the_script_is_trimmed()
    {
        GeneratedScript result = ScriptGenerator.ParseGeneratedJson(
            """{"script":"  Get-Date  ","explanation":"  x  ","warnings":"  y  "}""");

        Assert.Equal("Get-Date", result.Script);
        Assert.Equal("x", result.Explanation);
        Assert.Equal("y", result.Warnings);
    }

    [Theory]
    [InlineData("no json here at all")]
    [InlineData("")]
    [InlineData("{ this is not valid json")]
    [InlineData("""{"explanation":"I forgot the script."}""")]
    [InlineData("""{"script":"   "}""")]
    public void Anything_without_a_usable_script_is_reported_not_returned_empty(string text)
    {
        // Better a clear message than silently attaching an empty script to a segment.
        Assert.Throws<InvalidOperationException>(() => ScriptGenerator.ParseGeneratedJson(text));
    }

    [Fact]
    public void Picks_the_text_block_out_of_an_api_response()
    {
        GeneratedScript result = ScriptGenerator.ParseResponse(
            """
            {"content":[{"type":"text","text":"{\"script\":\"Get-Date\"}"}],"stop_reason":"end_turn"}
            """);

        Assert.Equal("Get-Date", result.Script);
    }

    [Fact]
    public void Skips_leading_thinking_blocks()
    {
        GeneratedScript result = ScriptGenerator.ParseResponse(
            """
            {"content":[
              {"type":"thinking","thinking":"considering options"},
              {"type":"text","text":"{\"script\":\"Get-Date\"}"}
            ],"stop_reason":"end_turn"}
            """);

        Assert.Equal("Get-Date", result.Script);
    }

    [Fact]
    public void A_refusal_is_explained_rather_than_parsed_as_a_script()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ScriptGenerator.ParseResponse(
            """{"content":[],"stop_reason":"refusal"}"""));

        Assert.Contains("declined", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_response_with_no_text_block_is_reported()
    {
        Assert.Throws<InvalidOperationException>(() => ScriptGenerator.ParseResponse(
            """{"content":[{"type":"thinking","thinking":"..."}],"stop_reason":"end_turn"}"""));
    }
}
