using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>A small modal "type a name" box (Enter = OK, Esc = Cancel), run in its own message loop.</summary>
public sealed unsafe class PromptDialog : WindowBase
{
    private const int IdText = 100, IdOk = 1, IdCancel = 2;
    private nint _edit, _font, _owner;

    /// <summary>True while a prompt is up: the dock ignores commands that would change what it's asking about.</summary>
    public static bool IsOpen { get; private set; }
    private bool _done;
    private string? _result;

    /// <param name="owner">Disabled while the prompt is open (modal), so it can't start another command meanwhile.</param>
    public static string? Ask(nint owner, string title, string prompt, string initial)
    {
        if (IsOpen) return null;
        IsOpen = true;
        if (owner != 0) Win32.EnableWindow(owner, false);
        try { return new PromptDialog { _owner = owner }.Run(title, prompt, initial); }
        finally
        {
            if (owner != 0) Win32.EnableWindow(owner, true);
            IsOpen = false;
        }
    }

    private string? Run(string title, string prompt, string initial)
    {
        POINT p;
        Win32.GetCursorPos(&p);
        var mon = Monitors.ForPoint(p.X, p.Y);
        var scale = mon.Dpi / 96f;
        int w = (int)(380 * scale), h = (int)(160 * scale);
        Create("NETDock.Prompt", Win32.WS_EX_DLGMODALFRAME, Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_SYSMENU,
            mon.Work.Left + (mon.Work.Width - w) / 2, mon.Work.Top + (mon.Work.Height - h) / 2, w, h, title, owner: _owner);
        _font = Ui.CreateFont(-(int)(12 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var ui = new Layout(Hwnd, scale, _font);
        ui.Label(prompt, 12, 12, 340);
        _edit = ui.Edit(initial, 12, 36, 340, IdText);
        ui.Button(L.T("OK"), 168, 76, 90, 28, IdOk, isDefault: true);
        ui.Button(L.T("Cancel"), 262, 76, 90, 28, IdCancel);
        Win32.ShowWindow(Hwnd, Win32.SW_SHOW);
        Win32.SetForegroundWindow(Hwnd);
        Win32.SetFocus(_edit);
        Win32.SendMessage(_edit, 0xB1 /* EM_SETSEL */, 0, -1);

        MSG msg;
        while (!_done && Win32.GetMessage(&msg, 0, 0, 0) > 0)
        {
            if (Win32.IsDialogMessage(Hwnd, &msg)) continue;
            Win32.TranslateMessage(&msg);
            Win32.DispatchMessage(&msg);
        }
        if (!_done) Win32.PostQuitMessage(0); // WM_QUIT arrived inside our loop: pass it on to the app's loop
        return _result;
    }

    protected override nint WndProc(uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case Win32.WM_COMMAND:
                switch ((int)(w & 0xFFFF))
                {
                    case IdOk:
                        var buf = stackalloc char[128];
                        var n = Win32.GetWindowText(_edit, buf, 128);
                        _result = new string(buf, 0, n).Trim();
                        if (_result.Length == 0) _result = null;
                        Win32.DestroyWindow(Hwnd);
                        return 0;
                    case IdCancel:
                        Win32.DestroyWindow(Hwnd);
                        return 0;
                }
                break;
            case Win32.WM_CLOSE:
                Win32.DestroyWindow(Hwnd);
                return 0;
            case Win32.WM_DESTROY:
                _done = true;
                if (_font != 0) Win32.DeleteObject(_font);
                return 0;
        }
        return Default(msg, w, l);
    }
}
