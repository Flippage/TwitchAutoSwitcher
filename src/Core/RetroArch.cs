using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace AutoSwitcher;

/// <summary>
/// Asks RetroArch which game is loaded, using its Network Commands (Settings → Network → Network Commands, UDP 55355).
///   request:  GET_STATUS
///   reply:    GET_STATUS PLAYING n64,Super Mario 64 (USA),crc32=1A2B3C4D   (or PAUSED …, or CONTENTLESS)
/// Detection reads a cached answer (never blocks); a refresh runs in the background at most once a second,
/// and <see cref="Changed"/> fires when the loaded game changes so detection can react straight away.
/// </summary>
public static class RetroArch
{
    public const int DefaultPort = 55355;

    public sealed record Status(bool Reachable, string State, string System, string Game, string Raw);

    private sealed class Entry
    {
        public Status Last = new(false, "", "", "", "");
        public DateTime CheckedUtc = DateTime.MinValue;
        public bool InFlight;
        public bool WarnedUnreachable;
    }

    private static readonly ConcurrentDictionary<int, Entry> Ports = new();

    /// <summary>Raised (on a background thread) when a port's loaded game changes.</summary>
    public static event Action<int>? Changed;

    /// <summary>Name of the loaded game ("" if none or RetroArch isn't answering). Never blocks.</summary>
    public static string CurrentGame(int port)
    {
        var e = Ports.GetOrAdd(port, _ => new Entry());
        if (!e.InFlight && DateTime.UtcNow - e.CheckedUtc > TimeSpan.FromSeconds(1))
        {
            e.InFlight = true;
            _ = RefreshAsync(port, e);
        }
        return e.Last.Reachable ? e.Last.Game : "";
    }

    private static async Task RefreshAsync(int port, Entry e)
    {
        try
        {
            var s = await QueryAsync(port, TimeSpan.FromMilliseconds(400));
            bool changed = s.Game != e.Last.Game || s.Reachable != e.Last.Reachable;
            if (changed)
            {
                if (s.Reachable) Log.Info("retroarch", $"Port {port}: {(s.Game.Length > 0 ? $"{s.State} {s.System} · {s.Game}" : "no game loaded")}");
                else if (!e.WarnedUnreachable)
                {
                    e.WarnedUnreachable = true;
                    Log.Warn("retroarch", $"No answer on port {port}. In RetroArch turn on Settings → Network → Network Commands (port {port}).");
                }
                if (s.Reachable) e.WarnedUnreachable = false;
            }
            e.Last = s;
            if (changed) Changed?.Invoke(port);
        }
        finally
        {
            e.CheckedUtc = DateTime.UtcNow;
            e.InFlight = false;
        }
    }

    /// <summary>One GET_STATUS round trip (also used by the editor's Test button).</summary>
    public static async Task<Status> QueryAsync(int port, TimeSpan timeout)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            byte[] req = Encoding.ASCII.GetBytes("GET_STATUS");
            await udp.SendAsync(req, req.Length, new IPEndPoint(IPAddress.Loopback, port));
            var receive = udp.ReceiveAsync();
            if (await Task.WhenAny(receive, Task.Delay(timeout)) != receive) return new(false, "", "", "", "");
            string raw = Encoding.UTF8.GetString((await receive).Buffer).Trim();
            return Parse(raw);
        }
        catch (SocketException) { return new(false, "", "", "", ""); }   // nothing listening (ICMP port unreachable)
        catch (ObjectDisposedException) { return new(false, "", "", "", ""); }
    }

    /// <summary>"GET_STATUS PLAYING n64,Super Mario 64 (USA),crc32=…" → state, system, game. Names may contain commas.</summary>
    public static Status Parse(string raw)
    {
        string rest = raw.StartsWith("GET_STATUS", StringComparison.OrdinalIgnoreCase) ? raw[10..].Trim() : raw.Trim();
        int sp = rest.IndexOf(' ');
        string state = sp < 0 ? rest : rest[..sp];
        if (sp < 0) return new(true, state, "", "", raw);                 // CONTENTLESS
        string info = rest[(sp + 1)..];
        int crc = info.LastIndexOf(",crc32=", StringComparison.OrdinalIgnoreCase);
        if (crc >= 0) info = info[..crc];
        int comma = info.IndexOf(',');
        string system = comma < 0 ? "" : info[..comma];
        string game = comma < 0 ? info : info[(comma + 1)..];
        return new(true, state, system.Trim(), game.Trim(), raw);
    }
}
