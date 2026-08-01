using System.IO;
using System.Xml.Linq;
using Xunit;
// WPF's implicit usings bring in System.Windows.Shapes.Path.
using Path = System.IO.Path;

namespace CursorBubble.Tests;

/// <summary>
/// What the three windows declare about themselves to a screen reader.
///
/// The point of this file is one specific failure that nothing else catches: an
/// <c>AutomationProperties.LabeledBy</c> binding whose <c>ElementName</c> does
/// not resolve. It compiles perfectly cleanly, produces no warning, and the only
/// symptom is a screen reader saying "slider" where it should say "Radius
/// (outer)" — which nobody notices unless they are using one. Twenty-two of
/// those were previously trusted on inspection alone.
///
/// The XAML is read as XML rather than by constructing the windows. That is not
/// a compromise for this particular check: <c>ElementName</c> resolution *is* a
/// lookup in the file's namescope, so a name declared by an <c>x:Name</c> in the
/// same file is exactly what makes the binding work. Doing it this way needs no
/// WPF Application, no STA thread and no desktop, which matters when CI is the
/// only machine that ever runs it.
///
/// What it cannot show is that a screen reader speaks any of it. That stays on
/// the manual list in the README.
/// </summary>
public class WindowAutomationTests
{
    private const string Settings = "Settings/SettingsWindow.xaml";
    private const string Responder = "Responder/ResponderWindow.xaml";
    private const string AiDialog = "Settings/AiScriptDialog.xaml";

    /// <summary>Field → the caption naming it → the text a screen reader should announce.</summary>
    public static TheoryData<string, string, string, string> Labels => new()
    {
        { Settings,  "HotkeyBox",         "HotkeyCaption",         "Keyboard shortcut for the bubble" },
        { Settings,  "OuterRadiusSlider", "OuterRadiusCaption",    "Radius (outer)" },
        { Settings,  "InnerRadiusSlider", "InnerRadiusCaption",    "Radius (inner)" },
        { Settings,  "StartAngleSlider",  "StartAngleCaption",     "Start angle" },
        { Settings,  "GapSlider",         "GapCaption",            "Gap between segments" },
        { Settings,  "CornerSlider",      "CornerCaption",         "Corner rounding" },
        { Settings,  "LabelBox",          "LabelCaption",          "Name" },
        { Settings,  "IconGlyphBox",      "IconCaption",           "Icon" },
        { Settings,  "ActionBox",         "ActionCaption",         "Action" },
        { Settings,  "TargetBox",         "TargetCaption",         "Target (path, folder, URL or command)" },
        { Settings,  "ArgumentsBox",      "ArgumentsCaption",      "Arguments (optional)" },
        { Settings,  "IconBox",           "IconImageCaption",      "Icon image (optional)" },
        { Settings,  "TintOpacitySlider", "TintOpacityCaption",    "Glass opacity" },
        { Settings,  "TintColorBox",      "TintColorCaption",      "Glass colour" },
        { Settings,  "HighlightColorBox", "HighlightColorCaption", "Accent colour" },
        { Settings,  "LabelColorBox",     "LabelColorCaption",     "Text colour" },
        { Settings,  "AiApiKeyBox",       "AiApiKeyCaption",       "API key" },
        { Settings,  "AiModelBox",        "AiModelCaption",        "Model" },
        { Responder, "SessionsList",      "SessionsCaption",       "Pending sessions" },
        { Responder, "ReplyBox",          "ReplyCaption",          "Your reply" },
        { AiDialog,  "DescriptionBox",    "DescriptionCaption",    "Describe what the script should do" },
        { AiDialog,  "ScriptBox",         "ScriptCaption",         "Script — read this before you use it:" },
    };

