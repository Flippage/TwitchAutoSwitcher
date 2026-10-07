using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoSwitcher;

internal static class Native
{
    // ---------- Foreground window events ----------
    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const int OBJID_WINDOW = 0;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // ---------- Window enumeration (for "Pick running") ----------
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    public const uint GW_OWNER = 4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    public static string GetTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Title of a process's main (visible, unowned, titled) top-level window, or null.</summary>
    public static string? FindMainWindowTitle(uint pid)
    {
        string? found = null;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint p);
            if (p != pid || !IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            string t = GetTitle(hwnd);
            if (t.Length == 0) return true;
            found = t;
            return false;   // stop
        }, IntPtr.Zero);
        return found;
    }

    // ---------- Processes ----------
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool K32EnumProcesses([Out] uint[] processIds, uint arraySizeBytes, out uint bytesReturned);

    [DllImport("kernel32.dll")]
    private static extern bool K32EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Full image path of a process, or null if it can't be opened (e.g. elevated/system).</summary>
    public static string? GetProcessPath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        finally { CloseHandle(h); }
    }

    private static uint[] _pidBuffer = new uint[1024];

    public static ReadOnlySpan<uint> EnumProcessIds()
    {
        while (true)
        {
            uint cb = (uint)(_pidBuffer.Length * sizeof(uint));
            if (!K32EnumProcesses(_pidBuffer, cb, out uint needed)) return ReadOnlySpan<uint>.Empty;
            if (needed < cb) return new ReadOnlySpan<uint>(_pidBuffer, 0, (int)(needed / sizeof(uint)));
            _pidBuffer = new uint[_pidBuffer.Length * 2];
        }
    }

    /// <summary>Hand unused pages back to Windows (called when hiding to tray).</summary>
    public static void TrimWorkingSet()
    {
        try { K32EmptyWorkingSet(GetCurrentProcess()); } catch { /* best effort */ }
    }

    // ---------- Dark title bar ----------
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void StyleTitleBar(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));        // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = 0x00000000;                                     // COLORREF black
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));   // DWMWA_CAPTION_COLOR (Win11)
        int border = 0x001C1C1C;
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));    // DWMWA_BORDER_COLOR (Win11)
    }

    // ---------- Fullscreen detection (for toast suppression) ----------
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    public static bool IsBusyFullscreen()
    {
        try
        {
            if (SHQueryUserNotificationState(out int s) != 0) return false;
            // QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4
            return s is 2 or 3 or 4;
        }
        catch { return false; }
    }

    // ---------- DPAPI (per-user encryption for tokens) ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    public static byte[] Protect(byte[] data) => Crypt(data, protect: true);
    public static byte[] Unprotect(byte[] data) => Crypt(data, protect: false);

    private static byte[] Crypt(byte[] data, bool protect)
    {
        const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
        var input = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        var output = new DATA_BLOB();
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            bool ok = protect
                ? CryptProtectData(ref input, "AutoSwitcher", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }
}
