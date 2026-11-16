namespace NETDock.Core;

public enum DropAction
{
    /// <summary>Add the dropped files as new dock items at the insertion point.</summary>
    Insert,
    /// <summary>Open the files with the target app (RocketDock: PNG onto Photoshop).</summary>
    OpenWith,
    /// <summary>Copy the files into the target folder or stack, like Explorer.</summary>
    CopyInto,
    /// <summary>Send the files to the Recycle Bin.</summary>
    Recycle,
}

public static class DropRules
{
    /// <summary>Fraction of an icon, centred, that counts as "on" it. Its outer edges insert instead.</summary>
    public const double OntoZone = 0.6;

    /// <param name="item">The icon under the drop point, or null for a gap or empty shelf.</param>
    /// <param name="position">Drop point across that icon, 0 = leading edge, 1 = trailing edge.</param>
    public static DropAction Decide(DockItem? item, double position)
    {
        if (item is null || item.Kind == ItemKind.Separator) return DropAction.Insert;
        var margin = (1 - OntoZone) / 2;
        if (position < margin || position > 1 - margin) return DropAction.Insert;
        if (RecycleBin.Is(item)) return DropAction.Recycle;
        if (item.Kind == ItemKind.Stack) return DropAction.CopyInto;
        // Only real programs take files; shell folders like This PC (::{...}) don't.
        return item.Target.StartsWith("::", StringComparison.Ordinal) || SpecialItems.Is(item) ? DropAction.Insert : DropAction.OpenWith;
    }

    /// <summary>Arguments for opening dropped paths with an app: its own arguments, then each path quoted.</summary>
    public static string OpenWithArguments(string? existing, IEnumerable<string> paths)
    {
        var quoted = string.Join(' ', paths.Select(p => $"\"{p}\""));
        return string.IsNullOrWhiteSpace(existing) ? quoted : existing + " " + quoted;
    }
}