    [Theory]
    [MemberData(nameof(Labels))]
    public void Each_field_is_labelled_by_the_caption_next_to_it(
        string file, string field, string caption, string text)
    {
        XDocument xaml = Xaml.Load(file);

        XElement target = Xaml.ByName(xaml, field)
            ?? throw new Xunit.Sdk.XunitException($"{file} has no element named '{field}'.");

        string? boundTo = Xaml.LabeledByElementName(target);
        Assert.True(boundTo is not null, $"{field} in {file} has no LabeledBy binding.");
        Assert.Equal(caption, boundTo);

        XElement label = Xaml.ByName(xaml, caption)
            ?? throw new Xunit.Sdk.XunitException(
                $"{field} is labelled by '{caption}', which nothing in {file} declares. " +
                "A LabeledBy binding to a name that does not resolve fails silently at runtime.");

        Assert.Equal("TextBlock", label.Name.LocalName);
        Assert.Equal(text, Xaml.TextOf(label));
    }

    [Theory]
    [InlineData(Settings)]
    [InlineData(Responder)]
    [InlineData(AiDialog)]
    public void No_LabeledBy_binding_points_at_a_name_that_does_not_exist(string file)
    {
        // The table above is maintained by hand, so it can fall behind. This
        // sweeps whatever is actually in the file, which catches a pair added
        // later and never added there.
        XDocument xaml = Xaml.Load(file);

        foreach (XElement element in xaml.Descendants())
        {
            if (Xaml.LabeledByElementName(element) is not { } name)
                continue;

            XElement? label = Xaml.ByName(xaml, name);
            Assert.True(label is not null,
                $"LabeledBy on a {element.Name.LocalName} in {file} names '{name}', " +
                "which resolves to nothing.");
            Assert.Equal("TextBlock", label!.Name.LocalName);
        }
    }

    [Theory]
    [InlineData(Settings, 18)]
    [InlineData(Responder, 2)]
    [InlineData(AiDialog, 2)]
    public void Every_label_pair_this_test_knows_about_still_exists(string file, int expected)
    {
        // Guards the other direction: a pair silently deleted from the XAML.
        int found = Xaml.Load(file).Descendants().Count(e => Xaml.LabeledByElementName(e) is not null);

        Assert.Equal(expected, found);
    }

    [Theory]
    // "⟳" and "✨" are announced as unreadable codepoints without a name, and
    // two buttons in the segment panel are both literally "Browse…".
    [InlineData(Settings, "BrowseTargetBtn", "Browse for a target file")]
    [InlineData(Settings, "BrowseIconBtn", "Browse for an icon image")]
    [InlineData(Settings, "AiGenerateBtn", "Generate a script with AI")]
    [InlineData(Settings, "AddBtn", "Add segment")]
    [InlineData(Settings, "RemoveBtn", "Remove the selected segment")]
    [InlineData(Settings, "UpBtn", "Move the selected segment up")]
    [InlineData(Settings, "DownBtn", "Move the selected segment down")]
    [InlineData(Settings, "NavList", "Settings sections")]
    [InlineData(Settings, "SegmentsList", "Segments")]
    [InlineData(Responder, "RefreshBtn", "Refresh the session list")]
    [InlineData(Responder, "MessageBox", "Message from Claude Code")]
    [InlineData(Responder, "SendBtn", "Send reply")]
    [InlineData(Responder, "DismissBtn", "Dismiss this session")]
    public void Controls_without_a_caption_name_themselves(string file, string element, string expected)
    {
        XElement target = Xaml.ByName(Xaml.Load(file), element)
            ?? throw new Xunit.Sdk.XunitException($"{file} has no element named '{element}'.");

        Assert.Equal(expected, (string?)target.Attribute("AutomationProperties.Name"));
    }

    [Theory]
    [InlineData(Settings)]
    [InlineData(Responder)]
    [InlineData(AiDialog)]
    public void Every_button_template_recognises_access_keys(string file)
    {
        // RecognizesAccessKey defaults to false on a bare ContentPresenter, so
        // without it Content="_Save" renders a literal underscore instead of a
        // mnemonic. It is the easiest thing in the accessibility work to lose.
        XDocument xaml = Xaml.Load(file);

        var buttonTemplates = xaml.Descendants()
            .Where(e => e.Name.LocalName == "ControlTemplate"
                     && (string?)e.Attribute("TargetType") == "Button")
            .ToList();

        Assert.NotEmpty(buttonTemplates);

        foreach (XElement template in buttonTemplates)
        {
            XElement presenter = Assert.Single(
                template.Descendants().Where(e => e.Name.LocalName == "ContentPresenter"));

            Assert.Equal("True", (string?)presenter.Attribute("RecognizesAccessKey"));
        }
    }

