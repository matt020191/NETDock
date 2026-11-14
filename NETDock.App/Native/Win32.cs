using System.Runtime.InteropServices;

namespace NETDock.App.Native;

[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
[StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }
[StructLayout(LayoutKind.Sequential)] public struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct WNDCLASSEXW
{
    public uint cbSize, style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public nint hInstance, hIcon, hCursor, hbrBackground;
    public char* lpszMenuName, lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFOHEADER
{
    public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public nint bmBits; }

[StructLayout(LayoutKind.Sequential)]
public struct ICONINFO { public int fIcon, xHotspot, yHotspot; public nint hbmMask, hbmColor; }

[StructLayout(LayoutKind.Sequential)]
public struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

[StructLayout(LayoutKind.Sequential)]
public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public unsafe struct SHELLEXECUTEINFOW
{
    public uint cbSize, fMask; public nint hwnd;
    public char* lpVerb, lpFile, lpParameters, lpDirectory;
    public int nShow; public nint hInstApp, lpIDList; public char* lpClass; public nint hkeyClass;
    public uint dwHotKey; public nint hIconOrMonitor, hProcess;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NOTIFYICONDATAW
{
    public uint cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
    public fixed char szTip[128];
    public uint dwState, dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct OPENFILENAMEW
{
    public uint lStructSize; public nint hwndOwner, hInstance;
    public char* lpstrFilter, lpstrCustomFilter; public uint nMaxCustFilter, nFilterIndex;
    public char* lpstrFile; public uint nMaxFile; public char* lpstrFileTitle; public uint nMaxFileTitle;
    public char* lpstrInitialDir, lpstrTitle; public uint Flags; public ushort nFileOffset, nFileExtension;
    public char* lpstrDefExt; public nint lCustData, lpfnHook; public char* lpTemplateName;
    public nint pvReserved; public uint dwReserved, FlagsEx;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct BROWSEINFOW
{
    public nint hwndOwner, pidlRoot; public char* pszDisplayName, lpszTitle; public uint ulFlags;
    public nint lpfn, lParam; public int iImage;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct WIN32_FIND_DATAW
{
    public uint dwFileAttributes; public long ftCreationTime, ftLastAccessTime, ftLastWriteTime;
    public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
    public fixed char cFileName[260];
    public fixed char cAlternateFileName[14];
}

[StructLayout(LayoutKind.Sequential)]
public struct SHQUERYRBINFO { public uint cbSize; public long i64Size, i64NumItems; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SHFILEOPSTRUCTW
{
    public nint hwnd; public uint wFunc; public char* pFrom, pTo; public ushort fFlags;
    public int fAnyOperationsAborted; public nint hNameMappings; public char* lpszProgressTitle;
}

public static unsafe partial class Win32
{
    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW")] public static partial int SHQueryRecycleBin(char* root, SHQUERYRBINFO* info);
    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW")] public static partial int SHEmptyRecycleBin(nint hwnd, char* root, uint flags);
    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")] public static partial int SHFileOperation(SHFILEOPSTRUCTW* op);

    public const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000,
        WS_CHILD = 0x40000000, WS_TABSTOP = 0x00010000, WS_BORDER = 0x00800000;
    public const uint WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000,
        WS_EX_ACCEPTFILES = 0x10, WS_EX_DLGMODALFRAME = 0x1, WS_EX_CLIENTEDGE = 0x200;

    public const uint WM_DESTROY = 0x2, WM_SIZE = 0x5, WM_ACTIVATE = 0x6, WM_CLOSE = 0x10, WM_SETFONT = 0x30, WM_TIMER = 0x113,
        WM_COMMAND = 0x111, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205,
        WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A, WM_MOUSELEAVE = 0x2A3, WM_DROPFILES = 0x233, WM_DPICHANGED = 0x2E0,
        WM_DISPLAYCHANGE = 0x7E, WM_SETTINGCHANGE = 0x1A, WM_KEYDOWN = 0x100, WM_APP = 0x8000, WM_CAPTURECHANGED = 0x215,
        WM_MOUSEACTIVATE = 0x21, WM_CONTEXTMENU = 0x7B, WM_LBUTTONDBLCLK = 0x203, WM_GETTEXT = 0xD, WM_GETTEXTLENGTH = 0xE,
        WM_SETTEXT = 0xC, WM_NCDESTROY = 0x82;
    public const int MA_NOACTIVATE = 3;

    public const uint ULW_ALPHA = 2;
    public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5, SW_RESTORE = 9, SW_MINIMIZE = 6;
    public static readonly nint HWND_TOPMOST = -1;

    public const uint TME_LEAVE = 2;
    public const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_POPUP = 0x10, MF_CHECKED = 0x8, MF_GRAYED = 0x1;
    public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80;
    public const uint MB_OK = 0, MB_ICONINFORMATION = 0x40, MB_ICONWARNING = 0x30, MB_YESNO = 4, MB_ICONQUESTION = 0x20;
    public const int IDYES = 6;

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")] public static partial ushort RegisterClassEx(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateWindowEx(uint ex, string cls, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] public static partial nint DefWindowProc(nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] public static partial int GetMessage(MSG* m, nint h, uint a, uint b);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(MSG* m);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] public static partial nint DispatchMessage(MSG* m);
    [LibraryImport("user32.dll", EntryPoint = "IsDialogMessageW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsDialogMessage(nint h, MSG* m);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool PostMessage(nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] public static partial nint SendMessage(nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShowWindow(nint h, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(nint h, nint after, int x, int y, int w, int hh, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UpdateLayeredWindow(nint h, nint dst, POINT* pos, SIZE* size, nint src, POINT* srcPt, uint key, BLENDFUNCTION* blend, uint flags);
    [LibraryImport("user32.dll")] public static partial nint GetDC(nint h);
    [LibraryImport("user32.dll")] public static partial int ReleaseDC(nint h, nint dc);
    [LibraryImport("user32.dll")] public static partial nuint SetTimer(nint h, nuint id, uint ms, nint fn);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool KillTimer(nint h, nuint id);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(POINT* p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool TrackMouseEvent(TRACKMOUSEEVENT* t);
    [LibraryImport("user32.dll")] public static partial nint SetCapture(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ReleaseCapture();
    [LibraryImport("user32.dll")] public static partial nint MonitorFromPoint(POINT p, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetMonitorInfo(nint mon, MONITORINFO* mi);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EnumDisplayMonitors(nint dc, RECT* clip, delegate* unmanaged<nint, nint, RECT*, nint, int> cb, nint data);
    [LibraryImport("shcore.dll")] public static partial int GetDpiForMonitor(nint mon, int type, uint* x, uint* y);
    [LibraryImport("user32.dll")] public static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AppendMenu(nint m, uint flags, nuint id, string? text);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenuEx(nint m, uint flags, int x, int y, nint h, nint p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyMenu(nint m);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint h);
    [LibraryImport("user32.dll")] public static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)] public static partial int MessageBox(nint h, string text, string caption, uint type);
    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")] public static partial nint LoadCursor(nint inst, nint id);
    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")] public static partial nint LoadIcon(nint inst, nint id);
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW")] public static partial nint LoadImage(nint inst, nint name, uint type, int cx, int cy, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterWindowMessage(string s);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RegisterShellHookWindow(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> cb, nint data);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowVisible(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsIconic(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindow(nint h);
    [LibraryImport("user32.dll")] public static partial nint GetWindow(nint h, uint cmd);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static partial nint GetWindowLongPtr(nint h, int idx);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(nint h, uint* pid);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")] public static partial int GetClassName(nint h, char* buf, int max);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")] public static partial int GetWindowText(nint h, char* buf, int max);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint h);
    [LibraryImport("user32.dll")] public static partial short GetKeyState(int vk);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyIcon(nint h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetIconInfo(nint h, ICONINFO* ii);
    [LibraryImport("user32.dll", EntryPoint = "PrivateExtractIconsW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PrivateExtractIcons(string file, int index, int cx, int cy, nint* icons, uint* ids, uint n, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool EnableWindow(nint h, [MarshalAs(UnmanagedType.Bool)] bool enable);
    [LibraryImport("user32.dll")] public static partial nint SetFocus(nint h);
    [LibraryImport("user32.dll")] public static partial nint GetDlgItem(nint h, int id);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetrics(int i);
    [LibraryImport("dwmapi.dll")] public static partial int DwmFlush();

    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] public static partial nint CreateDIBSection(nint dc, BITMAPINFOHEADER* bi, uint usage, void** bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] public static partial int GetDIBits(nint dc, nint bmp, uint start, uint lines, void* bits, BITMAPINFOHEADER* bi, uint usage);
    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")] public static partial int GetObject(nint obj, int size, void* buf);
    [LibraryImport("gdi32.dll")] public static partial nint GetStockObject(int i);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")] public static partial nint GetModuleHandle(nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseHandle(nint h);
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool QueryFullProcessImageName(nint p, uint flags, char* buf, uint* size);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", StringMarshalling = StringMarshalling.Utf16)] public static partial nint CreateMutex(nint attrs, [MarshalAs(UnmanagedType.Bool)] bool owner, string name);
    [LibraryImport("kernel32.dll")] public static partial uint GetLastError();

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ShellExecuteEx(SHELLEXECUTEINFOW* i);
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool Shell_NotifyIcon(uint msg, NOTIFYICONDATAW* d);
    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")] public static partial uint DragQueryFile(nint drop, uint i, char* buf, uint size);
    [LibraryImport("shell32.dll")] public static partial void DragFinish(nint drop);
    [LibraryImport("shell32.dll")] public static partial void DragAcceptFiles(nint h, [MarshalAs(UnmanagedType.Bool)] bool accept);
    [LibraryImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateItemFromParsingName(string path, nint bc, Guid* riid, nint* item);
    [LibraryImport("shell32.dll", EntryPoint = "SHBrowseForFolderW")] public static partial nint SHBrowseForFolder(BROWSEINFOW* bi);
    [LibraryImport("shell32.dll", EntryPoint = "SHGetPathFromIDListW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SHGetPathFromIDList(nint pidl, char* path);
    [LibraryImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetOpenFileName(OPENFILENAMEW* o);
    [LibraryImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetSaveFileName(OPENFILENAMEW* o);

    [LibraryImport("ole32.dll")] public static partial int CoInitializeEx(nint r, uint mode);
    [LibraryImport("ole32.dll")] public static partial int CoCreateInstance(Guid* clsid, nint outer, uint ctx, Guid* iid, nint* obj);
    [LibraryImport("ole32.dll")] public static partial void CoTaskMemFree(nint p);
    [LibraryImport("ole32.dll")] public static partial int OleInitialize(nint r);

    public static int LoWord(nint v) => (short)((long)v & 0xFFFF);
    public static int HiWord(nint v) => (short)(((long)v >> 16) & 0xFFFF);
    public static nint MakeLParam(int lo, int hi) => (nint)((hi << 16) | (lo & 0xFFFF));
}
