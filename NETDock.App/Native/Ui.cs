using System.Runtime.InteropServices;

namespace NETDock.App.Native;

[StructLayout(LayoutKind.Sequential)] public struct NMHDR { public nint hwndFrom; public nuint idFrom; public int code; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LVITEMW
{
    public uint mask; public int iItem, iSubItem; public uint state, stateMask; public char* pszText; public int cchTextMax, iImage;
    public nint lParam; public int iIndent, iGroupId; public uint cColumns; public nint puColumns, piColFmt; public int iGroup;
}

[StructLayout(LayoutKind.Sequential)]
public struct NMLISTVIEW { public NMHDR hdr; public int iItem, iSubItem; public uint uNewState, uOldState, uChanged; public POINT ptAction; public nint lParam; }

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TVITEMW
{
    public uint mask; public nint hItem; public uint state, stateMask; public char* pszText; public int cchTextMax, iImage, iSelectedImage, cChildren; public nint lParam;
}

[StructLayout(LayoutKind.Sequential)]
public struct TVINSERTSTRUCTW { public nint hParent, hInsertAfter; public TVITEMW item; }

[StructLayout(LayoutKind.Sequential)]
public struct NMTREEVIEW { public NMHDR hdr; public uint action; public TVITEMW itemOld, itemNew; public POINT ptDrag; }

[StructLayout(LayoutKind.Sequential)]
public struct DRAWITEMSTRUCT { public uint CtlType, CtlID, itemID, itemAction, itemState; public nint hwndItem, hDC; public RECT rcItem; public nint itemData; }

[StructLayout(LayoutKind.Sequential)]
public struct MEASUREITEMSTRUCT { public uint CtlType, CtlID, itemID, itemWidth, itemHeight; public nint itemData; }

[StructLayout(LayoutKind.Sequential)]
public struct INITCOMMONCONTROLSEX { public uint dwSize, dwICC; }

/// <summary>Plain Win32 controls with DPI scaling, for the settings windows.</summary>
public static unsafe partial class Ui
{
    public const uint WM_NOTIFY = 0x4E, WM_HSCROLL = 0x114, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C,
        WM_CTLCOLORSTATIC = 0x138, WM_CTLCOLORBTN = 0x135, WM_CTLCOLORDLG = 0x136, WM_GETMINMAXINFO = 0x24, WM_SHOWWINDOW = 0x18;
    public const uint BN_CLICKED = 0, CBN_SELCHANGE = 1, EN_CHANGE = 0x300, LBN_SELCHANGE = 1;
    public const uint BS_PUSHBUTTON = 0, BS_DEFPUSHBUTTON = 1, BS_AUTOCHECKBOX = 3, BS_GROUPBOX = 7;
    public const uint CBS_DROPDOWNLIST = 3, WS_VSCROLL = 0x00200000, ES_AUTOHSCROLL = 0x80, ES_READONLY = 0x800;
    public const uint LBS_NOTIFY = 1, LBS_OWNERDRAWFIXED = 0x10, LBS_HASSTRINGS = 0x40, LBS_NOINTEGRALHEIGHT = 0x100;
    public const uint SS_OWNERDRAW = 0xD, SS_NOTIFY = 0x100, SS_RIGHT = 2, SS_CENTER = 1, SS_ENDELLIPSIS = 0x4000;
    public const uint TBS_HORZ = 0, TBS_NOTICKS = 0x10, TBS_BOTH = 0x8;
    public const uint TBM_GETPOS = 0x400, TBM_SETPOS = 0x405, TBM_SETRANGEMIN = 0x407, TBM_SETRANGEMAX = 0x408;
    public const uint CB_ADDSTRING = 0x143, CB_GETCURSEL = 0x147, CB_SETCURSEL = 0x14E, CB_RESETCONTENT = 0x14B;
    public const uint LB_ADDSTRING = 0x180, LB_GETCURSEL = 0x188, LB_SETCURSEL = 0x186, LB_GETTEXT = 0x189;
    public const uint BM_GETCHECK = 0xF0, BM_SETCHECK = 0xF1;
    public const uint LVS_ICON = 0, LVS_SINGLESEL = 4, LVS_SHOWSELALWAYS = 8, LVS_AUTOARRANGE = 0x100, LVS_SHAREIMAGELISTS = 0x40;
    public const uint LVM_SETIMAGELIST = 0x1003, LVM_DELETEALLITEMS = 0x1009, LVM_INSERTITEMW = 0x104D, LVM_SETITEMW = 0x104C,
        LVM_GETNEXTITEM = 0x100C, LVM_SETICONSPACING = 0x1035, LVM_SETEXTENDEDLISTVIEWSTYLE = 0x1036, LVM_SETITEMSTATE = 0x102B, LVM_ENSUREVISIBLE = 0x1013;
    public const int LVN_ITEMCHANGED = -101, LVN_ITEMACTIVATE = -114, NM_DBLCLK = -3;
    public const uint TVS_HASBUTTONS = 1, TVS_HASLINES = 2, TVS_LINESATROOT = 4, TVS_SHOWSELALWAYS = 0x20, TVS_FULLROWSELECT = 0x1000;
    public const uint TVM_INSERTITEMW = 0x1132, TVM_DELETEITEM = 0x1101, TVM_SELECTITEM = 0x110B, TVM_GETNEXTITEM = 0x110A, TVM_EXPAND = 0x1102, TVM_SETEXTENDEDSTYLE = 0x112C;
    public const int TVN_SELCHANGEDW = -451;
    // Negative handles: must sign-extend on x64 (0xFFFFFFFFFFFF0000), not 0x00000000FFFF0000.
    public static readonly nint TVI_ROOT = -0x10000, TVI_LAST = -0xFFFE;
    public const uint EM_SETCUEBANNER = 0x1501;

    [LibraryImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool InitCommonControlsEx(INITCOMMONCONTROLSEX* i);
    [LibraryImport("comctl32.dll")] public static partial nint ImageList_Create(int cx, int cy, uint flags, int initial, int grow);
    [LibraryImport("comctl32.dll")] public static partial int ImageList_ReplaceIcon(nint il, int i, nint icon);
    [LibraryImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ImageList_Destroy(nint il);
    // ImageList_RemoveAll is a header macro, not an export: it is ImageList_Remove(il, -1).
    [LibraryImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool ImageList_Remove(nint il, int i);
    public static bool ImageList_RemoveAll(nint il) => ImageList_Remove(il, -1);
    [LibraryImport("user32.dll")] public static partial nint CreateIconIndirect(ICONINFO* ii);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool DrawIconEx(nint dc, int x, int y, nint icon, int cx, int cy, uint step, nint brush, uint flags);
    [LibraryImport("user32.dll")] public static partial nint GetSysColorBrush(int i);
    [LibraryImport("user32.dll")] public static partial uint GetSysColor(int i);
    [LibraryImport("user32.dll")] public static partial int FillRect(nint dc, RECT* r, nint brush);
    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)] public static partial int DrawText(nint dc, string s, int n, RECT* r, uint fmt);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool InvalidateRect(nint h, RECT* r, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowText(nint h, string s);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetClientRect(nint h, RECT* r);
    [LibraryImport("gdi32.dll")] public static partial nint CreateBitmap(int w, int h, uint planes, uint bpp, void* bits);
    [LibraryImport("gdi32.dll")] public static partial uint SetTextColor(nint dc, uint c);
    [LibraryImport("gdi32.dll")] public static partial uint SetBkColor(nint dc, uint c);
    [LibraryImport("gdi32.dll")] public static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] public static partial nint CreateSolidBrush(uint c);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [LibraryImport("gdi32.dll")] public static partial nint CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool FillRgn(nint dc, nint rgn, nint brush);
    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)] public static partial int SetWindowTheme(nint h, string app, string? idList);
    [LibraryImport("dwmapi.dll")] public static partial int DwmSetWindowAttribute(nint h, uint attr, void* value, uint size);

    public static void Init()
    {
        var i = new INITCOMMONCONTROLSEX { dwSize = (uint)sizeof(INITCOMMONCONTROLSEX), dwICC = 0xFF | 0x4000 }; // ICC_WIN95_CLASSES (listview, treeview, trackbar) + standard
        InitCommonControlsEx(&i);
    }

    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | g << 8 | b << 16);

    /// <summary>Accent colour from DWM (ARGB) as a COLORREF; falls back to Windows blue.</summary>
    public static uint AccentColor()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int abgr) return (uint)abgr & 0xFFFFFF; // stored as ABGR = COLORREF + alpha
        }
        catch { }
        return Rgb(0, 103, 192);
    }

    /// <summary>A standard HICON with proper alpha from premultiplied pixels (image lists and DrawIconEx honour it).</summary>
    public static nint ToHIcon(IconImage img)
    {
        int w = img.Width, h = img.Height;
        var bi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        void* bits;
        var dc = Win32.GetDC(0);
        var color = Win32.CreateDIBSection(dc, &bi, 0, &bits, 0, 0);
        Win32.ReleaseDC(0, dc);
        var dst = new Span<byte>(bits, w * h * 4);
        var src = img.Bgra;
        for (var i = 0; i < dst.Length; i += 4)
        {
            // Icons want straight alpha: undo premultiplication.
            var a = src[i + 3];
            dst[i + 3] = a;
            if (a == 0) { dst[i] = dst[i + 1] = dst[i + 2] = 0; continue; }
            dst[i] = (byte)Math.Min(255, src[i] * 255 / a);
            dst[i + 1] = (byte)Math.Min(255, src[i + 1] * 255 / a);
            dst[i + 2] = (byte)Math.Min(255, src[i + 2] * 255 / a);
        }
        var maskBits = new byte[((w + 15) / 16 * 2) * h]; // all zero: alpha decides
        nint mask;
        fixed (byte* m = maskBits) mask = CreateBitmap(w, h, 1, 1, m);
        var ii = new ICONINFO { fIcon = 1, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(&ii);
        Win32.DeleteObject(color);
        Win32.DeleteObject(mask);
        return icon;
    }
}

