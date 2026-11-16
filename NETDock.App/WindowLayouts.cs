using System.Runtime.InteropServices;
using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>Capture and restore where app windows sit, for a mode's window layout.</summary>
public static unsafe partial class WindowLayouts
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT { public uint length, flags, showCmd; public POINT minPos, maxPos; public RECT normal; }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowPlacement(nint h, WINDOWPLACEMENT* p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPlacement(nint h, WINDOWPLACEMENT* p);

    /// <summary>Every visible app window, keyed by the key that identifies its app best (AUMID, else exe).</summary>
    /// <param name="allKeys">Also list each window under every other key it has (restore matches any of them).</param>
    private static Dictionary<string, List<(nint Window, string Title)>> Live(nint skip, bool allKeys = false)
    {
        var seen = new HashSet<nint>();
        var result = new Dictionary<string, List<(nint, string)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var hwnds in RunningApps.Snapshot().Values)
            foreach (var h in hwnds)
            {
                if (h == skip || !seen.Add(h)) continue;
                var keys = RunningApps.KeysOfWindow(h);
                if (keys.Count == 0) continue;
                var best = keys.FirstOrDefault(k => k.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase)) ?? keys[0];
                var title = Title(h);
                foreach (var key in allKeys ? keys.Distinct(StringComparer.OrdinalIgnoreCase) : [best])
                {
                    if (!result.TryGetValue(key, out var list)) result[key] = list = [];
                    list.Add((h, title));
                }
            }
        return result;
    }

    private static string Title(nint h)
    {
        var buf = stackalloc char[256];
        var n = Win32.GetWindowText(h, buf, 256);
        return new string(buf, 0, n);
    }

    /// <summary>Where every app window is now (minimised ones are left out).</summary>
    public static List<WindowLayout> Capture(nint dock)
    {
        var list = new List<WindowLayout>();
        foreach (var (key, windows) in Live(dock))
            for (var i = 0; i < windows.Count; i++)
            {
                var (h, title) = windows[i];
                var p = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
                if (!GetWindowPlacement(h, &p) || p.showCmd == 2 /* SW_SHOWMINIMIZED */ || Win32.IsIconic(h)) continue;
                list.Add(new WindowLayout
                {
                    Key = key, Title = title, Index = i,
                    Left = p.normal.Left, Top = p.normal.Top, Right = p.normal.Right, Bottom = p.normal.Bottom,
                    Maximized = p.showCmd == 3, // SW_SHOWMAXIMIZED
                });
            }
        return list;
    }

    /// <summary>Move the windows that are open now to their saved places; returns how many moved.</summary>
    public static int Restore(IEnumerable<WindowLayout> saved, nint dock)
    {
        var moved = 0;
        foreach (var (w, h) in ModeSwitcher.Match(saved, Live(dock, allKeys: true)))
        {
            var p = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
            if (!GetWindowPlacement(h, &p)) continue;
            p.flags = 0;
            p.normal = new RECT { Left = w.Left, Top = w.Top, Right = w.Right, Bottom = w.Bottom };
            p.showCmd = w.Maximized ? 3u : 4u; // SW_SHOWMAXIMIZED / SW_SHOWNOACTIVATE (restores without stealing focus)
            if (SetWindowPlacement(h, &p)) moved++;
        }
        return moved;
    }
}
