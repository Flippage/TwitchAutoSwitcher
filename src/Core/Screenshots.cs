using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutoSwitcher;

/// <summary>
/// <c>--screenshots &lt;dir&gt;</c>: start with a demo setup (sample games, a demo channel, nothing personal and nothing
/// saved), capture each page as a 2× PNG for the README, then exit. Used by the "README screenshots" workflow.
/// Games are "running" via copies of ping.exe renamed to the game's exe, so real detection drives the UI.
/// </summary>
public static class Screenshots
{
    private const string ArtBase = "https://static-cdn.jtvnw.net/ttv-boxart/";
    private static readonly List<Process> Demo = new();

    private sealed record DemoGame(string Id, string Name, string Exe, string FullName, string Custom, bool Running,
                                   bool Enabled = true, string? TitlePattern = null, string[]? ArtIds = null);

    private static readonly DemoGame[] Games =
    {
        new("11557", "The Legend of Zelda: Ocarina of Time", "soh.exe", "Ship of Harkinian", "Zelda OoT", Running: true, ArtIds: new[] { "11557" }),
        new("490147", "Hollow Knight", "hollow_knight.exe", "Hollow Knight", "", Running: true, ArtIds: new[] { "490147" }),
        new("1424133580", "Neon White", "Neon White.exe", "Neon White", "", Running: false),
        new("1665347569", "Metroid Prime Remastered", "emulator.exe", "Emulator", "Metroid Prime", Running: false, TitlePattern: "Metroid Prime*"),
        new("504461", "Celeste", "Celeste.exe", "Celeste", "", Running: false, Enabled: false, ArtIds: new[] { "504461" }),
    };

    // ------------------------------------------------------------------ config

