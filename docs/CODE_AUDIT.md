# CursorBubble — code quality audit

Audit of the default branch (`claude/windows-cursor-bubble-shortcuts-zk9tqt`,
commit `2c3345e`), against the standard of a codebase that has to stand as a
foundation: correct, safe to change, verifiable, and supportable in the field.

- **Scope:** every tracked file — 35 files, ~4.7k lines (C#, XAML, csproj, CI).
- **Method:** full static read of every source file. **The project was not
  compiled or run**: WPF targets `net8.0-windows` and only builds on Windows,
  and no .NET SDK is present in the audit environment. Every finding below is
  derived from reading the code, not from a failing build or a repro. Items
  marked *(unverified at runtime)* are the ones where that distinction matters
  most.
- **Open PR #1** (`claude/code-review-glass-look-1yzoee`) already fixes a
  significant share of what an audit of this branch turns up. Those items are
  listed separately in [Appendix A](#appendix-a--already-fixed-in-pr-1) rather
  than repeated as open work, and the items below are the ones that are **still
  open even after PR #1 merges** — verified file by file against that branch.

---

## Verdict

For a ~4.7k-line single-developer Windows utility, the code is **above average
and in places genuinely good**. The things that usually rot first are healthy:

- Every type and non-obvious method carries an XML doc comment that explains
  *why*, not just *what* — `MouseHook`'s state machine, `WindowInput`'s refusal
  to type blind, `AcrylicHelper`'s region clipping.
- Folder structure maps cleanly onto responsibilities (`Native/`, `Overlay/`,
  `Config/`, `Actions/`, `ClaudeCode/`), with no circular reach-through.
- `<Nullable>enable</Nullable>` is on, and the code actually respects it.
- The P/Invoke layer is careful where carelessness is normal: the hook delegate
  is rooted against GC (`MouseHook.cs:35`), the GDI region is freed in a
  `finally` (`AcrylicHelper.cs:34-38`), the `INPUT` struct layout is right for
  x64, and injected events are filtered so the replayed click cannot re-enter
  the state machine (`MouseHook.cs:88`).
- The riskiest feature in the app — synthesising Ctrl+V + Enter into someone
  else's terminal — is the most conservatively written part of it
  (`WindowInput.cs:109-128`). It verifies foreground twice and refuses to type
  rather than guess. That is the right call and it was not the easy one.

It is **not** yet at the standard the surrounding claim implies, and the gap is
not in the code that exists — it is in what is missing around it. On this
branch: no tests, no logging, no crash handling, no licence, and a CI job that
compiles but verifies nothing. Two of the bugs below silently destroy user
data. That is the honest summary: a well-written prototype-grade codebase, not
yet a foundation.

**Priorities:** P0 = data loss or silent wrong behaviour, fix before anything
else. P1 = reliability in the field. P2 = security. P3+ = structural and
process debt, in descending order.

---

> **Status:** the P0 band (#1-#4, #6) and the two reliability findings that
> touching it exposed (#11, #12) are **fixed in this branch** — see
> [Appendix B](#appendix-b--fixed-in-this-branch). #5 was left alone on purpose;
> the entry explains why. Everything from P1 down is still open.

## P0 — Correctness and data loss

### 1. DPAPI failure silently stores the API key in the clear *and* corrupts it
`Native/DataProtection.cs:43,56`

`Protect` falls back to the raw input when `CryptProtectData` fails:

```csharp
return Convert.ToBase64String(Transform(input, protect: true) ?? input);
```

So `config.json` receives `base64(plaintext key)` — obfuscated, not encrypted,
while README and the settings UI both promise DPAPI encryption.

The read path then compounds it. `Unprotect` base64-decodes successfully,
`CryptUnprotectData` fails on plaintext, and the `null` branch returns
`stored` — **the base64 string**, not the decoded key:

```csharp
byte[]? plain = Transform(blob, protect: false);
return plain is null ? stored : Encoding.UTF8.GetString(plain);
```

Net effect on that path: the key is stored unprotected, and the AI feature then
fails with "invalid API key" because it sends base64 to Anthropic. Both halves
of a security promise fail at once, silently.

**Fix:** make `Protect` throw or return a failure the caller must surface —
never degrade to plaintext without telling the user. Distinguish "not
DPAPI-protected" from "DPAPI failed" on read.
*(Untouched by PR #1 — `DataProtection.cs` is not in that diff.)*

### 2. A corrupt config silently destroys every user setting
`Config/ConfigStore.cs:40-49`

Any read or parse error falls through to `CreateDefault()` **and immediately
writes it back**, overwriting the file that failed to parse. A truncated write,
a bad hand-edit, or a half-flushed file after a power loss costs the user every
segment, colour and layout value, with no backup and no message.

**Fix:** on parse failure, rename the bad file to `config.corrupt-<timestamp>.json`,
tell the user via the tray, and only then start from defaults.

### 3. No atomic writes anywhere
`Config/ConfigStore.cs:55`, `ClaudeCode/InboxStore.cs:30`,
`ClaudeCode/HookInstaller.cs:144`

All three use `File.WriteAllText` straight onto the destination. A crash, a
power cut or an antivirus lock mid-write truncates the file — which for
`config.json` walks straight into finding #2, and for `~/.claude/settings.json`
damages a file this app does not own.

**Fix:** write to `<name>.tmp` in the same directory, flush, then
`File.Move(tmp, dest, overwrite: true)`. One shared helper for all three.

### 4. The inbox retry loop is a symptom of the same gap
`ClaudeCode/InboxStore.cs:38-60`

`TryLoad` retries six times at 60 ms because the writer may not have flushed.
That is a workaround for the non-atomic write in #3. With atomic replace the
reader either sees the old file or the new one, never a partial one, and the
loop (and its 360 ms of blocking — see #8) disappears.

### 5. Releasing the mouse anywhere on screen fires an action — *by design, worth revisiting*
`Overlay/RadialMenuControl.cs:304-318`

`HitTest` rejects the inner dead zone but has **no outer bound**:

```csharp
double dist = Math.Sqrt(dx * dx + dy * dy);
if (dist < _inner) return -1;   // dead zone = cancel
// ...no upper check; angle alone decides
```

Flick the cursor to the far corner of the monitor, release, and whichever
segment lies on that bearing runs — which can be "run script". Combined with
the work-area clamping in `ShowAt` (`RadialMenuWindow.xaml.cs:101-102`), where
the bubble is deliberately *not* centred on the cursor near a screen edge, the
distance between "what I pointed at" and "what ran" can be most of the screen.

**This is a deliberate decision, not an oversight.** PR #1 extracts the same
logic into `RadialMath.SegmentAt` and documents it explicitly at
`RadialMath.cs:28`: *"Distance beyond the outer radius deliberately still
selects: a radial menu…"* — which is indeed how most radial menus behave, and
it makes fast flick gestures work without precision.

Left unchanged here for that reason. It is listed because the reasoning
deserves to be weighed against this app's specific payload: a radial menu that
switches brush size can afford a generous catchment, and one whose segments run
arbitrary PowerShell is a different risk. If the current behaviour stands, the
README's "Bekende beperkingen" section should say so, since nothing visible on
screen suggests that releasing 900 px away will act.

**If revisited:** return `-1` beyond roughly `_outer * 2`, generous enough that
a deliberate flick still lands, bounded enough that an abandoned gesture on the
far side of the monitor cancels.

### 6. Truncated AI scripts are presented as complete
`Ai/ScriptGenerator.cs:47,79-110`

`max_tokens = 4096` is hardcoded and `stop_reason` is only checked for
`"refusal"`. A response that stops at `"max_tokens"` yields a half-written
PowerShell script that is shown for review, saved to `%APPDATA%`, and attached
to a segment as if it were whole. A script cut mid-statement is exactly the
kind of thing that half-completes a backup.

**Fix:** check `stop_reason == "max_tokens"` and refuse the result with a clear
message. Raise `max_tokens` and make it configurable.

---

## P1 — Reliability in the field

### 7. A low-level mouse hook that Windows drops is never reinstalled
`Native/MouseHook.cs:57-68`

Windows silently unhooks a `WH_MOUSE_LL` callback whose handler exceeds
`LowLevelHooksTimeout` (~300 ms by default). This handler runs on the UI thread
and raises events that queue dispatcher work; under load, or during a GC pause,
or while a slow `Process.Start` is still on the thread (#10), it can exceed
that. When it does, the app keeps running, the tray icon stays put, and the
gesture simply stops working forever with no error.

**Fix:** a watchdog — periodically verify the hook is alive and reinstall it if
not — plus keep the callback to state mutation only. *(unverified at runtime)*

### 8. `MouseHook.Install()` throws into `OnStartup` with no handler
`Native/MouseHook.cs:66-67`, `App.xaml.cs:71`

Hook installation failure raises `InvalidOperationException` from `OnStartup`.
There is no `DispatcherUnhandledException` handler on this branch, so the app
dies at launch with a Windows crash dialog and no explanation.

### 9. No logging and no crash handling
`App.xaml.cs` (whole file)

Neither `DispatcherUnhandledException`, `AppDomain.UnhandledException`, nor
`TaskScheduler.UnobservedTaskException` is subscribed, and there is no log file
anywhere. For a background tray app with no main window, this is the single
biggest supportability gap: when a user reports "the bubble stopped appearing",
there is nothing to look at.

### 10. Actions run on the UI thread
`App.xaml.cs:88`, `Actions/ActionRunner.cs:48,61,93`

`ActionRunner.Run` calls `Process.Start` with `UseShellExecute = true` directly
on the dispatcher. ShellExecute can block for hundreds of milliseconds — COM
initialisation, a cold shell extension, a network path — and while it does, the
UI thread is stalled, which feeds directly into the hook timeout in #7.

**Fix:** run actions on the thread pool and marshal only the error back.

### 11. The FileSystemWatcher has no `Error` handler and can die silently
`App.xaml.cs:109-128`

`_inboxWatcher` subscribes `Created` and `Changed` but not `Error`. When the
internal buffer overflows — a burst of hook writes from several concurrent
Claude Code sessions is exactly that shape — the watcher raises `Error` and
stops. Nothing re-arms it. The tray notifications quietly stop for the rest of
the session. `InternalBufferSize` is also left at the 8 KB default.

### 12. The watcher thread is blocked for up to 360 ms per event
`App.xaml.cs:137`, `ClaudeCode/InboxStore.cs:38-60`

`OnInboxFileEvent` runs on the watcher's own thread and calls `TryLoad`, which
sleeps 6 × 60 ms. Every millisecond spent there is a millisecond of events
accumulating in the buffer that #11 has no handler for.

**Fix:** hand the path to the thread pool immediately and return.

### 13. Inbox records are never pruned
`ClaudeCode/InboxStore.cs` (whole file)

Records are removed only on explicit send or dismiss. A terminal that is closed
mid-session leaves its record behind forever: it inflates the badge, appears in
the responder pointing at a dead window handle, and accumulates across months.

**Fix:** drop records older than N days on load, and drop those whose
`WindowHandle` no longer resolves to a live window.

### 14. The `--hook` path boots the whole WPF application
`App.xaml.cs:34-43`

`OnStartup` is already inside `Application`, so **every** Claude Code `Stop` and
`Notification` event pays for WPF initialisation in a self-contained,
single-file exe — just to read stdin, parse a small JSON object and write a
file. On an active session that is one heavyweight process launch per turn.

**Fix:** an explicit `[STAThread] static Main` that handles `--hook` and returns
before `Application` is ever constructed.
*(Still present on PR #1.)*

### 15. `HookHandler.Run()` returns an exit code that is discarded
`ClaudeCode/HookHandler.cs:14`, `App.xaml.cs:40`

The `int` return is ignored and `Shutdown()` is called with no argument, so the
process always exits 0 — including when the hook failed. Claude Code cannot
distinguish a working hook from a broken one.

### 16. 25 of 32 `catch` blocks swallow the exception without binding it

Across `HookHandler`, `InboxStore`, `ConfigStore`, `DataProtection`,
`WindowInput`, `RadialMenuControl`, `TrayIcon` and `App`. Several are correct
by design — a hook must never disrupt its session — but with no logging (#9)
they are collectively a blindfold. Every one should at minimum write to a log
before returning.

### 17. Clipboard restore races the paste it is restoring after
`Responder/ResponderWindow.xaml.cs:119-135`

`SendReplyAsync` returns right after `SendEnter()`, and the caller immediately
restores the previous clipboard. A terminal that processes `WM_PASTE`
asynchronously can read the clipboard after it has been reset, sending the
user's *previous* clipboard content into the session — followed by the Enter
that was already queued.

**Fix:** confirm delivery (or wait a conservative interval) before restoring.
*(unverified at runtime)*

### 18. `keybd_event` is deprecated and ignores already-held modifiers
`Native/WindowInput.cs:294-306`

Superseded by `SendInput` since Windows 2000, and it does not clear modifier
state: if the user is holding Shift when delivery fires, `Ctrl+V` arrives as
`Ctrl+Shift+V`, which in most terminals is a different command entirely.

**Fix:** `SendInput` with scan codes, after explicitly releasing live modifiers.

### 19. `GetWindowLong`/`SetWindowLong` instead of the `Ptr` variants, unchecked
`Native/NativeMethods.cs:102-106`, `Overlay/RadialMenuWindow.xaml.cs:63-65`

The 32-bit variants happen to work for `GWL_EXSTYLE`, but `SetLastError = true`
is declared and the return value is never checked. A failed `GetWindowLong`
returns 0, and the code then ORs styles onto 0 and writes that back — silently
clearing every other extended style on the overlay.

### 20. No handling of display, DPI or session changes
`Overlay/RadialMenuWindow.xaml.cs`

DPI is sampled per `ShowAt`, which is right, but nothing subscribes to
`SystemEvents.DisplaySettingsChanged` or `SessionSwitch`. Unplugging a monitor
while the bubble is visible, or locking the workstation mid-gesture, leaves the
state machine in `MenuActive` with no release event coming.

### 21. Elevated windows are an undocumented dead zone

UIPI prevents a non-elevated process from receiving hook events over an
elevated window, and from focusing or sending input to one. The gesture will
appear broken over an admin terminal, and Claude Code replies to an elevated
session can never be delivered. Neither is mentioned in the README or surfaced
in the UI.

---

## P2 — Security

### 22. Script arguments are concatenated into a command line unquoted
`Actions/ActionRunner.cs:71-91`

```csharp
Arguments = $"-ExecutionPolicy Bypass -File \"{target}\" {args}"
Arguments = $"/c \"{target}\" {args}"
Arguments = $"/c {target} {args}"
```

`args` is never quoted or escaped, and a `"` inside `target` breaks out of its
quotes. This is user-owned configuration rather than a privilege boundary, but
`Target` is run through `Environment.ExpandEnvironmentVariables` first
(`ActionRunner.cs:98`), so any process that can set an environment variable in
the user's session can influence the resulting command line.
*(Fixed on PR #1 via `Actions/CommandLine.cs` — listed here because it is the
highest-severity item on the default branch.)*

### 23. `-ExecutionPolicy Bypass` on every `.ps1`
`Actions/ActionRunner.cs:75`

Convenient, and deliberate for AI-generated scripts — but it means the app is a
general-purpose way to run unsigned PowerShell that ignores machine policy. It
should be a documented decision in `SECURITY.md`, not an implementation detail.

### 24. DPAPI is used without an entropy parameter
`Native/DataProtection.cs:77-78`

`pOptionalEntropy` is `IntPtr.Zero`, so **any** process running as the same user
can decrypt the stored API key by reading `config.json` and calling
`CryptUnprotectData`. Passing an application-specific entropy blob raises the
bar from "any code running as you" to "code that also knows CursorBubble's
entropy".

### 25. The API key lives in managed strings for the process lifetime
`Config/AppConfig.cs:101-105`, `Settings/SettingsWindow.xaml.cs:133,323`,
`Settings/AiScriptDialog.xaml.cs:16`

`PasswordBox.Password` materialises the key as an immutable `string`, which is
then copied into `AppConfig`, into the dialog, and into an HTTP header. None can
be zeroed; all sit in the heap until GC, and land in any crash dump.

Separately, `AiApiKey_Changed` (`SettingsWindow.xaml.cs:320-324`) re-runs DPAPI
encryption **on every keystroke** in the password box.

### 26. `HookInstaller` writes a file it does not own, with no backup
`ClaudeCode/HookInstaller.cs:122-145`

`Load()` returns a **fresh empty object** when `~/.claude/settings.json` is
corrupt or unreadable, and `Install()`/`Uninstall()` then unconditionally
`Save()` — writing that empty object over the user's entire Claude Code
configuration. A transient read failure costs them every setting in that file.
*(Fixed on PR #1.)*

### 27. Session IDs are sanitised into a filename with a possible silent collision
`ClaudeCode/InboxStore.cs:115-121`

`PathFor` strips everything but alphanumerics, `-` and `_`, so two distinct
session IDs can map to the same file, with the second overwriting the first.
UUID session IDs make this practically unreachable today, but the collapse is
silent and the input comes from outside the app. There is also no length cap
(`MAX_PATH`).

**Fix:** hash the raw ID and use the hash as the filename.

### 28. No supply-chain or secret scanning on this branch
`.github/workflows/build.yml`

No CodeQL, no dependency review, no `dotnet list package --vulnerable`, no
Dependabot, no pinned action SHAs (`actions/checkout@v4` is a moving tag). The
published artifact is also unsigned, so every user meets a SmartScreen warning
and has no way to verify provenance.
*(CodeQL and Dependabot land in PR #1; signing does not.)*

---

## P3 — Architecture

### 29. No presentation layer — all logic is in code-behind
`Settings/SettingsWindow.xaml.cs` (481 lines), `Responder/ResponderWindow.xaml.cs`

`SettingsWindow.xaml.cs` is the largest file in the project and mixes config
mutation, validation, preview rendering, file dialogs, hook installation and AI
orchestration. None of it is reachable from a test without hosting WPF.

**Fix:** extract a `SettingsViewModel` holding the working config and the
commands. The window keeps only `InitializeComponent` and wiring. This is the
single change that most improves testability.

### 30. Config models do not implement `INotifyPropertyChanged`
`Config/AppConfig.cs:10-76`

Compensated for by hand: `SegmentsList.Items.Refresh()` and explicit
`RebuildPreview()` calls scattered across `SettingsWindow.xaml.cs:292-293,
314, 362-363`. Every new field means remembering to add another refresh call —
the classic way a settings screen starts showing stale values.

### 31. The preview rebuilds its entire visual tree on every keystroke and slider tick
`Settings/SettingsWindow.xaml.cs:281-294,460-465`,
`Overlay/RadialMenuControl.cs:50-145`

`Build()` clears `Children` and reconstructs every `Path`, `TextBlock`,
`StackPanel`, brush and `DropShadowEffect` from scratch. It is called per
character typed in the label box and per pixel of slider travel. The same
happens per gesture in `ShowAt` whenever the inbox badge count changes
(`RadialMenuWindow.xaml.cs:85-90`).

**Fix:** separate "rebuild geometry" from "update properties", and debounce.

### 32. Config values are not validated or clamped on load
`Config/ConfigStore.cs:29-49`, `Overlay/RadialMenuControl.cs:57-60`

Clamping happens in `Build()`, at render time, and only partially:
`_outer = Math.Max(40, ...)` has **no upper bound**, so a hand-edited
`OuterRadius` of 100000 produces a window larger than the desktop.
`StartAngle`, `TintOpacity` and `SegmentOpacity` are unclamped on load.

**Fix:** a `Validate()`/`Normalize()` pass in `ConfigStore.Load`, so every
consumer downstream can trust the object.

### 33. Visual segment gaps and hit-test regions disagree
`Overlay/RadialMenuControl.cs:113-118` vs `304-318`

`Build` insets each wedge by `halfGap`, `HitTest` ignores the gap entirely. The
visible space between two segments is live, and selects one of its neighbours.
That is defensible (no dead zones) but it is undocumented, and it means the
highlight is the only truthful indicator of what will run.

### 34. `AutostartManager.IsEnabled()` is dead code
`Tray/AutostartManager.cs:22-26`

Never called; the checkbox state comes from config instead, so the registry and
the config can silently disagree — a user who removes the Run entry by hand
still sees the toggle checked, and `Apply` writes it back on next launch.

### 35. A `TextBox` named `MessageBox` shadows `System.Windows.MessageBox`
`Responder/ResponderWindow.xaml:146`, `Responder/ResponderWindow.xaml.cs:70`

Inside `ResponderWindow`, `MessageBox.Show(...)` no longer resolves to the WPF
dialog — it resolves to the field and fails to compile, or worse, resolves to
something unexpected after a refactor. `SettingsWindow` calls `MessageBox.Show`
freely, so the two files disagree on what that identifier means.

**Fix:** rename to `MessageText`.

### 36. `ScriptGenerator` takes a `CancellationToken` that no caller ever passes
`Ai/ScriptGenerator.cs:38`, `Settings/AiScriptDialog.xaml.cs:52`

The parameter exists and threads through correctly, but the dialog calls
`GenerateAsync(_apiKey, _model, description)` with no token and no Cancel
button. A user who mistypes a prompt waits out the full 120-second timeout with
a disabled button.

### 37. No retry or `Retry-After` handling on the API client
`Ai/ScriptGenerator.cs:61-76`

429 and 5xx map straight to a terminal error message. The `Retry-After` header
is ignored. A single transient failure loses the request.

### 38. The AI model list is hardcoded with no free-text option
`Settings/SettingsWindow.xaml.cs:29-34`

Three fixed IDs. Any new model requires a rebuild.

---

## P4 — Testing

### 39. There is no test project on this branch

Zero tests for ~4.7k lines. This is the finding that turns every other one into
a permanent risk: nothing below can be fixed with confidence, and nothing fixed
can be kept fixed. *(PR #1 adds a full `tests/CursorBubble.Tests` project — the
highest-value thing in that PR.)*

The following are pure logic and testable today with no WPF host:

40. `RadialMenuControl.HitTest` — every bearing, dead zone, segment-count
    boundary, wrap at 0°/360°, and the missing outer bound from #5.
41. `RadialMenuControl` geometry helpers — `PointOnCircle`,
    `ClockwiseAngleFromTop`, `Mod` with negative input, `WithAlpha` clamping.
42. `ScriptGenerator.ParseGeneratedJson` — fenced markdown, prose around the
    JSON, missing keys, empty script, nested braces inside string values.
43. `ScriptGenerator.DescribeApiError` — 401/429/500, malformed error bodies.
44. `DataProtection` — round-trip, plaintext legacy value, non-base64 input, and
    specifically the failure path in #1.
45. `HookInstaller` — install into empty/absent/populated/corrupt settings,
    idempotency, uninstall leaving unrelated hooks intact, the wipe in #26.
46. `InboxStore.PathFor` — traversal attempts (`../`), empty IDs, collisions.
47. `HookHandler` — malformed stdin, missing `session_id`, `Notification` vs
    `Stop` state mapping, `last_assistant_message` vs `message` fallback.
48. `ScriptStore.Sanitize` + collision suffixing (`-2`, `-3`, …).
49. `ConfigStore` — round-trip, corrupt file behaviour (#2), unknown properties
    from a newer build, schema migration.
50. `AppConfig.CreateDefault` — a snapshot test, so the first-run experience
    cannot regress unnoticed.
51. `ActionRunner` — argument construction, once #22 is refactored to build an
    argument list rather than a string. Needs a seam over `Process.Start`.

Beyond unit tests: **no coverage measurement**, **no integration test** for the
hook → inbox → responder round trip, and **no UI smoke test** on the CI Windows
runner (the app can at least be launched headless to prove startup does not
throw).

---

## P5 — Build, CI and release

52. **CI compiles but verifies nothing** — `build.yml` has no test step, no
    analyzer gate, no format check. A green tick means "it compiled".
53. **No `TreatWarningsAsErrors`, no `EnableNETAnalyzers`, no `AnalysisMode`** in
    `CursorBubble.csproj`. Warnings accumulate unopposed.
54. **No `.editorconfig`** — no enforced style, so every contributor's IDE
    formats differently and diffs fill with noise.
55. **No `global.json`** — CI floats to whatever 8.0.x the runner has that day;
    a local build and a CI build are not the same build.
56. **No `Directory.Build.props`** — shared properties will be copy-pasted the
    moment a second project (such as the test project) exists.
57. **Actions are pinned to moving tags**, not commit SHAs.
58. **No NuGet caching** — every run restores from scratch.
59. **`on: push` with no branch or path filter** — a README-only commit triggers
    a full Windows build.
60. **No release workflow** — no tags, no GitHub Releases, no changelog
    generation. The only way to get a build is to dig through Actions artifacts.
61. **The exe is unsigned** — SmartScreen warns on every download, and there is
    no way to verify what users are running.
62. **No installer** (MSIX/WiX/Inno) and **no update mechanism**. `<Version>` is
    hardcoded at `1.0.0` in `CursorBubble.csproj:14` and has never moved, so
    every build reports the same version and field reports cannot be tied to a
    build.
63. **`.gitignore` is thin** — no `TestResults/`, `artifacts/`, `.vscode/`,
    `*.received.*`, `coverage*`.

---

## P6 — UX, accessibility, internationalisation

64. **The UI is half Dutch, half English.** Everything user-facing is Dutch
    except the tray menu, which is the app's only always-visible surface:
    `"Settings…"`, `"Start with Windows"`, `"Exit"`,
    `"CursorBubble — hold right + click left"` (`Tray/TrayIcon.cs:25,28,35,45`).
    *(PR #1 resolves this by moving everything to English.)*
65. **No localisation infrastructure** — every string is a literal in code or
    XAML. No `.resx`, no `CultureInfo` handling.
66. **The bubble is unreachable without a mouse.** No keyboard activation, no
    arrow-key navigation, no Escape to cancel. *(PR #1 adds a global hotkey and
    ring navigation.)*
67. **No screen-reader support** — no `AutomationProperties.Name` anywhere, and
    `RadialMenuControl` is a `Canvas` of raw shapes with no automation peer.
    *(PR #1 adds peers and announcements.)*
68. **`Verstuur ↵` promises an Enter that does not work.** `ReplyBox` sets
    `AcceptsReturn="True"` (`ResponderWindow.xaml:131`), so Enter inserts a
    newline; the label implies it sends. No Ctrl+Enter binding, and no Escape to
    close the window.
69. **The primary action is not visually distinct.** `SendBtn` sets
    `Background="{StaticResource Accent}"` (`ResponderWindow.xaml:138`) but the
    `Button` template hardcodes `Background="#1EFFFFFF"` on its `Border`
    (`ResponderWindow.xaml:38`) instead of using `{TemplateBinding Background}`,
    so the setting is ignored and "Verstuur" renders identically to
    "Verwijderen". `SettingsWindow` hit the same wall and worked around it by
    duplicating the whole template inline for `SaveBtn`
    (`SettingsWindow.xaml:386-401`) — the duplication is the tell.
70. **No light theme** — both windows hardcode a dark palette and ignore the OS
    preference, while the bubble itself defaults to a light glass tint. The two
    surfaces of the same app disagree.
71. **A destructive button with no confirmation** — `DismissBtn` deletes a
    pending session immediately (`ResponderWindow.xaml.cs:78-84`). No undo.
72. **No visible feedback when an action fails** beyond a 4-second tray balloon
    that Windows may suppress entirely under Focus Assist (`TrayIcon.cs:66-81`).
73. **Second-instance launch exits silently** (`App.xaml.cs:48-53`) — no
    message, and no signal to the running instance to surface its settings
    window. To the user, double-clicking the exe does nothing.
74. **Effects are applied generously** — a `DropShadowEffect` on the whole canvas
    plus one per label and glyph (`RadialMenuControl.cs:138-144,220,234`), on a
    per-pixel-transparent window. Each is a separate render pass; on integrated
    graphics this is the first thing that will stutter. *(unverified at runtime)*

---

## P7 — Documentation and repository hygiene

75. **`README.md:8` links an image that does not exist** — `docs/reference.png`
    is not in the repository. The first thing a visitor sees is a broken image.
    *(PR #1 adds `docs/glass-preview.png`.)*
76. **No `LICENSE`.** Without one the work is "all rights reserved" by default —
    nobody may legally use, fork or contribute to it. *(Added in PR #1.)*
77. **No `SECURITY.md`** — no disclosure contact, and no statement of the trust
    model for an app that installs a global input hook, runs arbitrary scripts
    and synthesises keystrokes into other applications.
78. **No `CONTRIBUTING.md`, `CODEOWNERS`, issue or PR templates.**
79. **No `CHANGELOG.md`** and no version history.
80. **No architecture document** — the README's project structure section is a
    file listing, not an explanation of the gesture state machine, the overlay's
    coordinate spaces (physical pixels vs DIPs), or the hook process lifecycle.
    Those are the three things a new contributor must understand first, and all
    three currently live only in scattered XML comments.
81. **No ADRs** for the decisions that are genuinely non-obvious and were clearly
    deliberate: swallowing and replaying right-clicks, refusing to type blind,
    per-session inbox files instead of a database, DPAPI over a credential
    manager. These read as arbitrary to a newcomer and will be "cleaned up" by
    someone who does not know why.
82. **The README's "Bekende beperkingen" section is honest and good** — it should
    be extended with the elevated-window limitation (#21) and the outer-bound
    behaviour (#5) once decided.

---

## Suggested order of work

1. **#1, #2, #3** — stop destroying user data. Small, self-contained, high value.
2. **#39 and the unit tests in #40-51** — or every fix after this is unverifiable.
   Land the test project first if PR #1 is not merging soon.
3. **#9, #7, #8, #11** — make failures visible and survivable in the field.
4. **#5, #6** — the two ways the app currently does the wrong thing confidently.
5. **#52-56** — turn CI into a gate rather than a formality.
6. **#29, #30** — the structural change that makes everything after it cheaper.
7. Everything else, by priority band.

---

## Appendix A — already fixed in PR #1

Verified against `claude/code-review-glass-look-1yzoee` file by file. These are
**not** open work; they are listed so this audit is not read as contradicting
that PR.

| Area | Covered by |
|---|---|
| Test project (~1.9k lines, 9 files) | `tests/CursorBubble.Tests/` |
| Logging + crash handlers | `Diagnostics/Log.cs`, `App.xaml.cs` |
| Command-line argument escaping (#22) | `Actions/CommandLine.cs` |
| `~/.claude/settings.json` wipe (#26) | `HookInstaller` rewrite |
| Config schema versioning + migration | `ConfigStore`, `AppConfig.SchemaVersion` |
| Inbox sweep / badge count (#13, partial) | `InboxStore` |
| Keyboard activation + ring navigation (#66) | `Input/HotkeySpec.cs`, `HotkeyManager.cs` |
| Screen-reader support (#67) | `Accessibility/`, `RadialMenuAutomationPeer.cs` |
| Warnings as errors, `.editorconfig` (#53, #54) | `csproj`, `.editorconfig` |
| CodeQL, Dependabot, coverage (#28, #52) | `.github/workflows/`, `dependabot.yml` |
| Release workflow (#60) | `.github/workflows/release.yml` |
| `LICENSE`, `SECURITY.md`, `CONTRIBUTING.md`, `CHANGELOG.md`, templates (#76-79) | repository root, `.github/` |
| Language consistency (#64) | full translation to English |
| Broken README image (#75) | `docs/glass-preview.png` |

**Still open after PR #1**, verified against that branch: #1 (`DataProtection.cs`
is untouched), #2/#3 (`ConfigStore.Save` still writes directly), #5, #6, #7, #14
(the `--hook` path still boots WPF), #17, #18 (`keybd_event` unchanged), #19,
#21, #24, #25, #27, #29, #35, #61, #62, #69, #70, #77 (as a trust-model
statement), #80, #81.

---

## Appendix B — fixed in this branch

Everything in the P0 band except #5, plus two P1 findings that fixing it
exposed. All of these were verified still open on PR #1 before being touched, so
none of it duplicates that work.

| # | Finding | Change |
|---|---|---|
| 1 | DPAPI failure stored the key in the clear and corrupted it | `Protect` throws instead of falling back to plaintext; `Unprotect` replaced by `TryUnprotect`, which reports "cannot decrypt" rather than returning base64 as if it were the key |
| 2 | A corrupt config destroyed every setting | `ConfigStore.Quarantine` moves the unreadable file to `config.corrupt-<timestamp>.json`; `LastLoadFailure` surfaces it in the tray at startup |
| 3 | No atomic writes | New `Storage/AtomicFile.cs` — temp file beside the destination, flushed to disk, then moved into place. Used by `ConfigStore`, `InboxStore`, `HookInstaller` and `ScriptStore` |
| 4 | Inbox retry loop papering over #3 | Kept, with the comment corrected to what it now actually guards against (a sharing violation during the move, not a partial read) |
| 6 | Truncated AI scripts presented as complete | `stop_reason == "max_tokens"` is now a hard error; `max_tokens` raised to 8192 |
| 11 | Watcher could die silently on buffer overflow | `Error` handler re-arms the watcher |
| 12 | Watcher thread blocked up to 360 ms per event | The read moved to the thread pool |

Two changes were forced by the above rather than chosen:

- **`FileSystemWatcher` now also subscribes to `Renamed`.** Atomic writes arrive
  as a move, which raises `Renamed` rather than `Created`. Without this the
  inbox notifications would have gone silent — the fix for #3 would have broken
  the feature it was protecting.
- **The API key is encrypted at Save, not per keystroke** (#25, partial).
  `AppConfig.AiApiKey`'s setter can now throw, and doing that on every keystroke
  would mean reporting the failure on every keystroke. `SettingsWindow` holds
  the typed value and encrypts once, reporting failure at the point the user
  asked for it to be kept — and refusing to save rather than dropping the key.
