using System.Collections.Concurrent;
using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// Loads icons on one background STA thread (Shell COM needs an apartment) and notifies
/// windows when they arrive. Decoded pixels are shared; each window makes its own D2D bitmap.
/// </summary>
public sealed class IconService
{
    private readonly ConcurrentDictionary<string, IconImage?> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private readonly BlockingCollection<(string Key, Func<IconImage?> Load)> _queue = new();
    private readonly List<nint> _listeners = [];
    public const uint WM_ICONS_READY = Win32.WM_APP + 1;
    private int _posted;

    public IconService()
    {
        var t = new Thread(Worker) { IsBackground = true, Name = "NETDock icons" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public void AddListener(nint hwnd) { lock (_listeners) _listeners.Add(hwnd); }
    public void RemoveListener(nint hwnd) { lock (_listeners) _listeners.Remove(hwnd); }

    /// <summary>The icon if it is ready; otherwise queues it and returns null.</summary>
    public IconImage? Get(string key, Func<IconImage?> load)
    {
        if (_cache.TryGetValue(key, out var img)) return img;
        if (_pending.TryAdd(key, 0)) _queue.Add((key, load));
        return null;
    }

    public bool IsLoaded(string key) => _cache.ContainsKey(key);

    public void Forget(Func<string, bool> match)
    {
        foreach (var k in _cache.Keys) if (match(k)) _cache.TryRemove(k, out _);
    }

    private void Worker()
    {
        Win32.CoInitializeEx(0, 2); // apartment threaded
        foreach (var (key, load) in _queue.GetConsumingEnumerable())
        {
            IconImage? img = null;
            try { img = load(); } catch { /* a broken icon must not stop the queue */ }
            _cache[key] = img;
            _pending.TryRemove(key, out _);
            // Coalesce: one repaint per burst, not one per icon.
            if (Interlocked.Exchange(ref _posted, 1) == 0)
                lock (_listeners) foreach (var h in _listeners) Win32.PostMessage(h, WM_ICONS_READY, 0, 0);
        }
    }

    public void Acknowledge() => Interlocked.Exchange(ref _posted, 0);

    // ---- what to load for a given reference ----

    public static Func<IconImage?> ForItem(DockItem item, SettingsStore store, PathTokens tokens, int size)
    {
        var icon = item.Icon;
        var target = tokens.Expand(item.Target) ?? "";
        return () =>
        {
            if (store.ResolveDataIcon(icon) is { } dataFile)
                return Shell.ShellImage(dataFile, size, preferThumbnail: true);
            if (icon is not null)
            {
                var comma = icon.LastIndexOf(',');
                var file = tokens.Expand(comma > 0 ? icon[..comma] : icon)!;
                var index = comma > 0 && int.TryParse(icon[(comma + 1)..], out var n) ? n : 0;
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".ico" or ".tif" or ".tiff")
                    return Shell.ShellImage(file, size, preferThumbnail: true);
                return Shell.ResourceIcon(file, index, size) ?? Shell.ShellImage(file, size);
            }
            if (item.Kind == ItemKind.Shortcut && !File.Exists(target) && item.Fallback is { } fb)
                target = tokens.Expand(fb.Target) ?? target;
            return Shell.ShellImage(target, size);
        };
    }

    public static string KeyFor(DockItem item, int size) => $"item|{item.Icon ?? item.Target}|{size}";
}

/// <summary>Starts things. Never edits the item it is given.</summary>
public static unsafe class Launcher
{
    public static void Launch(DockItem item, PathTokens tokens, bool elevate = false)
    {
        var target = tokens.Expand(item.Target) ?? "";
        var args = item.Arguments;
        string? dir = tokens.Expand(item.WorkingDirectory);

        if (item.Kind == ItemKind.Shortcut)
        {
            if (!File.Exists(target) && item.Fallback is { } fb)
            {
                target = tokens.Expand(fb.Target) ?? target;
                args ??= fb.Arguments;
                dir ??= tokens.Expand(fb.WorkingDirectory);
            }
            // Otherwise the .lnk itself carries Start In; the shell applies it.
        }
        if (target.StartsWith("::", StringComparison.Ordinal)) target = "shell:" + target;
        if (dir is null && item.Kind == ItemKind.App && Path.IsPathRooted(target) && File.Exists(target))
            dir = Path.GetDirectoryName(target);

        Start(target, args, dir, elevate || item.RunAsAdmin ? "runas" : null);
    }

    public static void Open(string path, string? verb = null) => Start(path, null, null, verb);

