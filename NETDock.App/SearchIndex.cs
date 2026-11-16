using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// What the search overlay searches: every app Windows lists in the Apps folder (Start menu desktop apps and
/// Store apps alike, launched as <c>shell:AppsFolder\&lt;id&gt;</c>), plus recent files. Built on a background STA thread
/// and refreshed each time the overlay opens; the previous list is used until the new one is ready.
/// </summary>
public static unsafe class SearchIndex
{
    private static volatile List<SearchEntry> _apps = [];
    private static volatile List<SearchEntry> _recent = [];
    private static int _refreshing;

    public static IReadOnlyList<SearchEntry> Apps => _apps;
    public static IReadOnlyList<SearchEntry> Recent => _recent;

    /// <summary>Rebuild in the background; posts <paramref name="notifyMsg"/> to <paramref name="notify"/> when done.</summary>
    public static void Refresh(nint notify, uint notifyMsg)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        var t = new Thread(() =>
        {
            Win32.CoInitializeEx(0, 2);
            try
            {
                _apps = EnumerateApps();
                _recent = RecentFiles();
            }
            catch { /* keep the old lists */ }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
                Win32.PostMessage(notify, notifyMsg, 0, 0);
            }
        }) { IsBackground = true, Name = "NETDock search index" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid IID_IEnumShellItems = new("70629033-E363-4A28-A567-0DB78006E6D7");
    private static readonly Guid BHID_EnumItems = new("94F60519-2850-4924-AA5A-D15E84868039");

    private static List<SearchEntry> EnumerateApps()
    {
        var list = new List<SearchEntry>();
        nint folder;
        var iid = IID_IShellItem;
        if (Win32.SHCreateItemFromParsingName("shell:AppsFolder", 0, &iid, &folder) < 0 || folder == 0) return list;
        nint enumerator = 0;
        try
        {
            var bhid = BHID_EnumItems;
            var eiid = IID_IEnumShellItems;
            // IShellItem::BindToHandler (3)
            if (((delegate* unmanaged<nint, nint, Guid*, Guid*, nint*, int>)Com.Vt(folder)[3])(folder, 0, &bhid, &eiid, &enumerator) < 0 || enumerator == 0)
                return list;
            while (true)
            {
                nint item;
                uint fetched;
                // IEnumShellItems::Next (3)
                if (((delegate* unmanaged<nint, uint, nint*, uint*, int>)Com.Vt(enumerator)[3])(enumerator, 1, &item, &fetched) != 0 || fetched == 0) break;
                try
                {
                    var name = DisplayName(item, 0);                 // SIGDN_NORMALDISPLAY
                    var id = DisplayName(item, unchecked((int)0x80018001)); // SIGDN_PARENTRELATIVEPARSING: the AUMID or path
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                        list.Add(new SearchEntry(name, RunningKeys.AppsFolder + id, SearchKind.App));
                }
                finally { Com.Release(ref item); }
            }
        }
        finally
        {
            if (enumerator != 0) Com.Release(ref enumerator);
            Com.Release(ref folder);
        }
        return list;
    }

    private static string? DisplayName(nint item, int sigdn)
    {
        nint p;
        // IShellItem::GetDisplayName (5)
        if (((delegate* unmanaged<nint, int, nint*, int>)Com.Vt(item)[5])(item, sigdn, &p) < 0 || p == 0) return null;
        try { return new string((char*)p); }
        finally { Win32.CoTaskMemFree(p); }
    }

    /// <summary>The newest shortcuts in the user's Recent folder (files and folders they opened).</summary>
    private static List<SearchEntry> RecentFiles()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(dir)) return [];
        return new DirectoryInfo(dir).EnumerateFiles("*.lnk")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(60)
            .Select(f => new SearchEntry(Path.GetFileNameWithoutExtension(f.Name), f.FullName, SearchKind.Recent, f.LastWriteTime.ToString("g")))
            .ToList();
    }
}
