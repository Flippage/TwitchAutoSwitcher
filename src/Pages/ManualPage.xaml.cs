using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoSwitcher;

public partial class ManualPage : UserControl
{
    private string? _artForGameId;
    private bool _titlePrefilled;

    public ManualPage()
    {
        InitializeComponent();
        App.Switcher.Changed += () => Dispatcher.InvokeAsync(UpdateView);
        IsVisibleChanged += async (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            UpdateView();
            await App.Switcher.RefreshLiveAsync();
        };
        UpdateView();
    }

    private void UpdateView()
    {
        bool auto = App.Config.AutoSwitch;
        if (auto)
        {
            Banner.Background = (Brush)FindResource("AccentDimBrush");
            Banner.BorderBrush = (Brush)FindResource("AccentLineBrush");
            BannerText.Foreground = (Brush)FindResource("Text2Brush");
            BannerText.Text = "Auto-switch is on. Applying anything here pauses it, so your manual choice isn't overwritten by the next game.";
            BannerIcon.Visibility = Visibility.Collapsed;
            ResumeBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            Banner.Background = (Brush)FindResource("WarnBgBrush");
            Banner.BorderBrush = (Brush)FindResource("WarnLineBrush");
            BannerText.Foreground = (Brush)FindResource("WarnTextBrush");
            BannerText.Text = "Auto-switch is paused. Launching or focusing a mapped game won't change your category.";
            BannerIcon.Visibility = Visibility.Visible;
            ResumeBtn.Visibility = Visibility.Visible;
        }

        var live = App.Switcher.Live;
        if (!App.Twitch.IsSignedIn)
        {
            LiveGame.Text = "Not connected";
            LiveTitle.Text = "Connect your Twitch account on the Account page.";
            Art.SetUrl(LiveArt, null);
        }
        else if (live == null)
        {
            LiveGame.Text = "Loading…";
            LiveTitle.Text = "";
        }
        else
        {
            LiveGame.Text = string.IsNullOrEmpty(live.GameName) ? "No category" : live.GameName;
            LiveTitle.Text = live.Title;
            _ = LoadArtAsync(live.GameId);
            if (!_titlePrefilled && string.IsNullOrEmpty(TitleBox.Text))
            {
                TitleBox.Text = live.Title;
                _titlePrefilled = true;
            }
        }

        RecentList.ItemsSource = null;
        RecentList.ItemsSource = App.Config.RecentTitles;
        RecentPanel.Visibility = App.Config.RecentTitles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCounter();
    }

    private async Task LoadArtAsync(string gameId)
    {
        if (_artForGameId == gameId) return;
        _artForGameId = gameId;
        try
        {
            var game = await App.Twitch.GetGameAsync(gameId);
            if (_artForGameId == gameId) Art.SetUrl(LiveArt, game?.BoxArtUrl);
        }
        catch { }
    }

    private void UpdateCounter() => Counter.Text = $"{TitleBox.Text.Length} / {Switcher.MaxTitle}";

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateCounter();

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string t }) TitleBox.Text = t;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _artForGameId = null;
        await App.Switcher.RefreshLiveAsync();
    }

    private void Resume_Click(object sender, RoutedEventArgs e) => App.SetAutoSwitch(true);

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var cat = Picker.Selected;
        string title = TitleBox.Text.Trim();
        if (cat == null && title.Length == 0) { SetStatus("Choose a category or type a title.", error: true); return; }
        await ApplyAsync(cat?.Id, cat?.Name, title.Length > 0 ? title : null);
    }

    private async void TitleOnly_Click(object sender, RoutedEventArgs e)
    {
        string title = TitleBox.Text.Trim();
        if (title.Length == 0) { SetStatus("Type a title first.", error: true); return; }
        await ApplyAsync(null, null, title);
    }

    private async Task ApplyAsync(string? gameId, string? gameName, string? title)
    {
        App.SetAutoSwitch(false);
        SetStatus("Updating Twitch…", error: false);
        bool ok = await App.Switcher.ApplyAsync(gameId, gameName, title, gameName);
        if (ok)
        {
            if (title != null) RememberTitle(title);
            _artForGameId = null;
            SetStatus("Updated.", error: false);
            UpdateView();
        }
        else SetStatus(App.Switcher.LastError ?? "Update failed.", error: true);
    }

    private static void RememberTitle(string title)
    {
        var list = App.Config.RecentTitles;
        list.Remove(title);
        list.Insert(0, title);
        if (list.Count > 5) list.RemoveRange(5, list.Count - 5);
        App.SaveConfig();
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "DangerBrush" : "GoodBrush");
    }
}
