# Contributing

## The one thing that surprises people

**WPF only builds on Windows.** There is no cross-platform fallback, no
headless mode, and `dotnet build` on macOS or Linux fails immediately. If you
are not on Windows, GitHub Actions is your compiler: push to a branch and read
the run. That is not a workaround, it is the workflow — a good part of this
codebase was written that way.

The practical consequence is that anything worth being sure about should be
reachable from a test, because a test is the only thing that runs unattended.
That is why the geometry, the hotkey parsing, the ring navigation and the
announcement strings all live in classes with no WPF types in them —
`Overlay/RadialMath.cs`, `Input/HotkeySpec.cs`, `Overlay/MenuAnnouncement.cs`,
`Actions/CommandLine.cs`. When you add logic, ask whether it can go somewhere
like that before you put it in a code-behind.

## Building

```powershell
dotnet restore CursorBubble.sln
dotnet build CursorBubble.sln -c Release
dotnet test CursorBubble.sln
```

.NET 8 SDK, Windows 10 1809 or later. Windows 11 gets real acrylic; Windows 10
falls back to a flat tint, and both paths are meant to look deliberate.

## Warnings are errors

The app project builds with `TreatWarningsAsErrors` and analyzers at
`Recommended`. This is not negotiable in a pull request — if an analyzer objects,
fix the code rather than suppressing the rule. The two suppressions that do
exist are in `.editorconfig` with the reason written next to them; add to that
list only when the analyzer is genuinely wrong, and say why.

Two `.editorconfig` rules exist because of specific pain:

- `IDE1006` (naming) is disabled **only** under `src/CursorBubble/Native/`, so
  Win32 constants can keep their documented spelling. `MOD_ALT` anywhere else
  fails the build, and that is intentional.
- `CA1838` is disabled there too: `StringBuilder` is the documented marshalling
  for the two `GetWindowText` calls.

## Tests

xunit, in `tests/CursorBubble.Tests`. Conventions worth matching:

- **Name the test after the behaviour, not the method.**
  `A_file_that_is_not_valid_JSON_is_refused_rather_than_overwritten`, not
  `LoadTest2`.
- **Say why in a comment when the test is a regression.** Several tests here
  exist because something specific went wrong; the comment is what stops the
  test being "simplified" back into the bug.
- Tests that touch the filesystem set the store's path property to a temp
  directory in the constructor and restore it in `Dispose` — see
  `HookInstallerFileTests`.

Coverage is measured in CI and reported in the job summary. There is no
threshold yet; do not add one without a discussion, and do not lower the number
without saying so.

## Style

`.editorconfig` covers the mechanical part. Beyond that:

- **Comments explain why, not what.** The code already says what it does. If a
  line looks odd and is correct, the comment says what would happen without it.
- Match the density of the file you are in. This codebase comments the
  non-obvious heavily and the obvious not at all.
- Prefer a named local over a clever expression, and a small pure function over
  either.

## Pull requests

Describe the behaviour change, not the diff. If you made a judgement call —
kept something, dropped something, chose one of two reasonable approaches — say
which and why; that is the part a reviewer cannot reconstruct.

State plainly what you could not verify. "Compiles and the tests pass, but I
have not run it on Windows 10" is useful. Silence on the point is not.

## Things that need a person

CI cannot drive a window or a screen reader, so these still need someone on
Windows and are worth mentioning in a PR that touches them: the acrylic blur,
the focus ring against dark chrome and glass, Narrator announcements, hotkey
registration from a full-screen app, and multi-monitor or mixed-DPI layout.
