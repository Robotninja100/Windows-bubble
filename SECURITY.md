# Security

## Reporting a vulnerability

Report privately through
[GitHub's private vulnerability reporting](https://github.com/Robotninja100/Windows-bubble/security/advisories/new)
rather than by opening an issue.

Expect an acknowledgement within **7 days** and an assessment within **30**.
This is a spare-time project, not a product with a security team behind it —
those are honest targets, not a contractual SLA. If a report is valid, the fix
and the advisory are published together.

Please do not test against machines that are not yours.

## What this app can do

CursorBubble is a shortcut launcher, so the interesting question is not "can it
be exploited" so much as "what does it already have permission to do". It runs
as the logged-in user, with no elevation, and:

| Capability | Where | Why |
| --- | --- | --- |
| Global mouse hook (`WH_MOUSE_LL`) | `Native/MouseHook.cs` | Detects the right-drag gesture. Sees the position and buttons of every mouse event in the session — **not** keystrokes. |
| Global hotkey (`RegisterHotKey`) | `Input/HotkeyManager.cs` | Opens the bubble from the keyboard. Deliberately not a keyboard hook: that would be a keylogger. |
| Launches processes | `Actions/ActionRunner.cs` | The entire point of the app. Runs what the user configured, as the user. |
| Writes PowerShell to disk | `Ai/ScriptStore.cs` | Stores AI-generated scripts under `%APPDATA%\CursorBubble\scripts`. |
| Reads and writes the clipboard | `Native/WindowInput.cs` | Delivers a reply to a Claude Code terminal. The previous contents are restored afterwards. |
| Sends keystrokes to another window | `Native/WindowInput.cs` | Same. Only after the target window is confirmed to be in the foreground. |
| Edits `~/.claude/settings.json` | `ClaudeCode/HookInstaller.cs` | Registers the Claude Code hooks. **Somebody else's configuration file** — see below. |
| Stores an API key | `Native/DataProtection.cs` | Encrypted with Windows DPAPI, per user. |
| Registry autostart entry | `Tray/AutostartManager.cs` | `HKCU\...\Run`, only when the user enables it. |
| One outbound network call | `Ai/ScriptGenerator.cs` | `api.anthropic.com`, only when the user asks for a generated script. Nothing else leaves the machine. |

## Where the risk actually is

**The Claude Code settings file.** It is the only file the app writes that it
does not own, and losing it would cost the user configuration this app knows
nothing about. It is therefore read strictly (a file that exists but does not
parse is refused, never overwritten), backed up before every write, and written
via a temp file and an atomic move.

**Running what the configuration says.** A segment can name any executable or
command line, so anyone who can edit `%APPDATA%\CursorBubble\config.json` can
run code as the user next time that segment is picked. This is not privilege
escalation — writing to that path already requires being that user — but it is
worth stating plainly. Arguments are passed through `ArgumentList` so a stray
`&` cannot smuggle in a second command; an inline command line is run as
written, because that is what the user asked for.

**AI-generated scripts.** The model's output is shown in full and must be read
and accepted before it is saved, and it is never run from the dialog. Treat a
generated script the way you would treat one from a stranger on the internet,
because in the relevant sense that is what it is.

**The API key.** Encrypted with DPAPI, so it is readable only by the same
Windows account. That protects it from other users of the machine and from
anyone reading `config.json` off a backup; it does **not** protect it from
malware already running as you.

## What is deliberately not claimed

- The executable is **not code-signed**. SmartScreen will warn on first run.
  Releases publish a SHA-256 checksum, which proves the file was not altered in
  transit — it does not prove who built it.
- There is no sandbox, no privilege separation and no anti-tampering. A
  shortcut launcher that runs arbitrary commands cannot meaningfully defend
  itself against the user's own account being compromised.
- No telemetry, no crash reporting, no analytics. Logs stay on the machine, in
  `%APPDATA%\CursorBubble\logs`.

## Supported versions

The latest release only. There are no maintenance branches.
