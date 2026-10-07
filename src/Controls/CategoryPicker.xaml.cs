using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace AutoSwitcher;

/// <summary>Search box + Twitch category results with box art. Debounced (300 ms), cancels stale requests.</summary>
public partial class CategoryPicker : UserControl
{
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? _cts;

    public CategoryRef? Selected { get; private set; }
    public event Action<CategoryRef?>? SelectionChanged;

    public double ResultsMaxHeight
    {
        get => Results.MaxHeight;
        set => Results.MaxHeight = value;
    }

    public CategoryPicker()
    {
        InitializeComponent();
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await SearchAsync();
        };
    }

    public void SetSelected(CategoryRef? category)
    {
        Selected = category;
        SelectedCard.Visibility = category == null ? Visibility.Collapsed : Visibility.Visible;
        SelectedName.Text = category?.Name ?? "";
        Art.SetUrl(SelectedArt, category?.BoxArtUrl);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SetSelected(null);
        SelectionChanged?.Invoke(null);
        Query.Focus();
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && Results.Visibility == Visibility.Visible && Results.Items.Count > 0)
        {
            var item = Results.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            item?.Focus();
            e.Handled = true;
        }
    }

    private async System.Threading.Tasks.Task SearchAsync()
    {
        string q = Query.Text.Trim();
        _cts?.Cancel();
        if (q.Length < 2)
        {
            HideResults();
            ShowStatus(null);
            return;
        }
        if (!App.Twitch.IsSignedIn)
        {
            HideResults();
            ShowStatus("Connect your Twitch account (Account page) to search categories.");
            return;
        }

        var cts = _cts = new CancellationTokenSource();
        try
        {
            var list = await App.Twitch.SearchCategoriesAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;
            Results.ItemsSource = list;
            Results.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowStatus(list.Count == 0 ? "No categories found." : null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowStatus("Search failed: " + ex.Message); }
    }

    private void Results_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src &&
            ItemsControl.ContainerFromElement(Results, src) is ListBoxItem { DataContext: CategoryRef c })
            Pick(c);
    }

    private void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Results.SelectedItem is CategoryRef c)
        {
            Pick(c);
            e.Handled = true;
        }
    }

    private void Pick(CategoryRef c)
    {
        SetSelected(c);
        HideResults();
        Query.TextChanged -= Query_TextChanged;
        Query.Text = "";
        Query.TextChanged += Query_TextChanged;
        SelectionChanged?.Invoke(c);
    }

    private void HideResults()
    {
        Results.ItemsSource = null;
        Results.Visibility = Visibility.Collapsed;
    }

    private void ShowStatus(string? text)
    {
        Status.Text = text ?? "";
        Status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
