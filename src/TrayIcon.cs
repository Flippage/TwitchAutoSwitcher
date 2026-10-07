using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutoSwitcher;

/// <summary>System tray icon + native Windows notifications (balloon tips render as toasts on Win10/11).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autoItem;
    private readonly Icon _iconImage;
    private bool _syncing;

    public TrayIcon()
    {
        _iconImage = MakeIcon();
        _icon = new NotifyIcon { Icon = _iconImage, Text = "AutoSwitcher", Visible = true };

        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add("Open AutoSwitcher", null, (_, _) => App.Current.ShowMain());
        _autoItem = new ToolStripMenuItem("Auto-switch") { CheckOnClick = true, Checked = App.Config.AutoSwitch };
        _autoItem.CheckedChanged += (_, _) =>
        {
            if (!_syncing) App.SetAutoSwitch(_autoItem.Checked);
        };
        menu.Items.Add(_autoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => App.Current.ExitApp());
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) App.Current.ShowMain();
        };
    }

    public void SetAutoChecked(bool on)
    {
        _syncing = true;
        _autoItem.Checked = on;
        _syncing = false;
    }

    public void ShowToast(string title, string text)
    {
        if (App.Config.SuppressToastsFullscreen && Native.IsBusyFullscreen()) return;
        _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.None);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(0x22, 0xC7, 0xE6));
            using (var path = RoundedRect(new Rectangle(0, 0, 31, 31), 8)) g.FillPath(bg, path);
            using var pen = new Pen(Color.Black, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new Point(19, 6), new Point(25, 11), new Point(19, 16) });
            g.DrawLine(pen, 7, 11, 24, 11);
            g.DrawLines(pen, new[] { new Point(13, 16), new Point(7, 21), new Point(13, 26) });
            g.DrawLine(pen, 8, 21, 25, 21);
        }
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _iconImage.Dispose();
    }
}
