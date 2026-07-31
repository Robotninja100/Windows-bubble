# CursorBubble

A Windows app that shows a **radial glass menu ("bubble") around your mouse
cursor** on a gesture. In the bubble you pick a shortcut that opens a program or
file, or runs a script. Layout, transparency and actions are all configured
inside the app.

![The bubble around the cursor, with glass segments](docs/glass-preview.png)

> The image above is a **rendering** of the shapes and the glass effect as the
> app draws them (same geometry and gradients), not a Windows screenshot. The
> icons are stand-ins: in the app they come from the Windows system font
> **Segoe Fluent Icons**, which only exists on Windows.

## How it works

1. **Hold the right mouse button** and **then click left**.
2. The glass bubble appears around your cursor.
3. **Move** to a segment and **release the mouse button** to run that action.
4. Releasing in the centre ("Cancel") closes the bubble without doing anything.

An ordinary right click (without a left click alongside it) still works
normally: the context menu appears as soon as you release the right button.

The app runs in the background with an **icon in the system tray**. From the
tray menu you open **Settings**, toggle **Start with Windows**, or exit.

## Settings

Right-click (or double-click) the tray icon → **Settings…**:

- **General** — autostart and a short how-to.
- **Layout** — outer/inner radius, start angle, the gap between segments and how
  rounded their corners are.
- **Segments** — add, edit, remove and reorder segments. Per segment: name,
  action, target, arguments and an icon. Pick an icon from the list (from the
  Windows font Segoe Fluent Icons) or point at your own `.png`/`.ico`, which
  takes priority.
- **Style** — glass blur (acrylic) on/off, **animate on open**, glass opacity,
  and the glass, accent and text colours.

The settings window itself has the same glass look: a real acrylic backdrop on
**Windows 11**, a flat dark theme on **Windows 10**.

Everything has a **live preview**. Settings are stored in
`%APPDATA%\CursorBubble\config.json`.

### Action types

| Action | Target field | Example |
|--------|--------------|---------|
| Open (file/folder/URL) | path, folder or URL | `https://google.com`, `%USERPROFILE%\Documents` |
| Launch program | path to an `.exe` (+ arguments) | `calc.exe`, `notepad.exe` |
| Run script / command | `.bat`/`.cmd`/`.ps1`, or a command | `powershell -Command "..."` |

`%VAR%` environment variables in the target are expanded automatically.

### Writing a script with AI

Instead of writing a script yourself you can have one generated:

1. Enter your **Anthropic API key** under **Settings → AI** (create one at
   console.anthropic.com). Pick a cheaper model if you like.
2. Go to **Segments**, select a segment and click **✨ Generate script with AI…**.
3. Describe in plain language what the script should do (e.g. *"back up my
   documents to D:\Backups"*).
4. Claude writes a PowerShell script. **Read it through**, then click **Use
   this** — it is saved to `%APPDATA%\CursorBubble\scripts\` and attached to the
   segment.

The script is never run automatically; it only runs when you pick that segment
in the bubble. The API key is stored **encrypted** in `config.json` using the
Windows Data Protection API (DPAPI) — only your Windows account on this PC can
decrypt it. Each generation costs a small amount of API credit.

## Claude Code inbox

Working with several Claude Code sessions (in VS Code or separate terminals)?
You can answer them from the bubble.

1. **Settings → Claude Code → Link Claude Code.** This adds `Stop` and
   `Notification` hooks to your `~/.claude/settings.json`. Restart running
   sessions so the hooks take effect.
2. As soon as a session **stops** or **has a question**, you get a tray
   notification and a **badge** with the count on the "Claude Code" segment in
   the bubble.
3. Pick that segment → a glass window opens with the pending sessions. The
   question/status plus context at the top, a **text field** below it.
4. Type your reply → **Send** → CursorBubble puts it on the clipboard, brings
   the right window to the front, and pastes + sends it (Enter).

**How it works / limitations:**
- Detection runs through Claude Code hooks; the hook passes the last message
  (`last_assistant_message`) or the notification directly — the transcript is
  not parsed (that format is unstable).
- Sending replies back is **not officially supported** by Claude Code;
  CursorBubble simulates keystrokes in the owning window. That window is found
  through the session's parent process (falling back to the project folder name
  in the window title, and only on an **unambiguous** match against a
  terminal/editor window).
- It **never types blind**: Ctrl+V and Enter only go out once the target window
  is confirmed to be in the foreground. If that fails, the session stays pending
  and your reply is left on the clipboard for you to paste — so an Enter never
  lands in the wrong window.
- Your previous clipboard contents are restored after a successful send.
- A brief focus switch to the target window is visible.
- Structured multiple-choice questions arrive as text; you type your answer.

## Downloading a ready-made .exe

On every commit, GitHub Actions builds the app on a real Windows runner and
publishes a **self-contained `CursorBubble.exe`** (no .NET install needed):

GitHub → **Actions** tab → the topmost (green) **Build** run → **Artifacts** at
the bottom → download **CursorBubble** and unzip.

## Building and running

> Requires **Windows 10/11** and the **.NET 8 SDK** (WPF only builds on Windows).

```powershell
# In the folder containing CursorBubble.sln
dotnet run --project src/CursorBubble
```

To produce a standalone `.exe`:

```powershell
dotnet publish src/CursorBubble -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true
```

The `.exe` then lives in
`src/CursorBubble/bin/Release/net8.0-windows/win-x64/publish/`.

## How it is built

- **C# / WPF (.NET 8)** — a native Windows overlay.
- A **global mouse hook** (`WH_MOUSE_LL`) recognises the gesture and suppresses
  the context menu; an ordinary right click is replayed with `SendInput`.
- A **transparent, top-most, click-through overlay window**; hit-testing runs
  off the global cursor position, so a held mouse button is not a problem.
- **Liquid glass** — each segment is a rounded shape with a thin, transparent
  body and a rim of light that runs along its edge like refraction (a wide soft
  band plus a narrow bright line), with a specular gloss on top.
- **Frosted glass** via `DwmEnableBlurBehindWindow`, with a region that follows
  **the segments themselves** rather than one big circle: the desktop is blurred
  only underneath the glass, while the gaps and the centre stay sharp.
- **DPI-aware** (Per-Monitor v2); the bubble is placed on the cursor in physical
  pixels.

### Known limitations

- The blur behind the bubble uses `DwmEnableBlurBehindWindow`. On some Windows
  versions that blur is subtle or disabled; in that case turn **Glass blur
  (acrylic)** off — the segments are then tinted flatly instead of blurred. The
  bubble is always visible as glass either way.
- While the app runs, every right click is held very briefly and replayed on
  release (needed to detect the gesture).
- Right-dragging (dragging with the right button held) is not passed through.

## Project layout

```
src/CursorBubble/
  App.xaml(.cs)            startup, tray + hook
  Native/                  P/Invoke, mouse hook + gesture, blur helper
  Overlay/                 transparent window + radial glass drawing
  Settings/                settings window with live preview + AI dialog
  Ai/                      script generation via the Claude API + storage
  ClaudeCode/              hook handler, inbox storage, hook installer
  Responder/               glass window for answering sessions
  Config/                  model + JSON storage
  Actions/                 running actions
  Tray/                    system tray icon + autostart
```
