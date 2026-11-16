using Microsoft.Win32;
using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

public sealed unsafe partial class App
{
    public const uint WM_TRAY = Win32.WM_APP + 2;
    public static nint AppIcon { get; private set; }
    /// <summary>The 16 px (at 100 %) version, for the tray and title bars.</summary>
    public static nint AppIconSmall { get; private set; }

    /// <summary>The app icon at a given pixel size, picked from the .ico's own sizes. Caller destroys it.</summary>
    public static nint LoadAppIcon(int px) => Win32.LoadImage(Win32.GetModuleHandle(0), 32512, 1 /* IMAGE_ICON */, px, px, 0);

    public DockSettings Settings { get; set; } = null!;
    public SettingsStore Store { get; }
    public PathTokens Tokens { get; }
    public IconService Icons { get; } = new();
    public DockWindow Dock { get; private set; } = null!;
    public StackFlyout Stack { get; private set; } = null!;
    public PreviewPopup Preview { get; private set; } = null!;
    public SearchOverlay Search { get; private set; } = null!;
    public string ExeFolder { get; }

    private App()
    {
        ExeFolder = AppContext.BaseDirectory.TrimEnd('\\');
        Store = new SettingsStore(DataLocation.Resolve(ExeFolder));
        Tokens = PathTokens.ForThisMachine(ExeFolder);
    }

    [STAThread]
    public static int Main()
    {
        Win32.CreateMutex(0, true, "NETDock.SingleInstance");
        if (Win32.GetLastError() == 183 /* ERROR_ALREADY_EXISTS */)
        {
            // Already running: hand "--settings" / "--icon-settings N" to that instance.
            if (CommandFromArgs() is { } cmd && FindDock() is var dock and not 0) Win32.PostMessage(dock, WM_COMMAND_LINE, cmd, 0);
            return 0;
        }
        Win32.OleInitialize(0);
        try
        {
            AppIcon = Win32.LoadImage(Win32.GetModuleHandle(0), 32512, 1, 0, 0, 0x40 /* LR_DEFAULTSIZE */);
            if (AppIcon == 0) AppIcon = Win32.LoadIcon(0, 32512);
            AppIconSmall = Win32.LoadImage(Win32.GetModuleHandle(0), 32512, 1, Win32.GetSystemMetrics(49 /* SM_CXSMICON */), Win32.GetSystemMetrics(50), 0);
            if (AppIconSmall == 0) AppIconSmall = AppIcon;
            D2D.Init();
            Ui.Init();
            var app = new App();
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log(ex);
            Win32.MessageBox(0, ex.ToString(), L.T("NETDock failed to start"), Win32.MB_OK | Win32.MB_ICONWARNING);
            return 1;
        }
    }

    private void Run()
    {
        Settings = Store.Load();
        LoadLanguage();
        if (Environment.GetCommandLineArgs().Contains("--import-rocketdock", StringComparer.OrdinalIgnoreCase)
            && ImportRocketDock(replace: true) is not null)
            Save(); // unattended: no prompts, no summary box
        var firstRun = !Store.Exists;
        string? summary = null;
        if (firstRun)
        {
            if (RocketDockConfig.IsInstalled
                && Win32.MessageBox(0, L.T("RocketDock settings were found on this PC.\n\nImport your RocketDock icons into NETDock?"),
                    "NETDock", Win32.MB_YESNO | Win32.MB_ICONQUESTION) == Win32.IDYES)
                summary = ImportRocketDock(replace: true);
            if (Settings.Items.Count == 0) Settings.Items.AddRange(Defaults());
            Save(); // before any summary box, so closing it (or killing us) can't lose the import
        }

        Dock = new DockWindow(this);
        Stack = new StackFlyout(this);
        Preview = new PreviewPopup(this);
        Search = new SearchOverlay(this);
        Dock.Show();
        AddTray();
        if (summary is not null) Win32.MessageBox(Dock.Hwnd, summary, "NETDock", Win32.MB_OK | Win32.MB_ICONINFORMATION);
        if (CommandFromArgs() is { } startCmd) RunCommandLine(startCmd);

        MSG msg;
        while (Win32.GetMessage(&msg, 0, 0, 0) > 0)
        {
            // Tab / Enter / Esc navigation in the settings windows.
            if (SettingsWindow.Current is { } sw && Win32.IsDialogMessage(sw.Hwnd, &msg)) continue;
            if (IconSettingsDialog.Current is { } isd && Win32.IsDialogMessage(isd.Hwnd, &msg)) continue;
            // Arrow keys, Enter and Esc in the search box drive the result list.
            if (msg.message == Win32.WM_KEYDOWN && Search is { IsOpen: true } so && msg.hwnd == so.Edit && so.HandleKey(msg.wParam)) continue;
            Win32.TranslateMessage(&msg);
            Win32.DispatchMessage(&msg);
        }
        RemoveTray();
    }

