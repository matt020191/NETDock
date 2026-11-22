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
            return Shell.AppImage(target, size);
        };
    }

    public static string KeyFor(DockItem item, int size) => $"item|{item.Icon ?? item.Target}|{size}";
}

/// <summary>Starts things. Never edits the item it is given.</summary>
public static unsafe class Launcher
{
    /// <param name="arguments">Replaces the item's own arguments (used to open dropped files with it).</param>
    public static void Launch(DockItem item, PathTokens tokens, bool elevate = false, string? arguments = null)
    {
        var target = tokens.Expand(item.Target) ?? "";
        var args = arguments ?? item.Arguments;
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

        Start(target, args, dir, elevate || item.RunAsAdmin ? "runas" : null, item.Run switch
        {
            RunWindow.Minimized => 7, // SW_SHOWMINNOACTIVE
            RunWindow.Maximized => 3, // SW_SHOWMAXIMIZED
            _ => Win32.SW_SHOWNORMAL,
        });
    }

    public static void Open(string path, string? verb = null) => Start(path, null, null, verb);

    public static void ShowInExplorer(string path) =>
        Start("explorer.exe", $"/select,\"{path}\"", null, null);

    private static void Start(string file, string? args, string? dir, string? verb, int show = Win32.SW_SHOWNORMAL)
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
                    lpVerb = verb is null ? null : v, nShow = show,
                };
                Win32.ShellExecuteEx(&sei);
            }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}

/// <summary>
/// Watches whether the Recycle Bin has anything in it (polled off the UI thread, since the
/// query can be slow on big drives) and recycles dropped files through the shell.
/// </summary>
public static unsafe class RecycleBinService
{
    public const uint WM_BIN_CHANGED = Win32.WM_APP + 4;
    private static Timer? _timer;
    private static int _full = -1;
    private static nint _notify;

    public static bool IsFull => _full == 1;

    public static void Start(nint notifyHwnd)
    {
        _notify = notifyHwnd;
        _timer = new Timer(_ => Poll(), null, 0, 2000);
    }

    public static void Poll()
    {
        var info = new SHQUERYRBINFO { cbSize = (uint)sizeof(SHQUERYRBINFO) };
        if (Win32.SHQueryRecycleBin(null, &info) < 0) return;
        var full = info.i64NumItems > 0 ? 1 : 0;
        if (Interlocked.Exchange(ref _full, full) != full) Win32.PostMessage(_notify, WM_BIN_CHANGED, 0, 0);
    }

    /// <summary>Send files to the Recycle Bin, with the shell's usual confirmation and progress UI.</summary>
    public static void Recycle(nint owner, IReadOnlyList<string> paths) =>
        ShellFiles.Run(owner, 3 /* FO_DELETE + FOF_ALLOWUNDO = recycle */, paths, null);

