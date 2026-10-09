using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoSwitcher;

/// <summary>Multiworld = a saved set of mappings. Activating one turns exactly those mappings on and pauses the rest.</summary>
public static class Multiworlds
{
    /// <summary>Raised after mappings are turned on/off from here, so the Mappings list can refresh.</summary>
    public static event Action? MappingsChanged;

    /// <summary>The mappings in a multiworld that still exist, in Mappings order.</summary>
    public static List<CategoryMapping> GamesOf(Multiworld w) =>
        App.Config.Categories.Where(c => w.CategoryIds.Contains(c.Id)).ToList();

    /// <summary>Active = exactly this multiworld's games are on (works even if you toggled them by hand).</summary>
    public static bool IsActive(Multiworld w)
    {
        var cats = App.Config.Categories;
        return GamesOf(w).Count > 0 && cats.All(c => c.Enabled == w.CategoryIds.Contains(c.Id));
    }

    public static void Activate(Multiworld w)
    {
        foreach (var c in App.Config.Categories) c.Enabled = w.CategoryIds.Contains(c.Id);
        Log.Info("multiworld", $"Activated \"{w.Name}\": {string.Join(", ", GamesOf(w).Select(c => c.Name))}");
        Apply();
    }

    public static void AllOn()
    {
        foreach (var c in App.Config.Categories) c.Enabled = true;
        Log.Info("multiworld", "All game mappings turned on");
        Apply();
    }

    private static void Apply()
    {
        App.SaveConfig();
        App.Watcher.UpdateMappings(App.Config.Categories);
        App.Switcher.ForgetIfInactive(App.Config.Categories);
        MappingsChanged?.Invoke();
    }

    public static string UniqueName(string wanted, Multiworld? except = null)
    {
        string name = wanted.Trim();
        if (name.Length == 0) name = "Multiworld";
        bool Taken(string n) => App.Config.Multiworlds.Any(m => !ReferenceEquals(m, except) &&
                                                                string.Equals(m.Name, n, StringComparison.CurrentCultureIgnoreCase));
        if (!Taken(name)) return name;
        for (int i = 2; ; i++) if (!Taken($"{name} ({i})")) return $"{name} ({i})";
    }
}
