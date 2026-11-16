using System.Diagnostics;
using Microsoft.Win32;
using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// The installer built into NETDock.exe: a per-user install (no admin) into %LocalAppData%\Programs\NETDock
/// with a Start menu shortcut and an entry in Settings > Apps, and the matching uninstall.
/// </summary>
public static unsafe class Installer
{
    public const string Publisher = "M-TEC SYSTEMS, INC.";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NETDock";

    public static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NETDock");
    public static string InstalledExe => Path.Combine(InstallFolder, "NETDock.exe");
    private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NETDock.lnk");
    private static string UserSettingsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NETDock");

    public static string Version => typeof(Installer).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>This copy is the installed one.</summary>
    public static bool IsInstalled(string exeFolder) =>
        string.Equals(Path.GetFullPath(exeFolder).TrimEnd('\\'), InstallFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// First run of a downloaded copy (not installed, not portable, no settings yet): offer to install.
    /// True when an install was started and this process should exit.
    /// </summary>
    public static bool OfferOnFirstRun(App app)
    {
        if (IsInstalled(app.ExeFolder) || app.Store.Location.IsPortable || app.Store.Exists) return false;
        var r = Win32.MessageBox(0,
            L.T("Install NETDock for you?\n\nYes: installs to your user profile (no admin needed), adds it to the Start menu and to Settings > Apps, and keeps it updated.\nNo: just run it from here."),
            "NETDock", Win32.MB_YESNO | Win32.MB_ICONQUESTION);
        return r == Win32.IDYES && Install(app, owner: 0);
    }

    /// <summary>Copy this exe (and languages) into the install folder, register it, and start the installed copy.
    /// True when the installed copy was started (this process should exit).</summary>
    public static bool Install(App app, nint owner)
    {
        try
        {
            var source = Environment.ProcessPath!;
            Directory.CreateDirectory(InstallFolder);
            // An older installed copy may be running: ask it to close, then replace it.
            CloseOtherInstances();
            CopyFileReplacing(source, InstalledExe);
            var langs = Path.Combine(app.ExeFolder, "languages");
            if (Directory.Exists(langs))
            {
                var target = Path.Combine(InstallFolder, "languages");
                Directory.CreateDirectory(target);
                foreach (var f in Directory.GetFiles(langs, "*.json")) File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: true);
            }

            // A portable copy's icons and settings come along (only when the installed copy has none yet).
            if (app.Store.Location.IsPortable && !File.Exists(Path.Combine(UserSettingsFolder, "settings.json")) &&
                Win32.MessageBox(owner, L.T("Bring this copy's icons and settings (its Data folder) into the installed NETDock?"), "NETDock",
                    Win32.MB_YESNO | Win32.MB_ICONQUESTION) == Win32.IDYES)
                CopyFolder(app.Store.Location.Folder, UserSettingsFolder);

            CreateShortcut(ShortcutPath, InstalledExe, L.T("NETDock - a dock for your apps"));
            Register();
            if (App.StartsWithWindows) App.SetStartup(InstalledExe);

            Process.Start(new ProcessStartInfo(InstalledExe, "--after-install") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Win32.MessageBox(owner, L.F("Couldn't install NETDock:\n{0}", ex.Message), "NETDock", Win32.MB_OK | Win32.MB_ICONWARNING);
            return false;
        }
    }

    /// <summary>Write (or refresh) the Settings > Apps entry; called on install and after every update.</summary>
    public static void Register()
    {
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "NETDock");
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", Publisher);
        k.SetValue("DisplayIcon", InstalledExe + ",0");
        k.SetValue("InstallLocation", InstallFolder);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("URLInfoAbout", Updater.ProjectUrl);
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var size = File.Exists(InstalledExe) ? (int)(new FileInfo(InstalledExe).Length / 1024) : 0;
        k.SetValue("EstimatedSize", size, RegistryValueKind.DWord);
    }