/// <summary>
/// Lays out child controls in DIPs scaled to the window's DPI, all sharing one font,
/// and keeps a per-control "page" tag so a settings page can be shown or hidden as a unit.
/// </summary>
public sealed unsafe class Layout(nint parent, float scale, nint font)
{
    public float Scale { get; } = scale;
    public nint Font { get; } = font;
    private readonly Dictionary<int, List<nint>> _pages = [];
    public int Page { get; set; } = -1;

    public int S(float v) => (int)MathF.Round(v * Scale);

    public nint Add(string cls, string? text, uint style, float x, float y, float w, float h, int id = 0, uint ex = 0)
    {
        var c = Win32.CreateWindowEx(ex, cls, text, Win32.WS_CHILD | Win32.WS_VISIBLE | style,
            S(x), S(y), S(w), S(h), parent, id, Win32.GetModuleHandle(0), 0);
        Win32.SendMessage(c, Win32.WM_SETFONT, Font, 1);
        if (Page >= 0)
        {
            if (!_pages.TryGetValue(Page, out var list)) _pages[Page] = list = [];
            list.Add(c);
        }
        return c;
    }

    // Single-line labels get an ellipsis; taller ones wrap.
    public nint Label(string text, float x, float y, float w, float h = 20, uint style = 0) =>
        Add("STATIC", text, style | (h <= 24 ? Ui.SS_ENDELLIPSIS : 0), x, y, w, h);
    public nint Button(string text, float x, float y, float w, float h, int id, bool isDefault = false) =>
        Add("BUTTON", text, (isDefault ? Ui.BS_DEFPUSHBUTTON : Ui.BS_PUSHBUTTON) | Win32.WS_TABSTOP, x, y, w, h, id);
    public nint Check(string text, float x, float y, float w, int id, bool on)
    {
        var c = Add("BUTTON", text, Ui.BS_AUTOCHECKBOX | Win32.WS_TABSTOP, x, y, w, 22, id);
        Win32.SendMessage(c, Ui.BM_SETCHECK, on ? 1 : 0, 0);
        return c;
    }
    public nint Group(string text, float x, float y, float w, float h) => Add("BUTTON", text, Ui.BS_GROUPBOX, x, y, w, h);
    public nint Edit(string text, float x, float y, float w, int id, uint extra = 0) =>
        Add("EDIT", text, Ui.ES_AUTOHSCROLL | Win32.WS_TABSTOP | extra, x, y, w, 24, id, Win32.WS_EX_CLIENTEDGE);
    public nint Combo(float x, float y, float w, int id, IEnumerable<string> items, int selected)
    {
        var c = Add("COMBOBOX", null, Ui.CBS_DROPDOWNLIST | Ui.WS_VSCROLL | Win32.WS_TABSTOP, x, y, w, 300, id);
        foreach (var s in items) fixed (char* p = s) Win32.SendMessage(c, Ui.CB_ADDSTRING, 0, (nint)p);
        Win32.SendMessage(c, Ui.CB_SETCURSEL, selected, 0);
        return c;
    }
    public nint Slider(float x, float y, float w, int id, int min, int max, int value)
    {
        var c = Add("msctls_trackbar32", null, Ui.TBS_HORZ | Ui.TBS_NOTICKS | Win32.WS_TABSTOP, x, y, w, 28, id);
        Win32.SendMessage(c, Ui.TBM_SETRANGEMIN, 0, min);
        Win32.SendMessage(c, Ui.TBM_SETRANGEMAX, 1, max);
        Win32.SendMessage(c, Ui.TBM_SETPOS, 1, value);
        return c;
    }

    public void ShowPage(int page)
    {
        foreach (var (p, list) in _pages)
            foreach (var c in list) Win32.ShowWindow(c, p == page ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
    }

    public static int Get(nint ctl, uint msg) => (int)Win32.SendMessage(ctl, msg, 0, 0);
    public static bool Checked(nint ctl) => Win32.SendMessage(ctl, Ui.BM_GETCHECK, 0, 0) == 1;

    public static string Text(nint ctl)
    {
        var len = (int)Win32.SendMessage(ctl, Win32.WM_GETTEXTLENGTH, 0, 0);
        var buf = new char[len + 1];
        fixed (char* b = buf) Win32.GetWindowText(ctl, b, len + 1);
        return new string(buf, 0, len).Trim();
    }
}
