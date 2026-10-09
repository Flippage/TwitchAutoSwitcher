using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoSwitcher;

/// <summary>
/// Multiworld = a saved set of mappings.
///  • Activate: remember which games were on (only if no multiworld was active yet), then turn exactly its games on.
///  • Deactivate: put every game back how it was before the first activation.
///  • Changing a mapping by hand so it no longer matches deactivates it (keeping your change; nothing is reverted).
/// </summary>
public static class Multiworlds
{
    /// <summary>Raised after mappings or the active multiworld change, so pages can refresh.</summary>
    public static event Action? MappingsChanged;

    private static AppConfig Cfg => App.Config;

    public static Multiworld? Active => Cfg.ActiveMultiworldId is { } id ? Cfg.Multiworlds.FirstOrDefault(m => m.Id == id) : null;

    /// <summary>The mappings in a multiworld that still exist, in Mappings order.</summary>
    public static List<CategoryMapping> GamesOf(Multiworld w) => Cfg.Categories.Where(c => w.CategoryIds.Contains(c.Id)).ToList();

    public static bool IsActive(Multiworld w) => Cfg.ActiveMultiworldId == w.Id;

    /// <summary>Exactly this multiworld's games are on.</summary>
    private static bool Matches(Multiworld w) =>
        GamesOf(w).Count > 0 && Cfg.Categories.All(c => c.Enabled == w.CategoryIds.Contains(c.Id));

    public static void Activate(Multiworld w)
    {
        if (Active == null)   // first activation: remember how things were (switching worlds keeps the original)
            Cfg.PreMultiworldEnabled = Cfg.Categories.ToDictionary(c => c.Id, c => c.Enabled);
        foreach (var c in Cfg.Categories) c.Enabled = w.CategoryIds.Contains(c.Id);
        Cfg.ActiveMultiworldId = w.Id;
        Log.Info("multiworld", $"Activated \"{w.Name}\": {string.Join(", ", GamesOf(w).Select(c => c.Name))}");
        Apply();
    }

    /// <summary>Turn the active multiworld off and restore each game's on/off state from before it was activated.</summary>
    public static void Deactivate()
    {
        var w = Active;
        var before = Cfg.PreMultiworldEnabled;
        if (before != null)
            foreach (var c in Cfg.Categories)
                if (before.TryGetValue(c.Id, out bool on)) c.Enabled = on;   // games added since keep their current state
        Clear();
        Log.Info("multiworld", $"Deactivated \"{w?.Name}\"; games restored to how they were");
        Apply();
    }

    public static void AllOn()
    {
        foreach (var c in Cfg.Categories) c.Enabled = true;
        Clear();
        Log.Info("multiworld", "All game mappings turned on");
        Apply();
    }

    public static void AllOff()
    {
        foreach (var c in Cfg.Categories) c.Enabled = false;
        Clear();
        Log.Info("multiworld", "All game mappings turned off");
        Apply();
    }

    /// <summary>
    /// Call after mappings are changed by hand (toggle, add, edit, delete). If the active multiworld no longer
    /// matches what's on, it's deactivated without restoring anything, so the change you just made sticks.
    /// </summary>
    public static void MappingsEdited()
    {
        var w = Active;
        if (Cfg.ActiveMultiworldId != null && (w == null || !Matches(w)))
        {
            Clear();
            Log.Info("multiworld", $"\"{w?.Name}\" deactivated: the games turned on no longer match it");
            App.SaveConfig();
        }
        MappingsChanged?.Invoke();
    }

    /// <summary>The active multiworld's games were edited: apply the new set (keeping the original snapshot).</summary>
    public static void Reapply(Multiworld w)
    {
        if (IsActive(w)) Activate(w);
    }

    /// <summary>Deleting a multiworld: if it's active, put the games back first.</summary>
    public static void Delete(Multiworld w)
    {
        if (IsActive(w)) Deactivate();
        Cfg.Multiworlds.Remove(w);
        App.SaveConfig();
        MappingsChanged?.Invoke();
    }

    private static void Clear()
    {
        Cfg.ActiveMultiworldId = null;
        Cfg.PreMultiworldEnabled = null;
    }

    private static void Apply()
    {
        App.SaveConfig();
        App.Watcher.UpdateMappings(Cfg.Categories);
        App.Switcher.ForgetIfInactive(Cfg.Categories);
        MappingsChanged?.Invoke();
    }

    public static string UniqueName(string wanted, Multiworld? except = null)
    {
        string name = wanted.Trim();
        if (name.Length == 0) name = "Multiworld";
        bool Taken(string n) => Cfg.Multiworlds.Any(m => !ReferenceEquals(m, except) &&
                                                        string.Equals(m.Name, n, StringComparison.CurrentCultureIgnoreCase));
        if (!Taken(name)) return name;
        for (int i = 2; ; i++) if (!Taken($"{name} ({i})")) return $"{name} ({i})";
    }
}