    public static void ShowInExplorer(string path) =>
        Start("explorer.exe", $"/select,\"{path}\"", null, null);

    private static void Start(string file, string? args, string? dir, string? verb)
    {
        // ShellExecute can block on slow handlers; keep it off the UI thread.
        var t = new Thread(() =>
        {
            Win32.CoInitializeEx(0, 2 | 4);
            fixed (char* f = file)
            fixed (char* a = args)
            fixed (char* d = dir)
            fixed (char* v = verb)
            {
                var sei = new SHELLEXECUTEINFOW
                {
                    cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                    // Zone checks stay on: a downloaded file opened from a stack still gets its Mark-of-the-Web prompt.
                    fMask = 0x00000100 /* SEE_MASK_ASYNCOK */,
                    lpFile = f, lpParameters = args is null ? null : a, lpDirectory = dir is null ? null : d,
                    lpVerb = verb is null ? null : v, nShow = Win32.SW_SHOWNORMAL,
                };
                Win32.ShellExecuteEx(&sei);
            }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}

/// <summary>Top-level windows grouped by process image, for running indicators and click-to-activate.</summary>
public static unsafe class RunningApps
{
    private static Dictionary<string, List<nint>> _scratch = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<uint, string?> PidPaths = [];

    public static Dictionary<string, List<nint>> Snapshot()
    {
        _scratch = new(StringComparer.OrdinalIgnoreCase);
        PidPaths.Clear();
        Win32.EnumWindows(&Callback, 0);
        return _scratch;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int Callback(nint hwnd, nint _)
    {
        if (!Win32.IsWindowVisible(hwnd) || Win32.GetWindow(hwnd, 4 /* GW_OWNER */) != 0) return 1;
        var ex = (long)Win32.GetWindowLongPtr(hwnd, -20);
        if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && (ex & 0x40000 /* APPWINDOW */) == 0) return 1;
        var title = stackalloc char[2];
        if (Win32.GetWindowText(hwnd, title, 2) == 0) return 1;
        uint pid;
        Win32.GetWindowThreadProcessId(hwnd, &pid);
        if (!PidPaths.TryGetValue(pid, out var path)) PidPaths[pid] = path = ProcessPath(pid);
        if (path is null) return 1;
        if (!_scratch.TryGetValue(path, out var list)) _scratch[path] = list = [];
        list.Add(hwnd);
        return 1;
    }

    private static string? ProcessPath(uint pid)
    {
        var h = Win32.OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == 0) return null;
        try
        {
            var buf = stackalloc char[1024];
            uint len = 1024;
            return Win32.QueryFullProcessImageName(h, 0, buf, &len) ? new string(buf, 0, (int)len) : null;
        }
        finally { Win32.CloseHandle(h); }
    }

    public static void Activate(nint hwnd)
    {
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        Win32.SetForegroundWindow(hwnd);
    }

    /// <summary>The exe an item would run, for matching against running windows.</summary>
    public static string? ExeFor(DockItem item, PathTokens tokens)
    {
        var t = item.Kind == ItemKind.Shortcut ? item.Fallback?.Target : item.Target;
        t = tokens.Expand(t);
        return t is not null && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t : null;
    }
}

public static class ItemFactory
{
    public static DockItem? FromPath(string path, PathTokens tokens)
    {
        path = path.TrimEnd('\\');
        if (path.Length == 2 && path[1] == ':') path += "\\";
        if (Directory.Exists(path))
        {
            var name = Path.GetFileName(path.TrimEnd('\\'));
            return new DockItem
            {
                Kind = ItemKind.Stack,
                Name = string.IsNullOrEmpty(name) ? path : name,
                Target = tokens.Tokenize(path)!,
                Stack = new StackOptions(),
            };
        }
        if (!File.Exists(path)) return null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var item = new DockItem { Name = Path.GetFileNameWithoutExtension(path), Target = tokens.Tokenize(path)! };
        if (ext == ".lnk")
        {
            // Keep the .lnk: launching it lets the shell apply the shortcut's own Start In,
            // so the dock never holds a copy that can go stale.
            item.Kind = ItemKind.Shortcut;
            if (Shell.ReadShortcut(path) is { } s && s.Target.Length > 0)
            {
                item.Fallback = new ShortcutFallback
                {
                    Target = tokens.Tokenize(s.Target)!,
                    Arguments = s.Arguments,
                    WorkingDirectory = tokens.Tokenize(s.WorkingDirectory),
                };
            }
        }
        return item;
    }
}
