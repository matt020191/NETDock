using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NETDock.App.Native;

namespace NETDock.App;

/// <summary>One dropped thing: a file system path, or a shell item's parsing name ("::{CLSID}" for the Recycle Bin).</summary>
public readonly record struct DroppedItem(string Path, string Name);

/// <summary>
/// The dock's OLE drop target. WM_DROPFILES only ever sees file system paths, so dragging the desktop's Recycle Bin
/// (or This PC, Control Panel …) onto the dock did nothing. Everything is read through the shell's item array instead,
/// which gives real paths for files and "::{CLSID}" parsing names for virtual items. A hand-built COM object (static
/// vtable, never freed) keeps this AOT-clean.
/// </summary>
public static unsafe partial class DropTarget
{
    private const int S_OK = 0, E_NOINTERFACE = unchecked((int)0x80004002);
    private const uint DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1, DROPEFFECT_LINK = 4;
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IDropTarget = new("00000122-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellItemArray = new("b63ea76d-1f85-456f-a19c-48159efa858b");

    /// <summary>Called on drop with the items and the drop point in screen coordinates.</summary>
    private static Action<List<DroppedItem>, POINT>? _onDrop;
    private static nint _object;

    public static void Register(nint hwnd, Action<List<DroppedItem>, POINT> onDrop)
    {
        _onDrop = onDrop;
        if (_object == 0)
        {
            var vtbl = (nint*)NativeMemory.Alloc((nuint)(7 * sizeof(nint)));
            vtbl[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
            vtbl[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
            vtbl[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
            vtbl[3] = (nint)(delegate* unmanaged<nint, nint, uint, long, uint*, int>)&DragEnter;
            vtbl[4] = (nint)(delegate* unmanaged<nint, uint, long, uint*, int>)&DragOver;
            vtbl[5] = (nint)(delegate* unmanaged<nint, int>)&DragLeave;
            vtbl[6] = (nint)(delegate* unmanaged<nint, nint, uint, long, uint*, int>)&Drop;
            _object = (nint)NativeMemory.Alloc((nuint)sizeof(nint));
            *(nint**)_object = vtbl;
        }
        RegisterDragDrop(hwnd, _object);
    }

    public static void Revoke(nint hwnd) => RevokeDragDrop(hwnd);

    /// <summary>Link where the source allows it (the dock points at things), else copy; never move.</summary>
    private static uint Effect(uint allowed) =>
        (allowed & DROPEFFECT_LINK) != 0 ? DROPEFFECT_LINK : (allowed & DROPEFFECT_COPY) != 0 ? DROPEFFECT_COPY : DROPEFFECT_NONE;

    [UnmanagedCallersOnly]
    private static int QueryInterface(nint self, Guid* iid, nint* ppv)
    {
        if (*iid == IID_IUnknown || *iid == IID_IDropTarget) { *ppv = self; return S_OK; }
        *ppv = 0;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly] private static uint AddRef(nint self) => 1;
    [UnmanagedCallersOnly] private static uint Release(nint self) => 1;
    [UnmanagedCallersOnly] private static int DragLeave(nint self) => S_OK;

    [UnmanagedCallersOnly]
    private static int DragEnter(nint self, nint data, uint keys, long pt, uint* effect) { *effect = Effect(*effect); return S_OK; }

    [UnmanagedCallersOnly]
    private static int DragOver(nint self, uint keys, long pt, uint* effect) { *effect = Effect(*effect); return S_OK; }

    [UnmanagedCallersOnly]
    private static int Drop(nint self, nint data, uint keys, long pt, uint* effect)
    {
        *effect = Effect(*effect);
        try
        {
            var items = Read(data);
            // POINTL arrives packed in one register: x in the low 32 bits, y in the high.
            var p = new POINT((int)(pt & 0xFFFFFFFF), (int)(pt >> 32));
            if (items.Count > 0) _onDrop?.Invoke(items, p);
        }
        catch (Exception ex) { App.Log(ex); }
        return S_OK;
    }

    /// <summary>The dropped items as parsing names (paths for files) with their display names.</summary>
    private static List<DroppedItem> Read(nint data)
    {
        var list = new List<DroppedItem>();
        nint array = 0;
        var iid = IID_IShellItemArray;
        if (data == 0 || SHCreateShellItemArrayFromDataObject(data, &iid, &array) < 0 || array == 0) return list;
        try
        {
            uint count;
            if (((delegate* unmanaged<nint, uint*, int>)Com.Vt(array)[7])(array, &count) < 0) return list; // GetCount
            for (uint i = 0; i < count; i++)
            {
                nint item = 0;
                if (((delegate* unmanaged<nint, uint, nint*, int>)Com.Vt(array)[8])(array, i, &item) < 0 || item == 0) continue; // GetItemAt
                try
                {
                    var path = Name(item, unchecked((int)0x80028000)); // SIGDN_DESKTOPABSOLUTEPARSING
                    if (!string.IsNullOrEmpty(path)) list.Add(new DroppedItem(path, Name(item, 0) ?? path)); // SIGDN_NORMALDISPLAY
                }
                finally { Com.Release(ref item); }
            }
        }
        finally { Com.Release(ref array); }
        return list;
    }

    private static string? Name(nint item, int sigdn)
    {
        char* s = null;
        if (((delegate* unmanaged<nint, int, char**, int>)Com.Vt(item)[5])(item, sigdn, &s) < 0 || s == null) return null; // GetDisplayName
        try { return new string(s); }
        finally { Win32.CoTaskMemFree((nint)s); }
    }

    [LibraryImport("ole32.dll")] private static partial int RegisterDragDrop(nint hwnd, nint target);
    [LibraryImport("ole32.dll")] private static partial int RevokeDragDrop(nint hwnd);
    [LibraryImport("shell32.dll")] private static partial int SHCreateShellItemArrayFromDataObject(nint data, Guid* iid, nint* ppv);
}
