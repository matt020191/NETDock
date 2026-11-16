namespace NETDock.Core;

/// <summary>"Monitor *" (RocketDock): one long edge across every monitor.</summary>
public static class MonitorSpan
{
    /// <summary><see cref="DockOptions.Monitor"/> value meaning "all monitors".</summary>
    public const int All = -1;

    /// <summary>
    /// The area a spanning dock lives in: the monitors' work areas joined along the edge. Across the edge it uses
    /// the overlap of the work areas (e.g. the highest bottom for a bottom dock), so the dock is on screen on every
    /// monitor even when they differ in height or taskbar placement.
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom) Union(IReadOnlyList<(int Left, int Top, int Right, int Bottom)> work, DockEdge edge)
    {
        if (work.Count == 0) return (0, 0, 0, 0);
        int l = work.Min(w => w.Left), r = work.Max(w => w.Right), t = work.Min(w => w.Top), b = work.Max(w => w.Bottom);
        switch (edge)
        {
            case DockEdge.Bottom: b = work.Min(w => w.Bottom); t = Math.Min(t, b); break;
            case DockEdge.Top: t = work.Max(w => w.Top); b = Math.Max(b, t); break;
            case DockEdge.Left: l = work.Max(w => w.Left); r = Math.Max(r, l); break;
            case DockEdge.Right: r = work.Min(w => w.Right); l = Math.Min(l, r); break;
        }
        return (l, t, r, b);
    }
}