    /// <summary>Remove the install (run as <c>NETDock.exe --uninstall</c> from Settings > Apps).</summary>
    public static void Uninstall()
    {
        if (Win32.MessageBox(0, L.T("Uninstall NETDock?"), "NETDock", Win32.MB_YESNO | Win32.MB_ICONQUESTION) != Win32.IDYES) return;
        CloseOtherInstances();
        var keepSettings = Directory.Exists(UserSettingsFolder) &&
            Win32.MessageBox(0, L.T("Keep your dock icons and settings, in case you install NETDock again?"), "NETDock",
                Win32.MB_YESNO | Win32.MB_ICONQUESTION) == Win32.IDYES;
        try
        {
            App.SetStartup(null);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            if (!keepSettings && Directory.Exists(UserSettingsFolder)) Directory.Delete(UserSettingsFolder, recursive: true);
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NETDock");
            if (!keepSettings && Directory.Exists(cache)) try { Directory.Delete(cache, recursive: true); } catch { /* log file in use: harmless */ }
        }
        catch (Exception ex) { App.Log(ex); }
        Win32.MessageBox(0, L.T("NETDock has been uninstalled."), "NETDock", Win32.MB_OK | Win32.MB_ICONINFORMATION);
        // A running exe can't delete itself: a hidden cmd removes the folder once this process has exited
        // (started last, so a slow click on the box above can't outlast its wait).
        if (Directory.Exists(InstallFolder))
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{InstallFolder}\"")
                { UseShellExecute = false, CreateNoWindow = true });
    }

    /// <summary>Ask any running dock to close and wait for it (up to 10 s).</summary>
    public static void CloseOtherInstances()
    {
        var me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("NETDock"))
        {
            if (p.Id == me) continue;
            try
            {
                var dock = App.FindDockWindow();
                if (dock != 0) Win32.PostMessage(dock, 0x10 /* WM_CLOSE */, 0, 0);
                if (!p.WaitForExit(10_000)) p.Kill();
            }
            catch { /* already gone */ }
        }
    }

    /// <summary>Replace a file that might be a running exe: rename the old one aside first (Windows allows that).</summary>
    public static void CopyFileReplacing(string source, string dest)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(dest))
        {
            var old = dest + ".old";
            try { File.Delete(old); } catch { /* still running: leave it */ }
            if (!File.Exists(old)) File.Move(dest, old); else File.Delete(dest);
        }
        File.Copy(source, dest);
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
    }

    // ---- Start menu shortcut (IShellLinkW + IPersistFile, slots from ShObjIdl_core.h / ObjIdl.h)

    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IID_IPersistFile = new("0000010B-0000-0000-C000-000000000046");

    private static void CreateShortcut(string lnk, string target, string description)
    {
        nint link = 0, file = 0;
        try
        {
            var c = CLSID_ShellLink; var i = IID_IShellLinkW;
            Com.Check(Win32.CoCreateInstance(&c, 0, 1, &i, &link), "ShellLink");
            fixed (char* t = target) ((delegate* unmanaged<nint, char*, int>)Com.Vt(link)[20])(link, t);                       // SetPath
            fixed (char* d = Path.GetDirectoryName(target)) ((delegate* unmanaged<nint, char*, int>)Com.Vt(link)[9])(link, d); // SetWorkingDirectory
            fixed (char* d = description) ((delegate* unmanaged<nint, char*, int>)Com.Vt(link)[7])(link, d);                  // SetDescription
            fixed (char* t = target) ((delegate* unmanaged<nint, char*, int, int>)Com.Vt(link)[17])(link, t, 0);              // SetIconLocation
            var pf = IID_IPersistFile;
            Com.Check(((delegate* unmanaged<nint, Guid*, nint*, int>)Com.Vt(link)[0])(link, &pf, &file), "IPersistFile");
            Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
            fixed (char* p = lnk) Com.Check(((delegate* unmanaged<nint, char*, int, int>)Com.Vt(file)[6])(file, p, 1), "Save shortcut"); // Save
        }
        finally
        {
            if (file != 0) Com.Release(ref file);
            if (link != 0) Com.Release(ref link);
        }
    }
}