    private List<DockItem> Defaults() =>
    [
        new() { Kind = ItemKind.App, Name = "File Explorer", Target = @"%SystemRoot%\explorer.exe" },
        new() { Kind = ItemKind.Stack, Name = "Downloads", Target = @"%USERPROFILE%\Downloads", Stack = new StackOptions { SortBy = StackSort.DateModified, SortDescending = true, FoldersFirst = false } },
        new() { Kind = ItemKind.Stack, Name = "Documents", Target = @"%USERPROFILE%\Documents", Stack = new StackOptions() },
        new() { Kind = ItemKind.Separator },
        new() { Kind = ItemKind.App, Name = "Recycle Bin", Target = "::{645FF040-5081-101B-9F08-00AA002F954E}" },
    ];

    public void Save()
    {
        try { Store.Save(Settings); }
        catch (Exception ex) { Log(ex); }
    }

    public bool StackOpen => Stack?.IsOpen == true;

    public void ToggleStack(DockItem item, RECT anchor)
    {
        if (Stack.IsOpen && Stack.Item == item) { Stack.Close(); return; }
        Stack.Open(item, anchor);
    }

    public void OnStackClosed() { }

    /// <summary>Apply a whole new settings object (import), keeping the windows.</summary>
    /// <summary>Language folders: the data folder (portable copies travel with it), then next to the exe.</summary>
    public string?[] LanguageFolders => [Store.Location.LanguagesFolder, Path.Combine(ExeFolder, "languages")];

    public void LoadLanguage() => L.Load(L.Find(Settings.Language, LanguageFolders), Settings.Language);

    public void Replace(DockSettings s)
    {
        Settings = s.Sanitize();
        LoadLanguage();
        Save();
        Icons.Forget(_ => true);
        Dock.Relayout();
    }

    /// <summary>Imports into <see cref="Settings"/> (not saved) and returns a summary, or null on failure.</summary>
    public string? ImportRocketDock(bool replace)
    {
        try
        {
            var (result, folder) = RocketDockConfig.Read(Tokens);
            Directory.CreateDirectory(Store.Location.IconsFolder);
            foreach (var (src, name) in result.IconFilesToCopy)
                SettingsStore.CopyOver(src, Path.Combine(Store.Location.IconsFolder, name));
            if (replace) Settings = result.Settings;
            if (replace && result.SkinFolder is { } skin) Settings.Dock.Skin = Store.ImportSkin(skin);
            else Settings.Items.AddRange(result.Settings.Items);
            Settings.Sanitize();
            var msg = L.F("Imported {0} items from RocketDock ({1}).", result.Settings.Items.Count, folder);
            if (result.Notes.Count > 0) msg += "\n\n" + string.Join("\n", result.Notes);
            return msg;
        }
        catch (Exception ex)
        {
            Log(ex);
            Win32.MessageBox(0, L.T("RocketDock import failed:\n") + ex.Message, "NETDock", Win32.MB_OK | Win32.MB_ICONWARNING);
            return null;
        }
    }

    // ---------------------------------------------------------------- command line

    public const uint WM_COMMAND_LINE = Win32.WM_APP + 3;

