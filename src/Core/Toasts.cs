using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AutoSwitcher;

/// <summary>
/// Native Windows toast notifications for an unpackaged desktop app.
/// Registers an AppUserModelID under HKCU (name + icon shown in Windows notification settings),
/// then shows silent toasts with the category's box art as the image.
/// Falls back to the tray balloon if toasts aren't available.
/// </summary>
public static class Toasts
{
    public const string Aumid = "Flippage.AutoSwitcher";
    private static bool _registered;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>Call once at startup, before any window is shown.</summary>
    public static void Init()
    {
        try { SetCurrentProcessExplicitAppUserModelID(Aumid); } catch { }
    }

    private static void EnsureRegistered()
    {
        if (_registered) return;
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
        {
            key.SetValue("DisplayName", "AutoSwitcher");
            string? icon = AppIconPng();
            if (icon != null) key.SetValue("IconUri", icon);
            key.SetValue("IconBackgroundColor", "FF000000");
        }
        _registered = true;
    }

    /// <summary>The app icon as a PNG on disk (toasts and Windows settings need a file).</summary>
    private static string? AppIconPng()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoSwitcher");
            string file = Path.Combine(dir, "appicon.png");
            if (File.Exists(file)) return file;
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info == null) return null;
            Directory.CreateDirectory(dir);
            using var stream = info.Stream;
            using var icon = new Icon(stream, 256, 256);
            using var bmp = icon.ToBitmap();
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            return file;
        }
        catch { return null; }
    }

    public static async Task ShowAsync(ToastInfo info, Action<string, string> fallback)
    {
        if (App.Config.SuppressToastsFullscreen && Native.IsBusyFullscreen()) return;
        try
        {
            EnsureRegistered();
            string? image = !string.IsNullOrEmpty(info.BoxArtUrl) ? await ImageCache.GetFileAsync(info.BoxArtUrl) : null;
            image ??= AppIconPng();

            string Esc(string s) => SecurityElement.Escape(s) ?? "";
            string imageXml = image == null ? ""
                : $"<image placement=\"appLogoOverride\" hint-crop=\"none\" src=\"{Esc(new Uri(image).AbsoluteUri)}\"/>";
            string detailXml = string.IsNullOrWhiteSpace(info.Detail) ? "" : $"<text hint-maxLines=\"2\">{Esc(info.Detail!)}</text>";
            string xml =
                "<toast duration=\"short\">" +
                "<visual><binding template=\"ToastGeneric\">" +
                imageXml +
                $"<text hint-maxLines=\"1\">{Esc(info.Heading)}</text>" +
                $"<text>{Esc(info.Body)}</text>" +
                detailXml +
                "</binding></visual>" +
                "<audio silent=\"true\"/>" +          // never let a chime leak onto stream
                "</toast>";

            var doc = new global::Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(xml);
            var toast = new global::Windows.UI.Notifications.ToastNotification(doc)
            {
                Tag = "switch",
                Group = "autoswitcher",                       // a new switch replaces the previous toast
                ExpirationTime = DateTimeOffset.Now.AddMinutes(5),
            };
            global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
        }
        catch
        {
            fallback(info.Heading, info.Body);
        }
    }
}
