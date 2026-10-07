using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoSwitcher;

public partial class ManualPage : UserControl
{
    private string? _artForGameId;
    private bool _titlePrefilled;
    private OverflowHighlighter? _highlighter;

    public ManualPage()
    {
        InitializeComponent();
        App.Switcher.Changed += () => Dispatcher.InvokeAsync(UpdateView);
        Loaded += (_, _) => _highlighter ??= OverflowHighlighter.Attach(TitleBox, Switcher.MaxTitle);
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

        UpdateRecent();
        UpdateCounter();
        UpdateGamePills();
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

    /// <summary>
    /// Older saved titles may still contain %variables%; fill them from the picked/detected game so
    /// Twitch never receives them literally.
    /// </summary>
    private string Rendered() => Render(TitleBox.Text);

    private string Render(string template)
    {
        var hit = App.Switcher.Current;
        var picked = Picker.Selected;
        string game = picked?.Name ?? hit?.Category.Name ?? App.Switcher.Live?.GameName ?? "";
        bool exeMatches = hit != null && (picked == null || picked.Id == hit.Category.Id);
        string full = exeMatches ? hit!.Exe.FullName : game;
        string custom = exeMatches ? hit!.Exe.EffectiveCustom : game;
        return Switcher.Render(template, game, full, custom);
    }

    private void UpdateCounter()
    {
        int len = TitleBox.Text.Length;
        bool over = len > Switcher.MaxTitle;
        Counter.Text = $"{len} / {Switcher.MaxTitle}";
        Counter.Foreground = (Brush)FindResource(over ? "DangerBrush" : "SubBrush");
        OverflowNote.Visibility = over ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateGamePills()
    {
        var cats = App.Config.Categories.Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Id).Select(g => g.First()).ToList();
        GamePills.ItemsSource = cats;
        GamePanel.Visibility = cats.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Inserts the game's name at the cursor and selects it as the new category.</summary>
    private void InsertGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: CategoryMapping cat }) return;
        Picker.SetSelected(new CategoryRef { Id = cat.Id, Name = cat.Name, BoxArtUrl = cat.BoxArtUrl });

        string name = cat.Name;
        int caret = TitleBox.SelectionStart;
        string text = TitleBox.Text.Remove(caret, TitleBox.SelectionLength);   // typing over a selection replaces it
        TitleBox.Text = text.Insert(caret, name);
        TitleBox.Focus();
        TitleBox.CaretIndex = caret + name.Length;
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateCounter();

    /// <summary>Same list as Stream Titles. Saved titles with name pills are filled in when picked.</summary>
    private void UpdateRecent()
    {
        var rows = RecentTitleRows.Build(this, TitleBox.Text,
            pick: t => { TitleBox.Text = Render(t); TitleBox.Focus(); TitleBox.CaretIndex = TitleBox.Text.Length; },
            delete: t => { App.Config.RecentTitles.Remove(t); App.SaveConfig(); UpdateRecent(); });
        RecentList.ItemsSource = rows;
        RecentPanel.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        string? rendered = title != null ? Rendered() : null;   // fill %variables% before sending
        bool ok = await App.Switcher.ApplyAsync(gameId, gameName, rendered, gameName, "Set manually",
            Picker.Selected?.Id == gameId ? Picker.Selected?.BoxArtUrl : null);
        if (ok)
        {
            if (title != null) App.RememberTitle(title);              // keep the template, variables included
            _artForGameId = null;
            SetStatus("Updated.", error: false);
            UpdateView();
        }
        else SetStatus(App.Switcher.LastError ?? "Update failed.", error: true);
    }


    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "DangerBrush" : "GoodBrush");
    }
}