    public static AppConfig BuildConfig(string dir)
    {
        ConfigStore.ReadOnly = true;
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        var art = Task.Run(() => ResolveArtAsync(log)).GetAwaiter().GetResult();
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "autoswitcher-screenshots.log"), log);

        string root = Path.Combine(Path.GetTempPath(), "AutoSwitcherDemo");
        var cfg = new AppConfig
        {
            AutoSwitch = true,
            Mode = DetectionMode.Focus,
            FocusDelaySeconds = 8,
            UpdateTitle = true,
            TitleTemplate = "Multiworld Day 2 | Now playing: %customName% | !discord",
            Toasts = true,
            CloseToTray = true,
            RecentTitles =
            {
                "Multiworld Day 2 | Now playing: %customName% | !discord",
                "Multiworld Day 1 | %gameName% | !discord",
                "Chill speedrun practice | %customName%",
            },
        };
        foreach (var g in Games)
        {
            string path = Path.Combine(root, Path.GetFileNameWithoutExtension(g.Exe), g.Exe);
            var exe = new ExeMapping { Path = path, FullName = g.FullName, CustomName = g.Custom };
            if (g.TitlePattern != null) { exe.MatchTitle = true; exe.TitlePattern = g.TitlePattern; }
            cfg.Categories.Add(new CategoryMapping
            {
                Id = g.Id, Name = g.Name, Enabled = g.Enabled,
                BoxArtUrl = art.TryGetValue(g.Name, out var u) ? u : "",
                Executables = { exe },
            });
            if (g.Running) StartFake(path);
        }
        return cfg;
    }

    /// <summary>Box art by name (Twitch's CDN still serves these), falling back to known category ids.</summary>
    private static async Task<Dictionary<string, string>> ResolveArtAsync(List<string> log)
    {
        var found = new Dictionary<string, string>();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        foreach (var g in Games)
        {
            var candidates = new List<string> { ArtBase + Uri.EscapeDataString(g.Name) + "-{width}x{height}.jpg" };
            foreach (var id in (g.ArtIds ?? Array.Empty<string>()).Append(g.Id).Distinct())
            {
                candidates.Add(ArtBase + id + "_IGDB-{width}x{height}.jpg");
                candidates.Add(ArtBase + id + "-{width}x{height}.jpg");
            }
            foreach (var c in candidates)
            {
                try
                {
                    using var resp = await http.GetAsync(c.Replace("{width}x{height}", "104x144"));
                    log.Add($"{(int)resp.StatusCode} {c}");
                    if (resp.StatusCode == HttpStatusCode.OK) { found[g.Name] = c; break; }
                }
                catch (Exception ex) { log.Add($"ERR {c}: {ex.Message}"); }
            }
        }
        return found;
    }

    private static void StartFake(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), path, overwrite: true);
            var p = Process.Start(new ProcessStartInfo(path, "-n 600 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false });
            if (p != null) Demo.Add(p);
        }
        catch (Exception ex) { Log.Warn("screenshots", $"Couldn't start demo game {path}: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ capture

    public static async Task RunAsync(MainWindow w, string dir)
    {
        try
        {
            App.Twitch.UseDemo("YourChannel");
            var neon = App.Config.Categories.First(c => c.Name == "Neon White");
            App.Switcher.SetDemo(new ChannelInfo { GameId = neon.Id, GameName = neon.Name,
                Title = "Multiworld Day 2 | Now playing: Neon White | !discord" }, isLive: true, viewers: 42);
            App.Watcher.StartDemo();
            _ = App.Updater.CheckAsync();

            w.Width = 1100; w.Height = 860;
            w.Left = 0; w.Top = 0;
            await Task.Delay(4000);                       // box art downloads + first layout

            async Task Shot(Action show, string name, FrameworkElement? element = null, double pad = 0)
            {
                show();
                PendingOoT();
                await Task.Delay(1200);                   // let animations (card, arrows) settle
                await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Save(element ?? (FrameworkElement)w.Content, Path.Combine(dir, name), pad);
            }

            await Shot(() => w.NavMappings.IsChecked = true, "mappings.png");
            await Shot(() => w.NavTitles.IsChecked = true, "stream-titles.png");
            await Shot(() => w.OpenEditor(App.Config.Categories.First(c => c.Name.Contains("Ocarina"))), "edit-category.png");
            await Shot(() => w.NavManual.IsChecked = true, "manual.png");
            await Shot(() => w.NavBehaviour.IsChecked = true, "behaviour.png");
            await Shot(() => w.NavSettings.IsChecked = true, "settings.png");
            await Shot(() => w.NavMappings.IsChecked = true, "on-stream-panel.png", w.NowCard, pad: 16);
        }
        catch (Exception ex) { App.LogError("screenshots", ex); }
        finally
        {
            foreach (var p in Demo) { try { p.Kill(); } catch { } }
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "AutoSwitcherDemo"), true); } catch { }
        }
    }

    /// <summary>OoT counting down (~6 s left) so the cyan "Switching in" card shows, with 2 games in the stack.</summary>
    private static void PendingOoT()
    {
        var hit = App.Watcher.Running.FirstOrDefault(h => h.Category.Name.Contains("Ocarina"));
        if (hit != null) App.Watcher.DemoPending(hit, TimeSpan.FromSeconds(1.2));
    }

    /// <summary>Render an element at 2× onto the app background (plus optional padding) and save it as PNG.</summary>
    private static void Save(FrameworkElement el, string file, double pad)
    {
        const double scale = 2;
        double w = el.ActualWidth, h = el.ActualHeight;
        var bg = (Brush)Application.Current.FindResource(pad > 0 ? "SideBrush" : "BgBrush");
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(bg, null, new Rect(0, 0, w + pad * 2, h + pad * 2));
            var vb = new VisualBrush(el) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, w, h) };
            dc.DrawRectangle(vb, null, new Rect(pad, pad, w, h));
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling((w + pad * 2) * scale), (int)Math.Ceiling((h + pad * 2) * scale),
                                         96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
        Log.Info("screenshots", "Saved " + file);
    }
}
