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
    /// <summary>Tidy demo paths for the editor screenshot (the build machine can write to C:\).</summary>
    private static readonly string DemoRoot = Directory.Exists(@"C:\Games") || TryCreate(@"C:\Games") ? @"C:\Games"
                                              : Path.Combine(Path.GetTempPath(), "AutoSwitcherDemo");
    private static bool TryCreate(string d) { try { Directory.CreateDirectory(d); return true; } catch { return false; } }

    private const string ArtBase = "https://static-cdn.jtvnw.net/ttv-boxart/";
    private static readonly List<Process> Demo = new();

    private sealed record DemoGame(string Id, string Name, string Exe, string FullName, string Custom, bool Running,
                                   bool Enabled = true, string? TitlePattern = null, string[]? ArtIds = null);

    private static readonly DemoGame[] Games =
    {
        new("11557", "The Legend of Zelda: Ocarina of Time", "soh.exe", "Ship of Harkinian", "Zelda OoT", Running: true, ArtIds: new[] { "11557" }),
        new("490147", "Hollow Knight", "hollow_knight.exe", "Hollow Knight", "", Running: true, ArtIds: new[] { "490147" }),
        new("2692", "Super Mario 64", "sm64.exe", "Super Mario 64", "SM64", Running: false, ArtIds: new[] { "2692" }),
        new("1229", "Super Metroid", "emulator.exe", "Emulator", "Super Metroid", Running: false, TitlePattern: "Super Metroid*"),
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

        string root = DemoRoot;
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
            string path = Path.Combine(root, g.FullName, g.Exe);
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
        // Multiworlds: the first matches the games turned on (everything but Celeste), so it shows as active.
        string Id(string name) => cfg.Categories.First(c => c.Name == name).Id;
        cfg.Multiworlds.Add(new Multiworld { Name = "Multiworld Day 2", CategoryIds =
            { Id("The Legend of Zelda: Ocarina of Time"), Id("Hollow Knight"), Id("Super Mario 64"), Id("Super Metroid") } });
        cfg.Multiworlds.Add(new Multiworld { Name = "Speedrun practice", CategoryIds = { Id("Super Mario 64"), Id("Celeste") } });
        cfg.Multiworlds.Add(new Multiworld { Name = "Chill Sunday", CategoryIds = { Id("Hollow Knight"), Id("Celeste"), Id("Super Metroid") } });
        cfg.ActiveMultiworldId = cfg.Multiworlds[0].Id;
        cfg.PreMultiworldEnabled = cfg.Categories.ToDictionary(c => c.Id, _ => true);
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
            var onStream = App.Config.Categories.First(c => c.Name == "Super Mario 64");
            App.Switcher.SetDemo(new ChannelInfo { GameId = onStream.Id, GameName = onStream.Name,
                Title = "Multiworld Day 2 | Now playing: SM64 | !discord" }, isLive: true, viewers: 42);
            App.Watcher.StartDemo();
            _ = App.Updater.CheckAsync();

            w.Width = 1100; w.Height = 950;
            w.Left = 0; w.Top = 0;
            await Task.Delay(4000);                       // box art downloads + first layout
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "autoswitcher-screenshots.log"),
                $"window client {((FrameworkElement)w.Content).ActualWidth}x{((FrameworkElement)w.Content).ActualHeight}{Environment.NewLine}");

            async Task Shot(Action show, string name, FrameworkElement? element = null, double pad = 0)
            {
                show();
                PendingOoT();
                await Task.Delay(1200);                   // let animations (card, arrows) settle
                await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Save(element ?? (FrameworkElement)w.Content, Path.Combine(dir, name), pad);
            }

            await Shot(() => w.NavMappings.IsChecked = true, "mappings.png");
            await Shot(() => w.NavMultiworlds.IsChecked = true, "multiworlds.png");
            await Shot(() => w.NavTitles.IsChecked = true, "stream-titles.png");
            await Shot(() => w.OpenEditor(App.Config.Categories.First(c => c.Name.Contains("Ocarina"))), "edit-category.png");
            await Shot(() => w.NavManual.IsChecked = true, "manual.png");
            await Shot(() => w.NavBehaviour.IsChecked = true, "behaviour.png");
            await Shot(() => w.NavSettings.IsChecked = true, "settings.png");
            await Shot(() => w.NavMappings.IsChecked = true, "on-stream-panel.png", w.NowCard, pad: 14);
        }
        catch (Exception ex) { App.LogError("screenshots", ex); }
        finally
        {
            foreach (var p in Demo) { try { p.Kill(); } catch { } }
            foreach (var g in Games) { try { Directory.Delete(Path.Combine(DemoRoot, g.FullName), true); } catch { } }
        }
    }

    /// <summary>OoT counting down (~6 s left) so the cyan "Switching in" card shows, with 2 games in the stack.</summary>
    private static void PendingOoT()
    {
        var hit = App.Watcher.Running.FirstOrDefault(h => h.Category.Name.Contains("Ocarina"));
        if (hit != null) App.Watcher.DemoPending(hit, TimeSpan.FromSeconds(1.2));
    }

    /// <summary>
    /// Render the whole window at 2× onto the app background and save it, or just one element's area
    /// (plus padding) cropped out of that render, so it looks exactly as it does in the window.
    /// </summary>
    private static void Save(FrameworkElement el, string file, double pad)
    {
        const double scale = 2;
        var root = (FrameworkElement)Application.Current.MainWindow!.Content;
        double w = root.ActualWidth, h = root.ActualHeight;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("BgBrush"), null, new Rect(0, 0, w, h));
            var vb = new VisualBrush(root) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, w, h) };
            dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);

        BitmapSource img = rtb;
        if (!ReferenceEquals(el, root))
        {
            var r = el.TransformToAncestor(root).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            r.Inflate(pad, pad);
            r.Intersect(new Rect(0, 0, w, h));
            img = new CroppedBitmap(rtb, new Int32Rect((int)(r.X * scale), (int)(r.Y * scale), (int)(r.Width * scale), (int)(r.Height * scale)));
        }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(file);
        enc.Save(fs);
        Log.Info("screenshots", "Saved " + file);
    }
}
