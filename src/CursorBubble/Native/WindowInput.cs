using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CursorBubble.Native;

/// <summary>
/// Finds the top-level window that owns a Claude Code session's terminal and
/// delivers a reply to it by focusing the window and simulating Ctrl+V + Enter.
/// The clipboard is used for the text (reliable for any Unicode); the caller
/// sets it on the UI thread.
/// </summary>
public static class WindowInput
{
    // ---- capture the owning window (called from the --hook process) ----------

    /// <summary>
    /// Walk up the parent-process chain from the current process to the first
    /// ancestor that owns a visible top-level window (VS Code, Windows Terminal,
    /// a console host, …). Falls back to the attached console window.
    /// </summary>
    public static void CaptureOwnerWindow(out long handle, out string title, out string processName)
    {
        handle = 0;
        title = "";
        processName = "";

        try
        {
            Dictionary<uint, uint> parents = BuildParentMap();
            uint pid = (uint)Environment.ProcessId;

            for (int depth = 0; depth < 16 && pid != 0; depth++)
            {
                if (!parents.TryGetValue(pid, out uint ppid))
                    break;
                pid = ppid;
                if (pid == 0)
                    break;

                try
                {
                    using Process p = Process.GetProcessById((int)pid);
                    IntPtr h = p.MainWindowHandle;
                    if (h != IntPtr.Zero && IsWindowVisible(h))
                    {
                        handle = h.ToInt64();
                        title = GetTitle(h);
                        processName = p.ProcessName;
                        return;
                    }
                }
                catch
                {
                    // process may have exited between snapshot and lookup
                }
            }
        }
        catch
        {
            // fall through to the console window
        }

        IntPtr console = GetConsoleWindow();
        if (console != IntPtr.Zero)
        {
            handle = console.ToInt64();
            title = GetTitle(console);
        }
    }

    // ---- deliver a reply (called from the responder window, UI thread) -------

    /// <summary>
    /// Focus the target window and paste + send <paramref name="text"/> (already
    /// on the clipboard). Returns false if no suitable window could be focused.
    /// </summary>
    public static bool SendReply(long storedHandle, string storedTitle, string projectName)
    {
        IntPtr target = ResolveWindow(storedHandle, storedTitle, projectName);
        if (target == IntPtr.Zero)
            return false;

        AllowSetForegroundWindow(ASFW_ANY);
        if (IsIconic(target))
            ShowWindow(target, SW_RESTORE);
        SetForegroundWindow(target);

        // Give the target a moment to actually receive focus, then paste + enter.
        Thread.Sleep(140);
        SendCtrlV();
        Thread.Sleep(40);
        SendEnter();
        return true;
    }

    private static IntPtr ResolveWindow(long storedHandle, string storedTitle, string projectName)
    {
        var h = new IntPtr(storedHandle);
        if (storedHandle != 0 && IsWindow(h) && IsWindowVisible(h))
            return h;

        // The stored window is gone — find one whose title mentions the project.
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            IntPtr byProject = FindWindowByTitleContains(projectName);
            if (byProject != IntPtr.Zero)
                return byProject;
        }

        if (!string.IsNullOrWhiteSpace(storedTitle))
        {
            IntPtr byTitle = FindWindowByTitleContains(storedTitle);
            if (byTitle != IntPtr.Zero)
                return byTitle;
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindWindowByTitleContains(string needle)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h))
                return true;
            string t = GetTitle(h);
            if (t.Length > 0 && t.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false; // stop
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---- helpers -------------------------------------------------------------

    private static Dictionary<uint, uint> BuildParentMap()
    {
        var map = new Dictionary<uint, uint>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
            return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    map[entry.th32ProcessID] = entry.th32ParentProcessID;
                } while (Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }

    private static string GetTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0)
            return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
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

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const int SW_RESTORE = 9;
    private const uint ASFW_ANY = 0xFFFFFFFF;

    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const byte VK_RETURN = 0x0D;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

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
