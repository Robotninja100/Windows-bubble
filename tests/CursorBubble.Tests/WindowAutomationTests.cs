using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CursorBubble.ClaudeCode;
using CursorBubble.Config;
using CursorBubble.Responder;
using CursorBubble.Settings;
using Xunit;
// WPF's implicit usings bring in System.Windows.Shapes.Path.
using Path = System.IO.Path;

namespace CursorBubble.Tests;

/// <summary>
/// Hosts a WPF <see cref="Application"/> on a dedicated STA thread so windows
/// can be constructed in a test.
///
/// The Application is not optional scaffolding: every window resolves
/// <c>{StaticResource FocusRing}</c>, which lives in App.xaml's
/// Application.Resources, and StaticResource is resolved while the XAML is
/// parsed. Without an Application in the process, constructing any of these
/// windows throws.
///
/// Nothing is ever shown. These tests are about what the XAML declares, which
/// is knowable from the object graph alone and needs no desktop.
/// </summary>
public sealed class WpfFixture : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;

    public WpfFixture()
    {
        using var ready = new ManualResetEventSlim();

        _thread = new Thread(() =>
        {
            var app = new App();
            app.InitializeComponent();      // loads App.xaml's resources
            _dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(30));
    }

    /// <summary>Run <paramref name="work"/> on the UI thread and return its result.</summary>
    public T On<T>(Func<T> work) => _dispatcher!.Invoke(work);

    public void Dispose() => _dispatcher?.InvokeShutdown();
}

[CollectionDefinition(Name)]
public sealed class WpfCollection : ICollectionFixture<WpfFixture>
{
    public const string Name = "wpf";
}

/// <summary>
/// What the three windows declare about themselves to a screen reader.
///
/// The point of this file is one specific failure that nothing else catches: an
/// <c>AutomationProperties.LabeledBy</c> binding whose <c>ElementName</c> does
/// not resolve. That compiles perfectly cleanly, produces no warning, and the
/// only symptom is a screen reader saying "slider" where it should say "Radius
/// (outer)" — which nobody notices unless they are using one.
///
/// These tests read the bindings off the constructed object graph, so they
/// prove the names resolve and point at the intended caption. They do not, and
/// cannot, prove that Narrator reads any of it out loud; that stays on the
/// manual list.
/// </summary>
[Collection(WpfCollection.Name)]
public class WindowAutomationTests
{
    private readonly WpfFixture _wpf;

    public WindowAutomationTests(WpfFixture wpf) => _wpf = wpf;

    /// <summary>Caption name → the text a screen reader should announce for the field.</summary>
    private static readonly (string Field, string Caption, string Text)[] SettingsLabels =
    {
        ("HotkeyBox",          "HotkeyCaption",         "Keyboard shortcut for the bubble"),
        ("OuterRadiusSlider",  "OuterRadiusCaption",    "Radius (outer)"),
        ("InnerRadiusSlider",  "InnerRadiusCaption",    "Radius (inner)"),
        ("StartAngleSlider",   "StartAngleCaption",     "Start angle"),
        ("GapSlider",          "GapCaption",            "Gap between segments"),
        ("CornerSlider",       "CornerCaption",         "Corner rounding"),
        ("LabelBox",           "LabelCaption",          "Name"),
        ("IconGlyphBox",       "IconCaption",           "Icon"),
        ("ActionBox",          "ActionCaption",         "Action"),
        ("TargetBox",          "TargetCaption",         "Target (path, folder, URL or command)"),
        ("ArgumentsBox",       "ArgumentsCaption",      "Arguments (optional)"),
        ("IconBox",            "IconImageCaption",      "Icon image (optional)"),
        ("TintOpacitySlider",  "TintOpacityCaption",    "Glass opacity"),
        ("TintColorBox",       "TintColorCaption",      "Glass colour"),
        ("HighlightColorBox",  "HighlightColorCaption", "Accent colour"),
        ("LabelColorBox",      "LabelColorCaption",     "Text colour"),
        ("AiApiKeyBox",        "AiApiKeyCaption",       "API key"),
        ("AiModelBox",         "AiModelCaption",        "Model"),
    };

    [Fact]
    public void Every_settings_field_is_labelled_by_the_caption_next_to_it()
    {
        _wpf.On(() =>
        {
            var window = new SettingsWindow(AppConfig.CreateDefault());

            foreach ((string field, string caption, string text) in SettingsLabels)
            {
                AssertLabelledBy(window, field, caption, text);
            }

            return true;
        });
    }

    [Fact]
    public void The_responder_labels_its_list_and_its_reply_box()
    {
        _wpf.On(() =>
        {
            using var inbox = new TempInbox();
            var window = new ResponderWindow();

            AssertLabelledBy(window, "SessionsList", "SessionsCaption", "Pending sessions");
            AssertLabelledBy(window, "ReplyBox", "ReplyCaption", "Your reply");
            return true;
        });
    }

    [Fact]
    public void The_AI_dialog_labels_its_description_and_its_script()
    {
        _wpf.On(() =>
        {
            var window = new AiScriptDialog("sk-not-a-real-key", "claude-opus-5");

            AssertLabelledBy(window, "DescriptionBox", "DescriptionCaption",
                "Describe what the script should do");
            AssertLabelledBy(window, "ScriptBox", "ScriptCaption",
                "Script — read this before you use it:");
            return true;
        });
    }