    /// <summary>-1 = Dock Settings; N >= 0 = Icon Settings for item N; null = nothing asked.</summary>
    private static nint? CommandFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--settings", StringComparison.OrdinalIgnoreCase)) return -1;
            // --mode N (1-based) or --mode next: shortcuts and hotkey tools can switch modes.
            if (args[i].Equals("--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (args[i + 1].Equals("next", StringComparison.OrdinalIgnoreCase)) return -99;
                if (int.TryParse(args[i + 1], out var m) && m is >= 1 and <= 99) return -100 - m;
            }
            if (args[i].Equals("--icon-settings", StringComparison.OrdinalIgnoreCase)
                && i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n >= 0) return n;
        }
        return null;
    }

    private static nint FindDock() => FindWindow("NETDock.Dock", null);

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial nint FindWindow(string cls, string? title);

    public void RunCommandLine(nint cmd)
    {
        if (cmd == -1) SettingsWindow.Show(this);
        else if (cmd == -99 && Settings.Modes.Count > 1)
        {
            var i = Settings.Modes.IndexOf(ModeSwitcher.Active(Settings));
            SwitchMode(Settings.Modes[(i + 1) % Settings.Modes.Count].Name);
        }
        else if (cmd <= -101 && -cmd - 101 < Settings.Modes.Count) SwitchMode(Settings.Modes[(int)(-cmd - 101)].Name);
        else if (cmd >= 0 && cmd < Settings.Items.Count) IconSettingsDialog.Show(this, Settings.Items[(int)cmd]);
    }

    public void SwitchMode(string name)
    {
        if (ModeSwitcher.Switch(Settings, name)) AfterModeChange(restoreLayout: true);
    }

    /// <summary>Save, redraw the dock with the new icons, and (on a switch) move windows to the mode's layout.</summary>
    public void AfterModeChange(bool restoreLayout)
    {
        Save();
        Dock.Relayout();
        if (restoreLayout && ModeSwitcher.Find(Settings, Settings.ActiveMode) is { Windows.Count: > 0 } m)
            WindowLayouts.Restore(m.Windows, Dock.Hwnd);
    }

    // ---------------------------------------------------------------- tray

    private nint _trayHwnd;

    private void AddTray()
    {
        _trayHwnd = Dock.Hwnd;
        var d = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _trayHwnd, uID = 1, uFlags = 1 | 2 | 4, uCallbackMessage = WM_TRAY, hIcon = AppIconSmall };
        "NETDock".AsSpan().CopyTo(new Span<char>(d.szTip, 128));
        Win32.Shell_NotifyIcon(0, &d);
    }

    private void RemoveTray()
    {
        var d = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _trayHwnd, uID = 1 };
        Win32.Shell_NotifyIcon(2, &d);
    }

    public void OnTray(DockWindow dock, uint mouseMsg)
    {
        if (mouseMsg == Win32.WM_LBUTTONUP && dock.IsUserHidden) { dock.ToggleVisible(); return; }
        if (mouseMsg is Win32.WM_RBUTTONUP or Win32.WM_LBUTTONUP)
        {
            POINT p;
            Win32.GetCursorPos(&p);
            Menus.ShowDockMenu(this, dock, null, -1, p);
        }
    }

    // ---------------------------------------------------------------- startup + logging

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartsWithWindows
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("NETDock") is not null; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("NETDock", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("NETDock", throwOnMissingValue: false);
        }
    }

    public static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NETDock");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTime.Now:O} {ex}\n\n");
        }
        catch { }
    }
}

/// <summary>Reads RocketDock's own registry settings (it stores everything under HKCU).</summary>
public static class RocketDockConfig
{
    private const string Key = @"Software\RocketDock";

    public static bool IsInstalled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Key + @"\Icons"); return k?.GetValue("count") is not null; }
    }

    public static string? Folder()
    {
        foreach (var env in new[] { "ProgramFiles(x86)", "ProgramW6432", "ProgramFiles" })
        {
            var root = Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrEmpty(root)) continue;
            var f = Path.Combine(root, "RocketDock");
            if (File.Exists(Path.Combine(f, "RocketDock.exe"))) return f;
        }
        return null;
    }

    public static (RocketDockImport.Result Result, string Folder) Read(PathTokens tokens)
    {
        static Dictionary<string, string> Values(string sub)
        {
            using var k = Registry.CurrentUser.OpenSubKey(sub);
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (k is null) return d;
            foreach (var n in k.GetValueNames()) d[n] = k.GetValue(n)?.ToString() ?? "";
            return d;
        }
        var folder = Folder() ?? "";
        var ini = Path.Combine(folder, "Docklets", "Settings.ini");
        var import = new RocketDockImport(tokens, p => File.Exists(p) || Directory.Exists(p));
        var result = import.Import(Values(Key + @"\Icons"), Values(Key), File.Exists(ini) ? File.ReadAllText(ini) : null, folder);
        return (result, folder);
    }
}
