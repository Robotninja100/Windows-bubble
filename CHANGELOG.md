# Changelog

Notable changes, in the format of [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Nothing has been released yet, so everything so far sits under Unreleased. The
first tagged version will be `v1.0.0`; see [the release section of the
README](README.md#cutting-a-release).

## What counts as a breaking change

`config.json` is a file users are invited to hand-edit, which makes it part of
the interface, not an implementation detail. So a **major** version is required
to rename or remove a setting, to change what an existing value means, or to
restructure the file such that an older one no longer loads correctly. Adding a
setting is a minor change — a file that does not mention it keeps the default,
which is why nothing so far has needed a migration.

Every config carries a `SchemaVersion`; a file written before that existed reads
as version 1, which is what those files are.

The gesture, the default hotkey and the key map are equally part of the
interface. Changing what an existing key does is breaking; adding a key is not.

---

## [Unreleased]

### Added

- **The settings window is the app now.** It opens on an **Overview** page:
  tiles for how often the bubble has been opened (and by which route), how many
  actions it has run, your favourite segment, your streak of consecutive days
  and an estimate of the time saved; a ranking of your busiest segments; a set
  of facts drawn from the same numbers; and a row per group of settings that
  takes you to it. The sidebar carries the app's identity, an icon per page and
  a live line of statistics; each page has a title and a sentence saying what it
  is for; the live preview appears only on the three pages that change how the
  bubble looks; the footer says when something is unsaved, and closing the
  window from the title bar asks before discarding it. **About** is new too:
  version, where every file lives, and buttons that open them.
- **Usage counters**, in `%APPDATA%\CursorBubble\usage.json` beside the
  settings. Opens by route, actions per segment and per kind, cancels, generated
  scripts, and about a year of active days for the streak. Local only — there is
  no network code behind them — switchable off under **General**, and deletable
  in one click under **About**.
- **A keyboard path to the bubble.** `Ctrl+Alt+Space` — configurable under
  Settings → General — opens it centred on the screen. Arrows and `Tab` walk the
  ring, `1`–`9` jump straight to a shortcut, `Home`/`End` go to the ends, `Enter`
  runs, `Esc` cancels, and pressing the hotkey again closes it. It opens with
  nothing selected, so a reflexive `Enter` cancels rather than running something
  arbitrary.
- **Screen-reader support.** Every field is associated with its caption, the
  four status lines are announced as live regions, and the bubble exposes itself
  through a focus proxy that announces "Open Documents, 2 of 7" per selection.
- **A visible keyboard focus ring.** Every button, list item and text box used a
  custom template with no focused state, so keyboard focus was invisible.
- **Liquid-glass rendering.** Layered per segment: a thin tinted body, two
  clipped edge bands that fake refraction around the rim, a specular gloss and a
  crisp outline. The desktop blur now follows the segments rather than one big
  circle, so the gaps and the centre stay sharp.
- **Open and hover animations.** The bubble unfurls like an umbrella — each
  petal springs from 30% to full size with an overshoot, twisted back a few
  degrees and untwisting, staggered so they open in sequence. The hovered
  segment lifts towards the viewer. Closing stays instant.
- **AI script generation**, **the Claude Code inbox**, and **built-in icons**
  from the Windows system icon font.
- **A test suite** (none existed), a **file log** under
  `%APPDATA%\CursorBubble\logs`, **crash handlers** on all three channels, an
  **MIT licence**, and a **tag-triggered release workflow** producing a
  versioned exe with a SHA-256 checksum.
- **Coverage measurement**, **CodeQL**, **Dependabot** and a **vulnerable
  package gate** in CI.
- `SECURITY.md` with a written-down threat model, and `CONTRIBUTING.md`.

### Fixed

- **Linking to Claude Code could destroy `~/.claude/settings.json`.** A file
  that existed but did not parse fell through to an empty object which was then
  written straight over the top, silently replacing the user's entire Claude
  Code configuration. Such a file is now refused, never overwritten; every write
  is backed up first and done atomically.
- **An `&` in a segment's Arguments ran a second program.** Command lines were
  built by string concatenation, so `cmd` treated it as a separator. Arguments
  now go through `ProcessStartInfo.ArgumentList`.
- **The bubble's badge could over-count.** It counted files while the responder
  list skipped records that failed to parse.
- **Link and unlink never showed their result.** Both wrote a status message and
  then immediately overwrote it, so neither success nor any error was visible.
- **Every Windows line break became a double space** in the inbox snippet.
- **A notch instead of a fillet on the inner corners** of each segment, from
  approximating the tangent angle.
- Corner rounding no longer collapses to sharp wedges when the fillets stop
  fitting; it shrinks to fit instead.

### Changed

- **Everything user-facing is now English** (it was Dutch).
- **`AcceptsTab` removed from the AI dialog's script box.** Tab inserted a
  character with no way back out — a keyboard trap (WCAG 2.1.2).
- **Layout and style sliders snap to their ticks.** The read-outs already
  rounded, so a config could say `200.37` while the UI insisted it was `200`.
  Existing fractional values round on the first save.
- **The Arguments field is ignored for an inline command target**, which already
  carries its own arguments. The settings window now says so instead of silently
  dropping it.
- Inbox records older than a fortnight are swept at startup; previously nothing
  ever removed them.
- Warnings are errors, with analyzers at `Recommended`.