    [Fact]
    public void No_LabeledBy_binding_anywhere_points_at_a_name_that_does_not_exist()
    {
        // The list above is maintained by hand, so it can fall behind. This
        // sweeps whatever is actually in the XAML, which catches a pair added
        // later and never added here.
        _wpf.On(() =>
        {
            AssertEveryLabelResolves(new SettingsWindow(AppConfig.CreateDefault()));

            using (new TempInbox())
                AssertEveryLabelResolves(new ResponderWindow());

            AssertEveryLabelResolves(new AiScriptDialog("sk-not-a-real-key", "claude-opus-5"));
            return true;
        });
    }

    [Fact]
    public void The_settings_window_has_every_label_pair_this_test_knows_about()
    {
        // Guards the other direction: a pair silently deleted from the XAML.
        int found = _wpf.On(() => CountLabelBindings(new SettingsWindow(AppConfig.CreateDefault())));

        Assert.Equal(SettingsLabels.Length, found);
    }

    [Fact]
    public void The_buttons_that_are_only_a_symbol_say_what_they_do()
    {
        // "⟳" and "✨" are announced as unreadable codepoints without a name,
        // and two buttons in the segment panel are both literally "Browse…".
        _wpf.On(() =>
        {
            var settings = new SettingsWindow(AppConfig.CreateDefault());
            AssertName(settings, "BrowseTargetBtn", "Browse for a target file");
            AssertName(settings, "BrowseIconBtn", "Browse for an icon image");
            AssertName(settings, "AiGenerateBtn", "Generate a script with AI");
            AssertName(settings, "AddBtn", "Add segment");
            AssertName(settings, "NavList", "Settings sections");

            using (new TempInbox())
            {
                var responder = new ResponderWindow();
                AssertName(responder, "RefreshBtn", "Refresh the session list");
                AssertName(responder, "MessageBox", "Message from Claude Code");
            }

            return true;
        });
    }

    [Fact]
    public void The_bubble_hides_its_internals_from_a_screen_reader()
    {
        // RadialMenuControl is Path geometry plus TextBlocks for the labels, so
        // left alone it dumps every segment label — each behind an unreadable
        // private-use glyph — into the tree, including in the settings preview
        // where it is purely decorative.
        _wpf.On(() =>
        {
            var control = new Overlay.RadialMenuControl();
            control.Build(AppConfig.CreateDefault());

            System.Windows.Automation.Peers.AutomationPeer peer =
                System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(control);

            Assert.NotNull(peer);
            Assert.Empty(peer.GetChildren());
            return true;
        });
    }

    // ---- helpers -------------------------------------------------------------

    private static void AssertLabelledBy(FrameworkElement window, string field, string caption, string text)
    {
        object? target = window.FindName(field);
        Assert.True(target is not null, $"{field} does not exist in {window.GetType().Name}.");

        object? label = window.FindName(caption);
        Assert.True(label is TextBlock,
            $"{field} is labelled by '{caption}', which is not a TextBlock in {window.GetType().Name}. " +
            "A LabeledBy binding to a name that does not resolve fails silently at runtime.");

        Assert.Equal(text, ((TextBlock)label!).Text);

        Binding? binding = BindingOperations.GetBinding(
            (DependencyObject)target!, AutomationProperties.LabeledByProperty);

        Assert.True(binding is not null, $"{field} has no LabeledBy binding.");
        Assert.Equal(caption, binding!.ElementName);
    }

    private static void AssertName(FrameworkElement window, string element, string expected)
    {
        object? target = window.FindName(element);
        Assert.True(target is not null, $"{element} does not exist in {window.GetType().Name}.");
        Assert.Equal(expected, AutomationProperties.GetName((DependencyObject)target!));
    }

    private static void AssertEveryLabelResolves(FrameworkElement window)
    {
        foreach (DependencyObject element in Descendants(window))
        {
            Binding? binding = BindingOperations.GetBinding(element, AutomationProperties.LabeledByProperty);
            if (binding?.ElementName is not { Length: > 0 } name)
                continue;

            Assert.True(window.FindName(name) is TextBlock,
                $"LabeledBy on a {element.GetType().Name} in {window.GetType().Name} names " +
                $"'{name}', which resolves to nothing.");
        }
    }

    private static int CountLabelBindings(FrameworkElement window) =>
        Descendants(window).Count(e =>
            BindingOperations.GetBinding(e, AutomationProperties.LabeledByProperty) is not null);

    /// <summary>
    /// Every element in the window's logical tree, including inside panels that
    /// are Collapsed — they are constructed regardless, which is what makes this
    /// checkable without showing anything.
    /// </summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object? child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node)
                continue;

            yield return node;

            foreach (DependencyObject deeper in Descendants(node))
                yield return deeper;
        }
    }

    /// <summary>Points the inbox at an empty temp directory for the duration.</summary>
    private sealed class TempInbox : IDisposable
    {
        private readonly string _original;
        private readonly string _dir;

        public TempInbox()
        {
            _original = InboxStore.Dir;
            _dir = Path.Combine(Path.GetTempPath(), "cursorbubble-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            InboxStore.Dir = _dir;
        }

        public void Dispose()
        {
            InboxStore.Dir = _original;
            try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        }
    }
}
