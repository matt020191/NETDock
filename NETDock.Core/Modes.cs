using System.Text.Json;

namespace NETDock.Core;

/// <summary>A named set of dock icons plus, optionally, where its apps' windows go (Aero Dock's modes).</summary>
public sealed class DockMode
{
    public string Name { get; set; } = "";
    /// <summary>The icons while this mode isn't active (the active mode's icons live in <see cref="DockSettings.Items"/>).</summary>
    public List<DockItem> Items { get; set; } = [];
    /// <summary>Saved window positions, restored when switching to this mode.</summary>
    public List<WindowLayout> Windows { get; set; } = [];
}

/// <summary>One saved window: matched by app key, then title, then order among that app's windows.</summary>
public sealed class WindowLayout
{
    public string Key { get; set; } = "";
    public string? Title { get; set; }
    public int Index { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Switching, creating and removing modes. The active mode's items are always <see cref="DockSettings.Items"/>.</summary>
public static class ModeSwitcher
{
    public const string DefaultName = "Default";

    /// <summary>The active mode's entry, created (as "Default") the first time modes are used.</summary>
    public static DockMode Active(DockSettings s)
    {
        s.Modes ??= [];
        var m = Find(s, s.ActiveMode);
        if (m is null)
        {
            m = new DockMode { Name = UniqueName(s, s.ActiveMode is { Length: > 0 } a ? a : DefaultName) };
            s.Modes.Insert(0, m);
            s.ActiveMode = m.Name;
        }
        return m;
    }

    public static DockMode? Find(DockSettings s, string? name) =>
        name is null ? null : s.Modes?.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Park the current icons in the active mode and bring in <paramref name="name"/>'s. False when it doesn't exist or is already active.</summary>
    public static bool Switch(DockSettings s, string name)
    {
        var from = Active(s);
        var to = Find(s, name);
        if (to is null || to == from) return false;
        from.Items = s.Items;
        s.Items = to.Items;
        to.Items = [];
        s.ActiveMode = to.Name;
        return true;
    }

    /// <summary>A new mode starting with a copy of the current icons (fresh ids), made active.</summary>
    public static DockMode Create(DockSettings s, string name)
    {
        var from = Active(s);
        var copy = s.Items.Select(Clone).ToList();
        var m = new DockMode { Name = UniqueName(s, name) };
        s.Modes.Add(m);
        from.Items = s.Items;
        s.Items = copy;
        s.ActiveMode = m.Name;
        return m;
    }

    public static void Rename(DockSettings s, string newName)
    {
        var m = Active(s);
        var name = newName.Trim();
        if (name.Length == 0 || string.Equals(name, m.Name, StringComparison.Ordinal)) return;
        m.Name = string.Equals(name, m.Name, StringComparison.OrdinalIgnoreCase) ? name : UniqueName(s, name);
        s.ActiveMode = m.Name;
    }

    /// <summary>Delete the active mode and switch to the first other one. False when it's the only mode.</summary>
    public static bool DeleteActive(DockSettings s)
    {
        var m = Active(s);
        var next = s.Modes.FirstOrDefault(x => x != m);
        if (next is null) return false;
        s.Modes.Remove(m);
        s.Items = next.Items;
        next.Items = [];
        s.ActiveMode = next.Name;
        return true;
    }

    public static string UniqueName(DockSettings s, string name)
    {
        name = name.Trim();
        if (name.Length == 0) name = "Mode";
        if (name.Length > 40) name = name[..40];
        if (Find(s, name) is null) return name;
        for (var n = 2; ; n++)
            if (Find(s, $"{name} {n}") is null) return $"{name} {n}";
    }

    public static DockItem Clone(DockItem item)
    {
        var c = JsonSerializer.Deserialize(JsonSerializer.Serialize(item, SettingsJson.Default.DockItem), SettingsJson.Default.DockItem)!;
        c.Id = Guid.NewGuid().ToString("n");
        return c;
    }

    /// <summary>Pair saved layouts with live windows: same key, then same title, then the remaining ones in order.</summary>
    public static List<(WindowLayout Layout, T Window)> Match<T>(IEnumerable<WindowLayout> saved, IReadOnlyDictionary<string, List<(T Window, string Title)>> live)
    {
        var result = new List<(WindowLayout, T)>();
        foreach (var group in saved.GroupBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!live.TryGetValue(group.Key, out var windows)) continue;
            var free = windows.ToList();
            var pending = new List<WindowLayout>();
            foreach (var w in group.OrderBy(w => w.Index))
            {
                var i = w.Title is null ? -1 : free.FindIndex(f => f.Title == w.Title);
                if (i >= 0) { result.Add((w, free[i].Window)); free.RemoveAt(i); }
                else pending.Add(w);
            }
            for (var i = 0; i < pending.Count && i < free.Count; i++) result.Add((pending[i], free[i].Window));
        }
        return result;
    }
}
