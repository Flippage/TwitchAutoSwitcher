using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace AutoSwitcher;

/// <summary>
/// Detects which mapped game is "current", and keeps a list of every running mapped game.
///
///  • Running list (both modes): every 2 s the PID list is diffed (K32EnumProcesses); only NEW processes are
///    inspected, so steady-state cost is tiny. Emulator PIDs with title rules have their title re-read.
///  • Focus mode: foreground events don't decide anything by themselves. Each event (and a 1 s safety tick)
///    triggers an evaluation that asks Windows which window REALLY has focus right now, ignoring invisible
///    helper windows. This fixes transient popups (e.g. browser menus/tooltips) cancelling a pending switch.
///    A game must stay focused for FocusDelay before it becomes current.
///  • Launch mode: the newest launched mapped game becomes current.
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
    private readonly DispatcherTimer _focusDebounce;
    private readonly DispatcherTimer _focusTick;
    private readonly Dictionary<uint, GameHit?> _known = new();
    private readonly Dictionary<uint, DateTime> _firstSeen = new();
    private readonly HashSet<uint> _titleWatch = new();
    private readonly HashSet<uint> _alive = new();
    private readonly uint _selfPid = (uint)Environment.ProcessId;

    private Dictionary<string, List<Candidate>> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<Candidate>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _hook;
    private IntPtr _nameHook;
    private uint _nameHookPid;
    private GameHit? _pending;
    private Process? _exitWatch;
    private bool _primed;
    private bool _running;
    private string _lastFocusLog = "";
    private List<GameHit> _runningList = new();

    public GameHit? Current { get; private set; }
    public DetectionMode Mode { get; private set; }
    public TimeSpan FocusDelay { get; set; } = TimeSpan.FromSeconds(8);

    public event Action<GameHit>? Activated;
    public event Action<GameHit>? Exited;

    /// <summary>A switch waiting out the focus delay (null when none).</summary>
    public GameHit? Pending => _pending;
    public DateTime PendingSinceUtc { get; private set; }
    public event Action? PendingChanged;

    /// <summary>All running mapped games, newest first.</summary>
    public IReadOnlyList<GameHit> Running => _runningList;
    public event Action? RunningChanged;

    public GameWatcher()
    {
        RetroArch.Changed += _ => _ui.InvokeAsync(() =>
        {
            if (!_running) return;
            Poll();
            if (Mode == DetectionMode.Focus) EvaluateFocus(immediate: false);
        });
        _hookProc = OnForegroundEvent;
        _nameProc = OnNameChangeEvent;
        _pendingTimer = new DispatcherTimer(DispatcherPriority.Background);
        _pendingTimer.Tick += (_, _) =>
        {
            var p = _pending;
            ClearPending("delay elapsed");
            if (p != null) Activate(p, "focused for " + FocusDelay.TotalSeconds + "s");
        };
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _pollTimer.Tick += (_, _) => Poll();
        _focusDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _focusDebounce.Tick += (_, _) => { _focusDebounce.Stop(); EvaluateFocus(immediate: false); };
        _focusTick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _focusTick.Tick += (_, _) => EvaluateFocus(immediate: false);
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
        int count = 0, paused = 0;
        foreach (var c in categories)
        {
            if (!c.Enabled) { paused++; continue; }      // paused mappings are never detected
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
                count++;
            }
        }
        _byPath = byPath;
        _byName = byName;
        Log.Info("detect", $"Mappings loaded: {count} executable(s)" + (paused > 0 ? $", {paused} paused categor{(paused == 1 ? "y" : "ies")}" : ""));

        _known.Clear();
        _firstSeen.Clear();
        _titleWatch.Clear();
        _primed = false;
        if (Current != null) Current = Resolve((uint)Current.Pid, null);   // null if its mapping was paused or removed
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
        Log.Info("detect", $"Started in {mode} mode (focus delay {FocusDelay.TotalSeconds}s)");
        _known.Clear();
        _firstSeen.Clear();
        _titleWatch.Clear();
        _primed = false;
        Poll();
        _pollTimer.Start();
        if (mode == DetectionMode.Focus)
        {
            _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            if (_hook == IntPtr.Zero) Log.Warn("detect", "Foreground hook failed; relying on the 1s check");
            EvaluateFocus(immediate: true);
            _focusTick.Start();
        }
    }

    public void Stop()
    {
        _running = false;
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        UnhookNameChange();
        _pollTimer.Stop();
        _focusTick.Stop();
        _focusDebounce.Stop();
        ClearPending("stopped");
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
            foreach (var c in list)
                if (c.Title != null && c.Title.IsMatch(c.Exe.RetroArch
                        ? RetroArch.CurrentGame(c.Exe.RetroArchPort)               // RetroArch: the loaded game
                        : (title ??= Native.FindMainWindowTitle(pid) ?? "")) &&
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

    private static bool Same(GameHit? a, GameHit? b) =>
        a != null && b != null && a.Pid == b.Pid && ReferenceEquals(a.Exe, b.Exe);

    private static string Describe(GameHit h) => $"{h.Exe.FileName} (pid {h.Pid}) → {h.Category.Name}";

    // ---------------------------------------------------------------- focus mode

    // Events only *trigger* an evaluation; the evaluation reads the real foreground window.
    private void OnForegroundEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        _focusDebounce.Stop();
        _focusDebounce.Start();
    }

    private void OnNameChangeEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != 0) return;
        _focusDebounce.Stop();
        _focusDebounce.Start();
    }

    private void EvaluateFocus(bool immediate)
    {
        if (!_running || Mode != DetectionMode.Focus) return;

        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;                                   // mid-switch: no decision
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == _selfPid) return;                           // our own window: keep state
        if (!Native.IsWindowVisible(hwnd)) return;                         // invisible helper window: ignore

        if (HasTitleRules(pid)) HookNameChange(pid); else UnhookNameChange();

        var hit = Resolve(pid, Native.GetTitle(hwnd));
        LogFocus(pid, hit);

        if (hit == null)
        {
            // A real, visible non-game window has focus: the user moved away.
            if (_pending != null) ClearPending("focus moved to a non-game window");
            return;
        }
        if (IsCurrent(hit)) { if (_pending != null) ClearPending("back on the current game"); return; }
        if (Same(_pending, hit)) return;                                   // already counting down for it
        Schedule(hit, immediate);
    }

    private void LogFocus(uint pid, GameHit? hit)
    {
        string what = hit != null ? "mapped " + Describe(hit)
                                  : "other app " + (Path.GetFileName(Native.GetProcessPath(pid) ?? "") is { Length: > 0 } f ? f : "pid " + pid);
        if (what == _lastFocusLog) return;
        _lastFocusLog = what;
        Log.Info("focus", "Focused: " + what);
    }

    private void Schedule(GameHit hit, bool immediate)
    {
        ClearPending(null);
        if (immediate || FocusDelay <= TimeSpan.Zero) { Activate(hit, immediate ? "focused at start" : "focused (no delay)"); return; }
        _pending = hit;
        PendingSinceUtc = DateTime.UtcNow;
        _pendingTimer.Interval = FocusDelay;
        _pendingTimer.Start();
        Log.Info("focus", $"Pending switch in {FocusDelay.TotalSeconds}s: {Describe(hit)}");
        PendingChanged?.Invoke();
    }

    private void ClearPending(string? reason)
    {
        _pendingTimer.Stop();
        if (_pending == null) return;
        if (reason != null) Log.Info("focus", $"Pending switch cancelled ({reason}): {Describe(_pending)}");
        _pending = null;
        PendingChanged?.Invoke();
    }

    /// <summary>"Switch now" from the sidebar: skip the rest of the focus delay.</summary>
    public void ActivatePendingNow()
    {
        var p = _pending;
        ClearPending(null);
        if (p != null) Activate(p, "Switch now");
    }

    /// <summary>Screenshot mode: scan running processes once (focus mode, no hooks, timers or switching).</summary>
    public void StartDemo()
    {
        Stop();
        Mode = DetectionMode.Focus;
        _running = true;
        _known.Clear(); _firstSeen.Clear(); _titleWatch.Clear();
        _primed = false;
        Poll();
    }

    /// <summary>Screenshot mode: show a switch counting down (never fires).</summary>
    public void DemoPending(GameHit hit, TimeSpan elapsed)
    {
        _pendingTimer.Stop();
        _pending = hit;
        PendingSinceUtc = DateTime.UtcNow - elapsed;
        PendingChanged?.Invoke();
    }

    /// <summary>Drop a pending switch without applying it (a manual switch replaced it).</summary>
    public void ClearPendingNow() => ClearPending("manual switch");

    /// <summary>
    /// Auto-switch was turned back on: forget which game was "current" (a manual switch may have changed Twitch
    /// since), then pick up from what's actually happening now. Focus mode: the focused game (with the usual
    /// delay; nothing while AutoSwitcher itself is focused, so the next game you click counts).
    /// Launch mode: the newest running mapped game.
    /// </summary>
    public void Resync()
    {
        if (!_running) return;
        Log.Info("detect", "Resync after auto-switch resumed");
        Current = null;
        _lastFocusLog = "";
        ClearPending(null);
        if (Mode == DetectionMode.Focus) EvaluateFocus(immediate: false);
        else if (_runningList.FirstOrDefault() is { } newest) Activate(newest, "auto-switch resumed");
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

    // ---------------------------------------------------------------- process scan (both modes)

    private void Poll()
    {
        var ids = Native.EnumProcessIds();
        _alive.Clear();
        GameHit? newest = null;
        GameHit? goneCurrent = null;
        bool changed = false;
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
            if (hit != null)
            {
                _firstSeen[id] = DateTime.UtcNow;
                newest = hit;
                changed = true;
                if (_primed) Log.Info("scan", "Started: " + Describe(hit));
            }
        }
        if (_known.Count > _alive.Count)
            foreach (uint dead in _known.Keys.Where(k => !_alive.Contains(k)).ToList())
            {
                if (_known[dead] is { } gone) { changed = true; Log.Info("scan", "Closed: " + Describe(gone)); }
                _known.Remove(dead);
                _firstSeen.Remove(dead);
                _titleWatch.Remove(dead);
            }

        // Emulators: the game is often loaded after launch, so re-read just these windows' titles.
        foreach (uint pid in _titleWatch)
        {
            var now = Resolve(pid, null);
            var before = _known[pid];
            if (ReferenceEquals(before?.Exe, now?.Exe)) continue;
            _known[pid] = now;
            changed = true;
            if (now != null)
            {
                _firstSeen[pid] = DateTime.UtcNow;
                if (_primed) { newest = now; Log.Info("scan", "Title matched: " + Describe(now)); }
            }
            else
            {
                // The game was closed inside the emulator (title / loaded game no longer matches): drop it,
                // and treat it like the game closing if it was the current one.
                _firstSeen.Remove(pid);
                Log.Info("scan", "No longer matches: " + Describe(before!));
                if (Current != null && Current.Pid == (int)pid && ReferenceEquals(Current.Exe, before!.Exe)) goneCurrent = Current;
            }
        }

        if (changed || !_primed) RebuildRunning();
        if (goneCurrent != null && newest == null) CurrentGone(goneCurrent, "no longer matches");

        bool wasPrimed = _primed;
        _primed = true;
        if (Mode == DetectionMode.Launch && newest != null && (wasPrimed || Current == null) && !IsCurrent(newest))
            Activate(newest, "launched");
    }

    private void RebuildRunning()
    {
        _runningList = _known
            .Where(kv => kv.Value != null)
            .OrderByDescending(kv => _firstSeen.TryGetValue(kv.Key, out var t) ? t : DateTime.MinValue)
            .Select(kv => kv.Value!)
            .ToList();
        RunningChanged?.Invoke();
    }

    // ---------------------------------------------------------------- shared

    private void Activate(GameHit hit, string why)
    {
        Current = hit;
        Log.Info("detect", $"Current game ({why}): {Describe(hit)}");
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
        catch (Exception ex)
        {
            // Elevated games can deny access; the 2 s scan still notices the exit.
            Log.Warn("detect", $"Can't watch pid {pid} for exit ({ex.GetType().Name}); using the process scan instead");
        }
    }

    private static int SafeId(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }

    private void OnExited(int pid)
    {
        if (_known.Remove((uint)pid)) { _firstSeen.Remove((uint)pid); _titleWatch.Remove((uint)pid); RebuildRunning(); }
        if (Current == null || Current.Pid != pid) return;
        CurrentGone(Current, "closed");
    }

    /// <summary>The current game closed (or stopped matching): launch mode moves to another running game, else fallback.</summary>
    private void CurrentGone(GameHit gone, string why)
    {
        Current = null;
        Log.Info("detect", $"Current game {why}: " + Describe(gone));

        if (Mode == DetectionMode.Launch && _runningList.FirstOrDefault() is { } other)
        {
            Activate(other, "previous game closed");
            return;
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
