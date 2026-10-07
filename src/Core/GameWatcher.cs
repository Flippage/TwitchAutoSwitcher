using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace AutoSwitcher;

/// <summary>
/// Detects which mapped game is "current".
///  • Focus mode: event-driven (SetWinEventHook on foreground changes) — zero polling. A game must stay
///    focused for FocusDelay before it becomes current; any other focus change cancels the pending switch.
///  • Launch mode: every 2s diffs the PID list (K32EnumProcesses) and only resolves paths for NEW PIDs.
///  • Window-title rules (emulators): the same exe can map to different categories by window title.
///    In focus mode a title-change hook is attached to the focused emulator process only; in launch mode
///    just the emulator PIDs have their title re-read on the 2s poll. Everything else costs nothing extra.
/// Exit of the current game is observed through a process wait handle (no polling).
/// Everything runs on the UI dispatcher thread.
/// </summary>
public sealed class GameWatcher : IDisposable
{
    private sealed record Candidate(CategoryMapping Cat, ExeMapping Exe, Regex? Title);

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly Native.WinEventDelegate _hookProc;     // keep references so the GC never collects them
    private readonly Native.WinEventDelegate _nameProc;
    private readonly DispatcherTimer _pendingTimer;
    private readonly DispatcherTimer _pollTimer;
    private readonly Dictionary<uint, GameHit?> _known = new();
    private readonly HashSet<uint> _titleWatch = new();      // launch mode: PIDs whose exe has title rules
    private readonly HashSet<uint> _alive = new();
    private readonly uint _selfPid = (uint)Environment.ProcessId;

    private Dictionary<string, List<Candidate>> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<Candidate>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _hook;
    private IntPtr _nameHook;
    private uint _nameHookPid;
    private IntPtr _foreground;
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
        _hookProc = OnForegroundEvent;
        _nameProc = OnNameChangeEvent;
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

    /// <summary>"Donkey Kong 64*USA" → case-insensitive "contains", * = any text.</summary>
    public static Regex TitleRegex(string pattern)
    {
        string body = Regex.Escape(pattern.Trim()).Replace(@"\*", ".*");
        return new Regex(body, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public void UpdateMappings(IEnumerable<CategoryMapping> categories)
    {
        var byPath = new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in categories)
            foreach (var e in c.Executables)
            {
                if (string.IsNullOrWhiteSpace(e.Path)) continue;
                Regex? rx = null;
                if (e.UsesTitle)
                {
                    try { rx = TitleRegex(e.TitlePattern); } catch { rx = null; }
                }
                var cand = new Candidate(c, e, rx);
                Add(byPath, e.Path, cand);
                Add(byName, e.ProcessName, cand);   // fallback if the game was moved/reinstalled
            }
        _byPath = byPath;
        _byName = byName;

        _known.Clear();
        _titleWatch.Clear();
        _primed = false;
        if (Current != null && Resolve((uint)Current.Pid, null) is { } still) Current = still;
        if (_running) Start(Mode);

        static void Add(Dictionary<string, List<Candidate>> d, string key, Candidate c)
        {
            if (!d.TryGetValue(key, out var list)) d[key] = list = new List<Candidate>();
            list.Add(c);
        }
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
            _titleWatch.Clear();
            _primed = false;
            Poll();
            _pollTimer.Start();
        }
    }

    public void Stop()
    {
        _running = false;
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        UnhookNameChange();
        _pollTimer.Stop();
        _pendingTimer.Stop();
        _pending = null;
    }

    // ---------------------------------------------------------------- resolution

    private List<Candidate>? CandidatesFor(string path)
    {
        if (_byPath.TryGetValue(path, out var list)) return list;
        if (_byName.TryGetValue(Path.GetFileNameWithoutExtension(path), out list)) return list;
        return null;
    }

    /// <summary>
    /// Title rules win (longest pattern = most specific); otherwise a plain mapping of the exe; otherwise nothing.
    /// <paramref name="title"/> null = look up the process's main window title (only if title rules exist).
    /// </summary>
    private GameHit? Resolve(uint pid, string? title)
    {
        string? path = Native.GetProcessPath(pid);
        if (path == null) return null;
        var list = CandidatesFor(path);
        if (list == null) return null;

        Candidate? best = null;
        if (list.Any(c => c.Title != null))
        {
            title ??= Native.FindMainWindowTitle(pid) ?? "";
            foreach (var c in list)
                if (c.Title != null && c.Title.IsMatch(title) &&
                    (best == null || c.Exe.TitlePattern.Length > best.Exe.TitlePattern.Length))
                    best = c;
        }
        best ??= list.FirstOrDefault(c => c.Title == null);
        return best == null ? null : new GameHit(best.Cat, best.Exe, (int)pid);
    }

