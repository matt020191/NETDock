using System.Runtime.InteropServices;
using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// Item properties, built from plain Win32 controls. Fields show exactly what is stored
/// (tokens like %ProgramFiles% included) and are saved exactly as typed, so Start In is only
/// ever what you put there.
/// </summary>
public sealed unsafe partial class ItemDialog : WindowBase
{
    public static nint Current { get; private set; }

    private const int IdName = 101, IdTarget = 102, IdArgs = 103, IdDir = 104, IdIcon = 105, IdAdmin = 106,
        IdBrowseTarget = 201, IdBrowseDir = 202, IdBrowseIcon = 203, IdOk = 1, IdCancel = 2;

    private readonly App _app;
    private readonly DockItem _item;
    private nint _font;
    private float _s;

    private ItemDialog(App app, DockItem item) { _app = app; _item = item; }

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);

    public static void Show(App app, DockItem item)
    {
        if (Current != 0) { Win32.SetForegroundWindow(Current); return; }
        new ItemDialog(app, item).Open();
    }

    private void Open()
    {
        POINT p;
        Win32.GetCursorPos(&p);
        var mon = Monitors.ForPoint(p.X, p.Y);
        _s = mon.Dpi / 96f;
        int S(float v) => (int)(v * _s);
        int w = S(560), h = S(_item.Kind == ItemKind.Stack ? 230 : 330);
        var x = mon.Work.Left + (mon.Work.Width - w) / 2;
        var y = mon.Work.Top + (mon.Work.Height - h) / 2;
        Create("NETDock.Props", Win32.WS_EX_DLGMODALFRAME | Win32.WS_EX_TOPMOST, Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_SYSMENU,
            x, y, w, h, $"{(string.IsNullOrEmpty(_item.Name) ? "Item" : _item.Name)} - Properties");
        _font = CreateFont(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        Current = Hwnd;

        var row = S(14);
        var isStack = _item.Kind == ItemKind.Stack;
        Field("Name", IdName, _item.Name, ref row, 0);
        Field(isStack ? "Folder" : _item.Kind == ItemKind.Shortcut ? "Shortcut (.lnk)" : "Target", IdTarget, _item.Target, ref row, IdBrowseTarget);
        if (!isStack)
        {
            Field("Arguments", IdArgs, _item.Arguments ?? "", ref row, 0);
            Field("Start in", IdDir, _item.WorkingDirectory ?? "", ref row, IdBrowseDir);
        }
        Field("Icon", IdIcon, _item.Icon ?? "", ref row, IdBrowseIcon);
        if (!isStack)
        {
            Control("BUTTON", "Run as administrator", 0x3 /* BS_AUTOCHECKBOX */ | Win32.WS_TABSTOP, S(110), row, S(300), S(22), IdAdmin);
            if (_item.RunAsAdmin) Win32.SendMessage(Win32.GetDlgItem(Hwnd, IdAdmin), 0xF1 /* BM_SETCHECK */, 1, 0);
            row += S(28);
            var note = _item.Kind == ItemKind.Shortcut
                ? "Blank Start in = use the shortcut's own. Paths may use %ProgramFiles%, %USERPROFILE%, %NETDOCK% etc."
                : "Blank Start in = the target's folder. Paths may use %ProgramFiles%, %USERPROFILE%, %NETDOCK% etc.";
            Control("STATIC", note, 0, S(14), row, S(520), S(34), 0);
            row += S(36);
        }
        Control("BUTTON", "OK", 0x1 /* BS_DEFPUSHBUTTON */ | Win32.WS_TABSTOP, S(560 - 14 - 180 - 10), row, S(90), S(28), IdOk);
        Control("BUTTON", "Cancel", Win32.WS_TABSTOP, S(560 - 14 - 90 - 10), row, S(90), S(28), IdCancel);

        Win32.ShowWindow(Hwnd, Win32.SW_SHOW);
        Win32.SetForegroundWindow(Hwnd);
        Win32.SetFocus(Win32.GetDlgItem(Hwnd, IdName));
    }

    private void Field(string label, int id, string value, ref int row, int browseId)
    {
        int S(float v) => (int)(v * _s);
        Control("STATIC", label, 0, S(14), row + S(4), S(92), S(20), 0);
        var editW = browseId != 0 ? S(340) : S(430);
        Control("EDIT", value, 0x80 /* ES_AUTOHSCROLL */ | Win32.WS_BORDER | Win32.WS_TABSTOP, S(110), row, editW, S(24), id);
        if (browseId != 0) Control("BUTTON", "Browse...", Win32.WS_TABSTOP, S(110) + editW + S(8), row, S(82), S(24), browseId);
        row += S(32);
    }

    private void Control(string cls, string text, uint style, int x, int y, int w, int h, int id)
    {
        var c = Win32.CreateWindowEx(0, cls, text, Win32.WS_CHILD | Win32.WS_VISIBLE | style, x, y, w, h, Hwnd, id, Win32.GetModuleHandle(0), 0);
        Win32.SendMessage(c, Win32.WM_SETFONT, _font, 1);
    }

    private string Text(int id)
    {
        var h = Win32.GetDlgItem(Hwnd, id);
        if (h == 0) return "";
        var len = (int)Win32.SendMessage(h, Win32.WM_GETTEXTLENGTH, 0, 0);
        var buf = new char[len + 1];
        fixed (char* b = buf) Win32.GetWindowText(h, b, len + 1);
        return new string(buf, 0, len).Trim();
    }

    private void SetText(int id, string text)
    {
        fixed (char* t = text) Win32.SendMessage(Win32.GetDlgItem(Hwnd, id), Win32.WM_SETTEXT, 0, (nint)t);
    }

    private void Apply()
    {
        var oldIcon = (_item.Icon, _item.Target);
        string? Opt(string s) => s.Length == 0 ? null : s;
        _item.Name = Text(IdName);
        var target = Text(IdTarget);
        if (target.Length > 0) _item.Target = _app.Tokens.Tokenize(target)!;
        if (_item.Kind != ItemKind.Stack)
        {
            _item.Arguments = Opt(Text(IdArgs));
            _item.WorkingDirectory = _app.Tokens.Tokenize(Opt(Text(IdDir)));
            _item.RunAsAdmin = Win32.SendMessage(Win32.GetDlgItem(Hwnd, IdAdmin), 0xF0 /* BM_GETCHECK */, 0, 0) == 1;
        }
        var icon = Opt(Text(IdIcon));
        _item.Icon = icon is null || icon.StartsWith("icons/", StringComparison.Ordinal) ? icon : _app.Tokens.Tokenize(icon);
        if (oldIcon != (_item.Icon, _item.Target))
        {
            var probe = new DockItem { Icon = oldIcon.Icon, Target = oldIcon.Target };
            _app.Dock.ForgetIcon(probe);
        }
        _app.Save();
        _app.Dock.Relayout();
    }

    protected override nint WndProc(uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case Win32.WM_COMMAND:
                switch ((int)(w & 0xFFFF))
                {
                    case IdOk: Apply(); Win32.DestroyWindow(Hwnd); return 0;
                    case IdCancel: Win32.DestroyWindow(Hwnd); return 0;
                    case IdBrowseTarget:
                        if (_item.Kind == ItemKind.Stack)
                        {
                            if (Dialogs.PickFolder(Hwnd, "Folder for this stack") is { } f) SetText(IdTarget, _app.Tokens.Tokenize(f)!);
                        }
                        else if (Dialogs.PickFile(Hwnd, "Target", "All files\0*.*\0", dereferenceLinks: false) is { } t) SetText(IdTarget, _app.Tokens.Tokenize(t)!);
                        return 0;
                    case IdBrowseDir:
                        if (Dialogs.PickFolder(Hwnd, "Start in") is { } d) SetText(IdDir, _app.Tokens.Tokenize(d)!);
                        return 0;
                    case IdBrowseIcon:
                        if (Dialogs.PickFile(Hwnd, "Icon", "Icons and images\0*.png;*.ico;*.jpg;*.bmp;*.exe;*.dll\0All files\0*.*\0") is { } i)
                        {
                            var ext = Path.GetExtension(i).ToLowerInvariant();
                            SetText(IdIcon, ext is ".exe" or ".dll" ? _app.Tokens.Tokenize(i) + ",0" : _app.Store.ImportIcon(i));
                        }
                        return 0;
                }
                break;
            case Win32.WM_CLOSE:
                Win32.DestroyWindow(Hwnd);
                return 0;
            case Win32.WM_DESTROY:
                Current = 0;
                if (_font != 0) { Win32.DeleteObject(_font); _font = 0; }
                return 0;
        }
        return Default(msg, w, l);
    }
}
