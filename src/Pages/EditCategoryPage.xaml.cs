using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AutoSwitcher;

public partial class EditCategoryPage : UserControl
{
    private readonly CategoryMapping? _existing;
    private readonly ObservableCollection<ExeMapping> _exes;

    public EditCategoryPage(CategoryMapping? existing)
    {
        InitializeComponent();
        _existing = existing;
        Heading.Text = existing == null ? "Add category" : "Edit category";
        _exes = new ObservableCollection<ExeMapping>(existing?.Executables.Select(e => e.Clone()) ?? Enumerable.Empty<ExeMapping>());
        _exes.CollectionChanged += (_, _) => UpdateEmpty();
        ExeList.ItemsSource = _exes;
        if (existing != null)
            Picker.SetSelected(new CategoryRef { Id = existing.Id, Name = existing.Name, BoxArtUrl = existing.BoxArtUrl });
        UpdateEmpty();
    }

    private void UpdateEmpty() => NoExes.Visibility = _exes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Best human-readable name baked into the exe (ProductName → FileDescription → file name).</summary>
    public static string ReadFullName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            foreach (var candidate in new[] { info.ProductName, info.FileDescription })
            {
                string c = (candidate ?? "").Trim();
                if (c.Length > 1 && !c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return c;
            }
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private void AddExe(string path)
    {
        // Same exe twice is fine only if one of them uses a title rule (set it after adding).
        if (_exes.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase) && !e.UsesTitle)) return;
        _exes.Add(new ExeMapping { Path = path, FullName = ReadFullName(path) });
        ErrorText.Text = "";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose game executable(s)",
            Filter = "Programs (*.exe)|*.exe",
            Multiselect = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            foreach (string f in dlg.FileNames) AddExe(f);
    }

    private void PickRunning_Click(object sender, RoutedEventArgs e)
    {
        RunningList.ItemsSource = GameWatcher.ListRunningApps();
        RunningPopup.IsOpen = true;
    }

    private void RunningList_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src &&
            ItemsControl.ContainerFromElement(RunningList, src) is ListBoxItem { DataContext: RunningApp app })
        {
            AddExe(app.Path);
            RunningPopup.IsOpen = false;
        }
    }

    private void GrabTitle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: ExeMapping exe }) return;
        string? title = GameWatcher.FindTitleForExe(exe.Path);
        if (title == null)
        {
            ErrorText.Text = $"{exe.FileName} isn't running. Start the game in it, then click Use current title again.";
            return;
        }
        exe.TitlePattern = title;
        ErrorText.Text = "";
    }

    private void RemoveExe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: ExeMapping exe }) _exes.Remove(exe);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.CloseEditor();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cat = Picker.Selected;
        if (cat == null) { ErrorText.Text = "Pick a Twitch category first."; return; }
        if (_exes.Count == 0) { ErrorText.Text = "Add at least one executable."; return; }

        foreach (var x in _exes)
        {
            x.FullName = string.IsNullOrWhiteSpace(x.FullName) ? x.ProcessName : x.FullName.Trim();
            x.CustomName = (x.CustomName ?? "").Trim();
            x.TitlePattern = x.TitlePattern.Trim();
            if (x.MatchTitle && x.TitlePattern.Length == 0)
            {
                ErrorText.Text = $"Enter the window title to match for {x.FileName}, or turn Match window title off.";
                return;
            }
        }
        if (_exes.GroupBy(x => x.Key).Any(g => g.Count() > 1))
        {
            ErrorText.Text = "The same executable is listed twice with the same title rule.";
            return;
        }

        var categories = App.Config.Categories;

        // Each exe + title rule belongs to exactly one category: take matching entries away from other mappings.
        // (An emulator can still appear in several categories with different title rules.)
        var keys = _exes.Select(n => n.Key).ToHashSet();
        foreach (var other in categories.Where(c => !ReferenceEquals(c, _existing)))
            other.Executables.RemoveAll(o => keys.Contains(o.Key));

        // Adding a category that's already mapped merges into it.
        var target = _existing ?? categories.FirstOrDefault(c => c.Id == cat.Id);
        if (target == null)
        {
            target = new CategoryMapping();
            categories.Add(target);
        }
        else if (_existing != null)
        {
            // Changing the category of an existing row onto another existing row: merge.
            var dupe = categories.FirstOrDefault(c => !ReferenceEquals(c, _existing) && c.Id == cat.Id);
            if (dupe != null)
            {
                dupe.Executables.AddRange(_exes);
                categories.Remove(_existing);
                Finish();
                return;
            }
        }

        target.Id = cat.Id;
        target.Name = cat.Name;
        target.BoxArtUrl = cat.BoxArtUrl;
        if (_existing == null && target.Executables.Count > 0)
            target.Executables.AddRange(_exes.Where(n => !target.Executables.Any(o => o.Key == n.Key)));
        else
            target.Executables = _exes.ToList();

        Finish();
    }

    private void Finish()
    {
        App.Config.Categories.RemoveAll(c => c.Executables.Count == 0);
        App.SaveConfig();
        App.Watcher.UpdateMappings(App.Config.Categories);
        (Window.GetWindow(this) as MainWindow)?.CloseEditor();
    }
}