    public static void Empty(nint owner)
    {
        var t = new Thread(() =>
        {
            Win32.CoInitializeEx(0, 2);
            Win32.SHEmptyRecycleBin(owner, null, 0); // shell asks for confirmation
            Poll();
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}

/// <summary>Explorer-style file operations (conflict prompts, progress, undo) on a background thread.</summary>
public static unsafe class ShellFiles
{
    /// <summary>Copy files and folders into a folder, as dropping them on it in Explorer would.</summary>
    public static void CopyInto(nint owner, IReadOnlyList<string> paths, string folder) =>
        Run(owner, 2 /* FO_COPY */, paths, folder);

    public static void Run(nint owner, uint func, IReadOnlyList<string> paths, string? dest)
    {
        if (paths.Count == 0) return;
        var from = string.Join('\0', paths) + "\0\0"; // double-null-terminated lists
        var to = dest is null ? null : dest + "\0\0";
        var t = new Thread(() =>
        {
            Win32.CoInitializeEx(0, 2);
            fixed (char* f = from)
            fixed (char* d = to)
            {
                var op = new SHFILEOPSTRUCTW { hwnd = owner, wFunc = func, pFrom = f, pTo = to is null ? null : d, fFlags = 0x40 /* FOF_ALLOWUNDO */ };
                Win32.SHFileOperation(&op);
            }
            if (func == 3) RecycleBinService.Poll();
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}

/// <summary>
/// Top-level windows grouped by match key (see <see cref="RunningKeys"/>: exe path, launcher, AppUserModelID),
/// for running indicators, click-to-activate and previews.
/// </summary>
public static unsafe partial class RunningApps
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
        foreach (var key in RunningKeys.ForProcess(path)) Add(key, hwnd);
        if (AppUserModelId(hwnd) is { } id) Add(RunningKeys.Aumid(id), hwnd);
        return 1;
    }

    private static void Add(string key, nint hwnd)
    {
        if (!_scratch.TryGetValue(key, out var list)) _scratch[key] = list = [];
        list.Add(hwnd);
    }

    /// <summary>All match keys of one window (for attention requests).</summary>
    public static List<string> KeysOfWindow(nint hwnd)
    {
        var keys = PathOf(hwnd) is { } p ? RunningKeys.ForProcess(p) : [];
        if (AppUserModelId(hwnd) is { } id) keys.Add(RunningKeys.Aumid(id));
        return keys;
    }

    // ---- AppUserModelID of a window (Store apps, and desktop apps that set one)

    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt, r1, r2, r3; public nint p, p2; }

    [System.Runtime.InteropServices.LibraryImport("shell32.dll")]
    private static partial int SHGetPropertyStoreForWindow(nint hwnd, Guid* riid, nint* store);
    [System.Runtime.InteropServices.LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(PROPVARIANT* pv);

    public static string? AppUserModelId(nint hwnd)
    {
        nint store;
        var iid = IID_IPropertyStore;
        if (SHGetPropertyStoreForWindow(hwnd, &iid, &store) != 0 || store == 0) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 }; // PKEY_AppUserModel_ID
            var pv = default(PROPVARIANT);
            // IPropertyStore: QueryInterface, AddRef, Release, GetCount, GetAt, GetValue (5)
            var hr = ((delegate* unmanaged<nint, PROPERTYKEY*, PROPVARIANT*, int>)(*(nint**)store)[5])(store, &key, &pv);
            try
            {
                return hr == 0 && pv.vt == 31 /* VT_LPWSTR */ && pv.p != 0 ? new string((char*)pv.p) is { Length: > 0 } s ? s : null : null;
            }
            finally { PropVariantClear(&pv); }
        }
        finally { ((delegate* unmanaged<nint, uint>)(*(nint**)store)[2])(store); }
    }

    public static string? PathOfProcess(uint pid) => ProcessPath(pid);

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

    /// <summary>Processes whose windows belong to other apps or to Windows itself, never shown as running apps.</summary>
    public static bool IsShellHost(string exe) => Path.GetFileName(exe).ToLowerInvariant() is
        "applicationframehost.exe" or "textinputhost.exe" or "shellexperiencehost.exe" or "startmenuexperiencehost.exe"
        or "searchhost.exe" or "lockapp.exe" or "systemsettingsbroker.exe";

    /// <summary>A program's friendly name (its file description), else the file name.</summary>
    public static string DisplayName(string exe)
    {
        try
        {
            var d = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(d)) return d;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return Path.GetFileNameWithoutExtension(exe);
    }

    /// <summary>Image path of the process that owns a window.</summary>
    public static string? PathOf(nint hwnd)
    {
        uint pid;
        Win32.GetWindowThreadProcessId(hwnd, &pid);
        return pid == 0 ? null : ProcessPath(pid);
    }

    public static void Activate(nint hwnd)
    {
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        Win32.SetForegroundWindow(hwnd);
    }

    /// <summary>The keys an item matches running windows by (see <see cref="RunningKeys"/>).</summary>
    public static List<string> KeysFor(DockItem item, PathTokens tokens)
    {
        if (item.Kind is ItemKind.Separator or ItemKind.Stack || SpecialItems.Is(item)) return [];
        var (target, args) = item.Kind == ItemKind.Shortcut ? (item.Fallback?.Target, item.Fallback?.Arguments) : (item.Target, item.Arguments);
        return RunningKeys.ForItem(tokens.Expand(target), args);
    }
}

public static class ItemFactory
{
    /// <summary>A dropped file, or a virtual shell item ("::{CLSID}", e.g. the Recycle Bin) kept by its parsing name.</summary>
    public static DockItem? FromDropped(string path, string name, PathTokens tokens)
    {
        if (!path.StartsWith("::", StringComparison.Ordinal)) return FromPath(path, tokens);
        return new DockItem { Kind = ItemKind.App, Name = name, Target = path };
    }

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
        if (ext == ".lnk" && Shell.ReadShortcut(path) is { } app && app.Target.StartsWith(RunningKeys.AppsFolder, StringComparison.OrdinalIgnoreCase))
        {
            // A shortcut to a Store/MSIX app: pin the app itself, so deleting the shortcut doesn't break the icon.
            item.Kind = ItemKind.App;
            item.Target = app.Target;
            return item;
        }
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
