using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace AutoSwitcher;

/// <summary>
/// Detects which mapped game is "current".
///  • Focus mode: event-driven (SetWinEventHook on foreground changes) — zero polling. A game must stay
///    focused for FocusDelay before it becomes current; any other focus change cancels the pending switch.
///  • Launch mode: every 2s diffs the PID list (K32EnumProcesses, no allocations per process) and only
///    resolves paths for NEW PIDs, so the steady-state cost is tiny.
/// Exit of the current game is observed through a process wait handle (no polling).
/// Everything runs on the UI dispatcher thread.
/// </summary>
public sealed class GameWatcher : IDisposable
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly Native.WinEventDelegate _hookProc;   // keep a reference so the GC never collects it
    private readonly DispatcherTimer _pendingTimer;
    private readonly DispatcherTimer _pollTimer;
    private readonly Dictionary<uint, GameHit?> _known = new();
    private readonly HashSet<uint> _alive = new();
    private readonly uint _selfPid = (uint)Environment.ProcessId;

    private Dictionary<string, (CategoryMapping Cat, ExeMapping Exe)> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (CategoryMapping Cat, ExeMapping Exe)> _byName = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _hook;
    private GameHit? _pending;
    private Process? _exitWatch;
    private bool _primed;
    private bool _running;

    public GameHit? Current { get; private set; }
    public DetectionMode Mode { get; private set; }
    public TimeSpan FocusDelay { get; set; } = TimeSpan.FromSeconds(8);

    public event Action<GameHit>? Activated;
    public event Action<GameHit>? Exited;

    public GameWatcher()
    {
        _hookProc = OnWinEvent;
        _pendingTimer = new DispatcherTimer(DispatcherPriority.Background);
        _pendingTimer.Tick += (_, _) =>
        {
            _pendingTimer.Stop();
            var p = _pending;
            _pending = null;
            if (p != null) Activate(p);
        };
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _pollTimer.Tick += (_, _) => Poll();
    }

    public void UpdateMappings(IEnumerable<CategoryMapping> categories)
    {
        var byPath = new Dictionary<string, (CategoryMapping, ExeMapping)>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, (CategoryMapping, ExeMapping)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in categories)
            foreach (var e in c.Executables)
            {
                if (string.IsNullOrWhiteSpace(e.Path)) continue;
                byPath[e.Path] = (c, e);
                byName.TryAdd(e.ProcessName, (c, e));   // fallback if the game was moved/reinstalled
            }
        _byPath = byPath;
        _byName = byName;

        // Re-evaluate with the new table.
        _known.Clear();
        _primed = false;
        if (Current != null && Resolve((uint)Current.Pid) is { } still) Current = still;
        if (_running) Start(Mode);
    }

    public void Start(DetectionMode mode)
    {
        Stop();
        _running = true;
        Mode = mode;
        if (mode == DetectionMode.Focus)
        {
            _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            HandleForeground(Native.GetForegroundWindow(), immediate: true);
        }
        else
        {
            _known.Clear();
            _primed = false;
            Poll();
            _pollTimer.Start();
        }
    }

    public void Stop()
    {
        _running = false;
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        _pollTimer.Stop();
        _pendingTimer.Stop();
        _pending = null;
    }

    // ---------------------------------------------------------------- focus mode

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        => HandleForeground(hwnd, immediate: false);

    private void HandleForeground(IntPtr hwnd, bool immediate)
    {
        // Any focus change cancels a pending switch (this is the "clicking back and forth" buffer).
        _pendingTimer.Stop();
        _pending = null;

        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _selfPid) return;

        var hit = Resolve(pid);
        if (hit == null) return;                                         // not a game: keep current
        if (Current != null && Current.Pid == hit.Pid && ReferenceEquals(Current.Exe, hit.Exe)) return;

        if (immediate || FocusDelay <= TimeSpan.Zero) { Activate(hit); return; }
        _pending = hit;
        _pendingTimer.Interval = FocusDelay;
        _pendingTimer.Start();
    }

    // ---------------------------------------------------------------- launch mode

    private void Poll()
    {
        var ids = Native.EnumProcessIds();
        _alive.Clear();
        GameHit? newest = null;
        foreach (uint id in ids)
        {
            _alive.Add(id);
            if (_known.ContainsKey(id)) continue;
            var hit = id == 0 || id == _selfPid ? null : Resolve(id);
            _known[id] = hit;
            if (hit != null) newest = hit;
        }
        if (_known.Count > _alive.Count)
            foreach (uint dead in _known.Keys.Where(k => !_alive.Contains(k)).ToList()) _known.Remove(dead);

        bool wasPrimed = _primed;
        _primed = true;
        if (newest != null && (wasPrimed || Current == null)) Activate(newest);
    }

    // ---------------------------------------------------------------- shared

    private GameHit? Resolve(uint pid)
    {
        string? path = Native.GetProcessPath(pid);
        if (path == null) return null;
        if (_byPath.TryGetValue(path, out var m) || _byName.TryGetValue(Path.GetFileNameWithoutExtension(path), out m))
            return new GameHit(m.Cat, m.Exe, (int)pid);
        return null;
    }

    private void Activate(GameHit hit)
    {
        Current = hit;
        WatchExit(hit.Pid);
        Activated?.Invoke(hit);
    }

    private void WatchExit(int pid)
    {
        if (_exitWatch != null && SafeId(_exitWatch) == pid) return;
        _exitWatch?.Dispose();
        _exitWatch = null;
        try
        {
            var p = Process.GetProcessById(pid);
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => _ui.InvokeAsync(() => OnExited(pid));
            _exitWatch = p;
        }
        catch { /* elevated games can deny SYNCHRONIZE access; we just won't see the exit */ }
    }

    private static int SafeId(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }

    private void OnExited(int pid)
    {
        if (Current == null || Current.Pid != pid) return;
        var gone = Current;
        Current = null;
        _known.Remove((uint)pid);

        if (Mode == DetectionMode.Launch)
        {
            var other = _known.Values.LastOrDefault(h => h != null && h.Pid != pid);
            if (other != null) { Activate(other); return; }
        }
        Exited?.Invoke(gone);
    }

    /// <summary>Visible top-level windows, one per executable — used by "Pick running".</summary>
    public static List<RunningApp> ListRunningApps()
    {
        var result = new List<RunningApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint self = (uint)Environment.ProcessId;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;
            string title = Native.GetTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self) return true;
            string? path = Native.GetProcessPath(pid);
            if (path == null || !seen.Add(path)) return true;
            if (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) return true;
            result.Add(new RunningApp(path, Path.GetFileName(path), title));
            return true;
        }, IntPtr.Zero);
        return result.OrderBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Dispose()
    {
        Stop();
        _exitWatch?.Dispose();
    }
}
