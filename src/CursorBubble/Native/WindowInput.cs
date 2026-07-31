using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CursorBubble.Native;

/// <summary>
/// Finds the top-level window that owns a Claude Code session's terminal and
/// delivers a reply to it by focusing the window and simulating Ctrl+V + Enter.
///
/// Delivery is deliberately conservative: keystrokes are only injected once the
/// target window is confirmed to be the foreground window. If focus cannot be
/// taken, nothing is typed and the caller is told the reply was not delivered —
/// pressing Enter in whatever window happens to be focused could run a command
/// the user never intended.
/// </summary>
public static class WindowInput
{
    /// <summary>Processes that plausibly host a Claude Code session (used for title fallback).</summary>
    private static readonly HashSet<string> HostProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Code - Insiders", "VSCodium", "Cursor", "devenv",
        "WindowsTerminal", "OpenConsole", "conhost", "wt",
        "pwsh", "powershell", "cmd", "bash", "wezterm-gui", "alacritty"
    };

    // ---- capture the owning window (called from the --hook process) ----------

    /// <summary>
    /// Determine the top-level window that owns this session. Prefers the
    /// attached console window (cheap, exact); otherwise walks up the
    /// parent-process chain to the first ancestor with a visible main window
    /// (VS Code, Windows Terminal, …).
    /// </summary>
    public static void CaptureOwnerWindow(out long handle, out string title, out string processName)
    {
        handle = 0;
        title = "";
        processName = "";

        // 1. A real (visible) console window is the exact owner — no process scan needed.
        IntPtr console = GetConsoleWindow();
        if (console != IntPtr.Zero && IsWindowVisible(console))
        {
            handle = console.ToInt64();
            title = GetTitle(console);
            processName = ProcessNameForWindow(console);
            return;
        }

        // 2. Otherwise walk ancestors. Uses a direct parent-PID query per hop
        //    (a few cheap calls) rather than snapshotting every process.
        try
        {
            uint pid = (uint)Environment.ProcessId;
            DateTime childStart = SafeStartTime(Process.GetCurrentProcess());

            for (int depth = 0; depth < 16; depth++)
            {
                uint parent = GetParentProcessId(pid);
                if (parent == 0 || parent == pid)
                    break;

                Process p;
                try
                {
                    p = Process.GetProcessById((int)parent);
                }
                catch
                {
                    break; // parent already exited
                }

                using (p)
                {
                    // Guard against PID reuse: a real parent started before its child.
                    DateTime parentStart = SafeStartTime(p);
                    if (parentStart > childStart)
                        break;

                    IntPtr h = p.MainWindowHandle;
                    if (h != IntPtr.Zero && IsWindowVisible(h))
                    {
                        handle = h.ToInt64();
                        title = GetTitle(h);
                        processName = p.ProcessName;
                        return;
                    }

                    pid = parent;
                    childStart = parentStart;
                }
            }
        }
        catch
        {
            // best effort — the responder can still fall back to a title match
        }
    }

    // ---- deliver a reply -----------------------------------------------------

    /// <summary>
    /// Focus the session's window and paste + send the text already on the
    /// clipboard. Runs off the calling (UI) thread. Returns false — without
    /// typing anything — when no unambiguous window is found or focus cannot be
    /// taken, so the caller can keep the pending item and let the user paste.
    /// </summary>
    public static Task<bool> SendReplyAsync(long storedHandle, string storedTitle, string projectName)
        => Task.Run(() =>
        {
            IntPtr target = ResolveWindow(storedHandle, storedTitle, projectName);
            if (target == IntPtr.Zero)
                return false;

            if (!ForceForeground(target))
                return false; // never type blind — it could hit the wrong window

            SendCtrlV();
            Thread.Sleep(60);

            // Re-check: a dialog or another app may have stolen focus mid-paste.
            if (!IsForeground(target))
                return false;

            SendEnter();
            return true;
        });

    private static IntPtr ResolveWindow(long storedHandle, string storedTitle, string projectName)
    {
        var h = new IntPtr(storedHandle);
        if (storedHandle != 0 && IsWindow(h) && IsWindowVisible(h))
            return h;

        // The captured window is gone. Fall back to a title match, but only
        // against plausible terminal/editor windows, and only when the match is
        // unambiguous — pasting into an unrelated window would be worse than
        // failing.
        IntPtr byProject = FindUniqueHostWindow(projectName);
        if (byProject != IntPtr.Zero)
            return byProject;

        return FindUniqueHostWindow(storedTitle);
    }

    /// <summary>
    /// Find the single visible window of a known terminal/editor process whose
    /// title contains <paramref name="needle"/>. Returns zero if there is no
    /// match or more than one (ambiguous).
    /// </summary>
    private static IntPtr FindUniqueHostWindow(string needle)
    {
        if (string.IsNullOrWhiteSpace(needle) || needle.Length < 2)
            return IntPtr.Zero;

        var matches = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h))
                return true;

            string t = GetTitle(h);
            if (t.Length == 0 || !t.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;

            if (HostProcesses.Contains(ProcessNameForWindow(h)))
            {
                matches.Add(h);
                if (matches.Count > 1)
                    return false; // ambiguous — stop early
            }
            return true;
        }, IntPtr.Zero);

        return matches.Count == 1 ? matches[0] : IntPtr.Zero;
    }

    /// <summary>
    /// Bring <paramref name="target"/> to the foreground and confirm it got
    /// there. Windows' foreground lock silently ignores SetForegroundWindow from
    /// a background process, so the result must be verified rather than assumed.
    /// </summary>
    private static bool ForceForeground(IntPtr target)
    {
        if (IsIconic(target))
            ShowWindow(target, SW_RESTORE);

        SetForegroundWindow(target);
        if (WaitForForeground(target))
            return true;

        // Retry with our input queue attached to the current foreground thread,
        // which lifts the foreground lock for this call.
        uint thisThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(target, out _);
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);

        bool attachedForeground = foregroundThread != 0 && foregroundThread != thisThread &&
                                  AttachThreadInput(thisThread, foregroundThread, true);
        bool attachedTarget = targetThread != 0 && targetThread != thisThread &&
                              AttachThreadInput(thisThread, targetThread, true);
        try
        {
            BringWindowToTop(target);
            SetForegroundWindow(target);
            return WaitForForeground(target);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(thisThread, targetThread, false);
            if (attachedForeground) AttachThreadInput(thisThread, foregroundThread, false);
        }
    }

    private static bool WaitForForeground(IntPtr target)
    {
        for (int i = 0; i < 20; i++) // up to ~600 ms
        {
            if (IsForeground(target))
                return true;
            Thread.Sleep(30);
        }
        return false;
    }

    private static bool IsForeground(IntPtr target) => GetForegroundWindow() == target;

    // ---- helpers -------------------------------------------------------------

    private static DateTime SafeStartTime(Process p)
    {
        try
        {
            return p.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static uint GetParentProcessId(uint pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return 0;
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(handle, ProcessBasicInformation,
                ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0)
                return 0;
            return (uint)info.InheritedFromUniqueProcessId.ToInt64();
        }
        catch
        {
            return 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string ProcessNameForWindow(IntPtr hwnd)
    {
        try
        {
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
                return "";
            using Process p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private static string GetTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0)
            return "";
        var sb = new StringBuilder(len + 1);
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static void SendCtrlV()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void SendEnter()
    {
        keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ---- P/Invoke ------------------------------------------------------------

    private const int SW_RESTORE = 9;
    private const int ProcessBasicInformation = 0;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const byte VK_RETURN = 0x0D;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public UIntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