    private bool HasTitleRules(uint pid)
    {
        string? path = Native.GetProcessPath(pid);
        return path != null && CandidatesFor(path)?.Any(c => c.Title != null) == true;
    }

    private bool IsCurrent(GameHit hit) =>
        Current != null && Current.Pid == hit.Pid && ReferenceEquals(Current.Exe, hit.Exe);

    // ---------------------------------------------------------------- focus mode

    private void OnForegroundEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        => HandleForeground(hwnd, immediate: false);

    private void HandleForeground(IntPtr hwnd, bool immediate)
    {
        // Any focus change cancels a pending switch (this is the "clicking back and forth" buffer).
        _pendingTimer.Stop();
        _pending = null;
        _foreground = hwnd;

        if (hwnd == IntPtr.Zero) { UnhookNameChange(); return; }
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _selfPid) { UnhookNameChange(); return; }

        // Emulator with title rules: also listen for its title changing (loading another game).
        if (HasTitleRules(pid)) HookNameChange(pid); else UnhookNameChange();

        var hit = Resolve(pid, Native.GetTitle(hwnd));
        if (hit == null || IsCurrent(hit)) return;                          // not a game, or already current
        Schedule(hit, immediate);
    }

    private void OnNameChangeEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != 0 || hwnd != _foreground) return;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        var hit = Resolve(pid, Native.GetTitle(hwnd));
        if (hit == null) return;                                             // e.g. emulator menu: keep current
        if (IsCurrent(hit)) { _pendingTimer.Stop(); _pending = null; return; }
        // Titles that tick (FPS counters) must not keep restarting the same pending switch.
        if (_pending != null && _pending.Pid == hit.Pid && ReferenceEquals(_pending.Exe, hit.Exe)) return;
        Schedule(hit, immediate: false);
    }

    private void Schedule(GameHit hit, bool immediate)
    {
        _pendingTimer.Stop();
        if (immediate || FocusDelay <= TimeSpan.Zero) { _pending = null; Activate(hit); return; }
        _pending = hit;
        _pendingTimer.Interval = FocusDelay;
        _pendingTimer.Start();
    }

    private void HookNameChange(uint pid)
    {
        if (_nameHook != IntPtr.Zero && _nameHookPid == pid) return;
        UnhookNameChange();
        _nameHook = Native.SetWinEventHook(Native.EVENT_OBJECT_NAMECHANGE, Native.EVENT_OBJECT_NAMECHANGE,
            IntPtr.Zero, _nameProc, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
        _nameHookPid = pid;
    }

    private void UnhookNameChange()
    {
        if (_nameHook != IntPtr.Zero) Native.UnhookWinEvent(_nameHook);
        _nameHook = IntPtr.Zero;
        _nameHookPid = 0;
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
            GameHit? hit = null;
            if (id != 0 && id != _selfPid)
            {
                hit = Resolve(id, null);
                if (HasTitleRules(id)) _titleWatch.Add(id);
            }
            _known[id] = hit;
            if (hit != null) newest = hit;
        }
        if (_known.Count > _alive.Count)
            foreach (uint dead in _known.Keys.Where(k => !_alive.Contains(k)).ToList())
            {
                _known.Remove(dead);
                _titleWatch.Remove(dead);
            }

        // Emulators: the game is often loaded after launch, so re-read just these windows' titles.
        foreach (uint pid in _titleWatch)
        {
            var now = Resolve(pid, null);
            var before = _known[pid];
            bool changed = now != null && (before == null || !ReferenceEquals(before.Exe, now.Exe));
            _known[pid] = now ?? before;                                     // menu/no match: keep last game
            if (changed && _primed) newest = now;
        }

        bool wasPrimed = _primed;
        _primed = true;
        if (newest != null && (wasPrimed || Current == null) && !IsCurrent(newest)) Activate(newest);
    }

    // ---------------------------------------------------------------- shared

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
        _titleWatch.Remove((uint)pid);

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

    /// <summary>Current window title of any running instance of this exe (for "Use current title").</summary>
    public static string? FindTitleForExe(string exePath)
    {
        string? found = null;
        uint self = (uint)Environment.ProcessId;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self) return true;
            string? path = Native.GetProcessPath(pid);
            if (path == null || !string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase)) return true;
            string title = Native.GetTitle(hwnd);
            if (title.Length == 0) return true;
            found = title;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public void Dispose()
    {
        Stop();
        _exitWatch?.Dispose();
    }
}