    [Theory]
    [InlineData(Settings)]
    [InlineData(Responder)]
    [InlineData(AiDialog)]
    public void Every_custom_template_shows_keyboard_focus(string file)
    {
        // A templated control that repaints its own border needs the focused
        // state in the template too, or keyboard focus is simply invisible.
        XDocument xaml = Xaml.Load(file);

        var templates = xaml.Descendants()
            .Where(e => e.Name.LocalName == "ControlTemplate"
                     && e.Attribute("TargetType") is not null)
            .ToList();

        Assert.NotEmpty(templates);

        foreach (XElement template in templates)
        {
            string targetType = (string?)template.Attribute("TargetType") ?? "?";

            bool hasFocusTrigger = template.Descendants()
                .Any(e => e.Name.LocalName == "Trigger"
                       && (string?)e.Attribute("Property") == "IsKeyboardFocused");

            Assert.True(hasFocusTrigger,
                $"The {targetType} template in {file} has no IsKeyboardFocused trigger, " +
                "so keyboard focus is invisible in it.");
        }
    }

    [Fact]
    public void The_script_box_is_not_a_keyboard_trap()
    {
        // AcceptsTab made Tab insert a character with no way back out of the
        // field, which is WCAG 2.1.2. It must stay gone.
        XElement box = Xaml.ByName(Xaml.Load(AiDialog), "ScriptBox")!;

        Assert.Null(box.Attribute("AcceptsTab"));
    }

    [Fact]
    public void The_overlay_names_itself_for_a_screen_reader()
    {
        // Narrator reads a window's Title on activation, and the overlay had
        // none — it would have appeared, taken focus and said nothing at all.
        XDocument xaml = Xaml.Load("Overlay/RadialMenuWindow.xaml");
        XElement window = xaml.Root!;

        Assert.False(string.IsNullOrWhiteSpace((string?)window.Attribute("Title")));
        Assert.False(string.IsNullOrWhiteSpace((string?)window.Attribute("AutomationProperties.Name")));

        // The announcement proxy must stay Visible: a collapsed element has no
        // automation peer, so it could be neither focused nor announced.
        XElement proxy = Xaml.ByName(xaml, "AnnounceProxy")!;
        Assert.Null(proxy.Attribute("Visibility"));
        Assert.Equal("True", (string?)proxy.Attribute("Focusable"));
    }
}

/// <summary>Reads the app's XAML off disk and answers questions about it.</summary>
internal static class Xaml
{
    private static readonly string SourceRoot = FindSourceRoot();

    public static XDocument Load(string relativePath)
    {
        string full = Path.Combine(SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"XAML not found: {full}");
        return XDocument.Load(full);
    }

    /// <summary>The element with this <c>x:Name</c>, or null.</summary>
    public static XElement? ByName(XDocument xaml, string name) =>
        xaml.Descendants().FirstOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && (string)a == name));

    /// <summary>
    /// The <c>ElementName</c> of this element's LabeledBy binding, or null if it
    /// has no such binding.
    /// </summary>
    public static string? LabeledByElementName(XElement element)
    {
        string? binding = (string?)element.Attribute("AutomationProperties.LabeledBy");
        if (binding is null)
            return null;

        const string marker = "ElementName=";
        int start = binding.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        string rest = binding[(start + marker.Length)..].TrimEnd('}', ' ');
        int end = rest.IndexOfAny(new[] { ',', ' ' });
        return end < 0 ? rest : rest[..end];
    }

    /// <summary>The Text attribute of a TextBlock, with XAML line wrapping collapsed.</summary>
    public static string TextOf(XElement element)
    {
        string text = (string?)element.Attribute("Text") ?? element.Value;
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Walk up from the test assembly to the directory holding the solution, so
    /// this works from any output path without the csproj copying files around.
    /// </summary>
    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CursorBubble.sln")))
            dir = dir.Parent;

        Assert.True(dir is not null,
            $"Could not find CursorBubble.sln above {AppContext.BaseDirectory}.");

        return Path.Combine(dir!.FullName, "src", "CursorBubble");
    }
}
