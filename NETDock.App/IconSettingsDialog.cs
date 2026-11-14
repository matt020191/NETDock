using NETDock.App.Native;
using NETDock.Core;

namespace NETDock.App;

/// <summary>
/// Icon Settings: RocketDock's per-icon dialog, modernised. Left: icon library folders and
/// their images; bottom: preview and properties. Paths show and save exactly as typed
/// (tokens such as %ProgramFiles% included), so Start In never changes behind your back.
/// </summary>
public sealed unsafe class IconSettingsDialog : WindowBase
{
    public static IconSettingsDialog? Current { get; private set; }

    private const int IdTree = 300, IdRemoveFolder = 301, IdAddFolder = 302, IdList = 310, IdBrowseIcon = 311, IdPreview = 320,
        IdDefaultIcon = 330, IdOk = 1, IdCancel = 2,
        IdName = 400, IdTarget = 401, IdBrowseTarget = 402, IdDir = 403, IdBrowseDir = 404, IdArgs = 405,
        IdRun = 406, IdOpenRunning = 407, IdAdmin = 408, IdView = 409, IdSort = 410, IdDescending = 411, IdFoldersFirst = 412;

    private readonly App _app;
    private readonly DockItem _item;
    private Layout _ui = null!;
    private nint _font, _tree, _list, _imageList, _preview, _previewIcon, _whiteBrush, _panelBrush;
    private int _iconPx;

    private readonly List<(string Path, bool Removable)> _folders = [];
    private List<string> _files = [];
    private readonly Dictionary<int, string> _pendingImages = []; // list index → icon key still loading

    private bool _iconChanged;
    private string? _pendingIcon; // a file path, or null for the default icon

    private IconSettingsDialog(App app, DockItem item) { _app = app; _item = item; }

    public static void Show(App app, DockItem item)
    {
        if (Current is { } c)
        {
            if (c._item == item) { Win32.SetForegroundWindow(c.Hwnd); return; }
            Win32.DestroyWindow(c.Hwnd);
        }
        Current = new IconSettingsDialog(app, item);
        Current.Open();
    }

    private bool IsStack => _item.Kind == ItemKind.Stack;

    private void Open()
    {
        POINT p;
        Win32.GetCursorPos(&p);
        var mon = Monitors.ForPoint(p.X, p.Y);
        var scale = mon.Dpi / 96f;
        int w = (int)(676 * scale), h = (int)(684 * scale); // outer size: client is ~16 x 40 smaller
        var title = string.IsNullOrWhiteSpace(_item.Name) ? "Icon Settings" : $"Icon Settings - {_item.Name}";
        Create("NETDock.IconSettings", Win32.WS_EX_DLGMODALFRAME, Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_SYSMENU,
            mon.Work.Left + (mon.Work.Width - w) / 2, mon.Work.Top + (mon.Work.Height - h) / 2, w, h, title, sysColor: 5);
        _whiteBrush = Ui.GetSysColorBrush(5);
        _panelBrush = Ui.CreateSolidBrush(Ui.Rgb(243, 243, 243));
        _font = Ui.CreateFont(-(int)(12 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _ui = new Layout(Hwnd, scale, _font);
        _iconPx = _ui.S(48);
        _app.Icons.AddListener(Hwnd);

        // Library
        _ui.Group("Folders", 12, 8, 240, 336);
        _tree = _ui.Add("SysTreeView32", null, Ui.TVS_HASBUTTONS | Ui.TVS_LINESATROOT | Ui.TVS_SHOWSELALWAYS | Ui.TVS_FULLROWSELECT | Win32.WS_TABSTOP | Win32.WS_BORDER,
            22, 30, 220, 268, IdTree);
        Ui.SetWindowTheme(_tree, "Explorer", null);
        _ui.Button("−", 22, 304, 34, 28, IdRemoveFolder);
        _ui.Button("+", 60, 304, 34, 28, IdAddFolder);
        _ui.Label("Add your own icon folders", 102, 310, 140);

        _ui.Group("Icons", 262, 8, 386, 336);
        _list = _ui.Add("SysListView32", null, Ui.LVS_ICON | Ui.LVS_SINGLESEL | Ui.LVS_SHOWSELALWAYS | Ui.LVS_AUTOARRANGE | Ui.LVS_SHAREIMAGELISTS | Win32.WS_TABSTOP | Win32.WS_BORDER,
            272, 30, 366, 268, IdList);
        Ui.SetWindowTheme(_list, "Explorer", null);
        Win32.SendMessage(_list, Ui.LVM_SETEXTENDEDLISTVIEWSTYLE, 0x10000, 0x10000); // double-buffered
        _imageList = Ui.ImageList_Create(_iconPx, _iconPx, 0x20 /* ILC_COLOR32 */, 64, 64);
        Win32.SendMessage(_list, Ui.LVM_SETIMAGELIST, 0, _imageList);
        Win32.SendMessage(_list, Ui.LVM_SETICONSPACING, 0, Win32.MakeLParam(_ui.S(86), _ui.S(84)));
        _ui.Button("Browse for an icon...", 272, 304, 170, 28, IdBrowseIcon);
        _ui.Label("PNG, ICO, or any EXE/DLL", 450, 310, 190);

        // Preview
        _ui.Group("Preview", 12, 352, 240, 228);
        _preview = _ui.Add("STATIC", null, Ui.SS_OWNERDRAW, 22, 374, 220, 196, IdPreview);

        // Properties
        _ui.Group("Properties", 262, 352, 386, 228);
        BuildProperties();

        _ui.Button("Default icon", 12, 592, 120, 30, IdDefaultIcon);
        _ui.Button("OK", 440, 592, 100, 30, IdOk, isDefault: true);
        _ui.Button("Cancel", 548, 592, 100, 30, IdCancel);

        BuildTree();
        UpdatePreview();
        Win32.ShowWindow(Hwnd, Win32.SW_SHOW);
        Win32.SetForegroundWindow(Hwnd);
        Win32.SetFocus(Win32.GetDlgItem(Hwnd, IdName));
    }

    private void BuildProperties()
    {
        const float lx = 270, lw = 84, ex = 360, ew = 248, bx = 612;
        float y = 374;
        void Row(string label) => _ui.Label(label, lx, y + 3, lw, 20, Ui.SS_RIGHT);

        Row("Name");
        _ui.Edit(_item.Name, ex, y, 278, IdName);
        y += 31;
        if (_item.Kind == ItemKind.Separator) return;

        Row(IsStack ? "Folder" : _item.Kind == ItemKind.Shortcut ? "Shortcut" : "Target");
        _ui.Edit(_item.Target, ex, y, ew, IdTarget);
        _ui.Button("...", bx, y, 26, 24, IdBrowseTarget);
        y += 31;

        if (IsStack)
        {
            var st = _item.Stack ??= new StackOptions();
            Row("View");
            _ui.Combo(ex, y, 278, IdView, ["Grid", "List", "Open the folder"], (int)st.View);
            y += 31;
            Row("Sort by");
            _ui.Combo(ex, y, 278, IdSort, ["Name", "Date modified", "Date created", "Type"], (int)st.SortBy);
            y += 33;
            _ui.Check("Descending", ex, y, 130, IdDescending, st.SortDescending);
            _ui.Check("Folders first", ex + 140, y, 140, IdFoldersFirst, st.FoldersFirst);
            return;
        }

        Row("Start in");
        var dir = _ui.Edit(_item.WorkingDirectory ?? "", ex, y, ew, IdDir);
        var cue = _item.Kind == ItemKind.Shortcut ? "(from the shortcut)" : "(the program's folder)";
        fixed (char* c = cue) Win32.SendMessage(dir, Ui.EM_SETCUEBANNER, 1, (nint)c);
        _ui.Button("...", bx, y, 26, 24, IdBrowseDir);
        y += 31;
        Row("Arguments");
        _ui.Edit(_item.Arguments ?? "", ex, y, 278, IdArgs);
        y += 31;
        Row("Run");
        _ui.Combo(ex, y, 278, IdRun, ["Normal window", "Minimized", "Maximized"], (int)_item.Run);
        y += 31;
        Row("Open running");
        _ui.Combo(ex, y, 278, IdOpenRunning, ["Use global setting", "Switch to it", "Start another copy"], (int)_item.OpenRunning);
        y += 33;
        _ui.Check("Run as administrator", ex, y, 278, IdAdmin, _item.RunAsAdmin);
    }

    // ------------------------------------------------------------ library

    private void BuildTree()
    {
        Win32.SendMessage(_tree, Ui.TVM_DELETEITEM, 0, Ui.TVI_ROOT);
        _folders.Clear();
        nint first = 0;
        void AddRoot(string label, string path, bool removable)
        {
            if (!Directory.Exists(path)) return;
            var root = Insert(Ui.TVI_ROOT, label, path, removable);
            if (first == 0) first = root;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(path).Order().Take(200))
                    Insert(root, Path.GetFileName(sub), sub, false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Directory.CreateDirectory(_app.Store.Location.IconsFolder);
        AddRoot("NETDock icons", _app.Store.Location.IconsFolder, false);
        if (RocketDockConfig.Folder() is { } rd) AddRoot("RocketDock icons", Path.Combine(rd, "Icons"), false);
        foreach (var f in _app.Settings.IconFolders)
            if (_app.Tokens.Expand(f) is { } path) AddRoot(Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path, path, true);
        if (first != 0) Win32.SendMessage(_tree, Ui.TVM_SELECTITEM, 9 /* TVGN_CARET */, first);
    }

    private nint Insert(nint parent, string text, string path, bool removable)
    {
        _folders.Add((path, removable));
        fixed (char* t = text)
        {
            var ins = new TVINSERTSTRUCTW
            {
                hParent = parent, hInsertAfter = Ui.TVI_LAST,
                item = new TVITEMW { mask = 0x1 | 0x4, pszText = t, lParam = _folders.Count - 1 },
            };
            return Win32.SendMessage(_tree, Ui.TVM_INSERTITEMW, 0, (nint)(&ins));
        }
    }

    private int _selectedFolder = -1;

    private void ShowFolder(int index)
    {
        _selectedFolder = index;
        Win32.SendMessage(_list, Ui.LVM_DELETEALLITEMS, 0, 0);
        Ui.ImageList_RemoveAll(_imageList);
        _pendingImages.Clear();
        _files = [];
        if (index < 0 || index >= _folders.Count) return;
        try
        {
            _files = Directory.EnumerateFiles(_folders[index].Path)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".ico" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff")
                .Order(StringComparer.OrdinalIgnoreCase).Take(1000).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var size = _iconPx <= 48 ? 48 : 96;
        for (var i = 0; i < _files.Count; i++)
        {
            var file = _files[i];
            var key = $"lib|{file}|{size}";
            var img = _app.Icons.Get(key, () => Shell.ShellImage(file, size, preferThumbnail: true));
            var image = img is null ? -2 /* I_IMAGENONE */ : AddImage(img);
            if (img is null) _pendingImages[i] = key;
            var name = Path.GetFileNameWithoutExtension(file);
            fixed (char* t = name)
            {
                var it = new LVITEMW { mask = 0x1 | 0x2, iItem = i, pszText = t, iImage = image };
                Win32.SendMessage(_list, Ui.LVM_INSERTITEMW, 0, (nint)(&it));
            }
        }
    }

    private int AddImage(IconImage img)
    {
        var icon = Ui.ToHIcon(img);
        var index = Ui.ImageList_ReplaceIcon(_imageList, -1, icon);
        Win32.DestroyIcon(icon);
        return index;
    }

    private void OnIconsReady()
    {
        _app.Icons.Acknowledge();
        foreach (var (index, key) in _pendingImages.ToList())
        {
            if (!_app.Icons.IsLoaded(key)) continue;
            _pendingImages.Remove(index);
            if (_app.Icons.Get(key, () => null) is not { } img) continue;
            var it = new LVITEMW { mask = 0x2, iItem = index, iImage = AddImage(img) };
            Win32.SendMessage(_list, Ui.LVM_SETITEMW, 0, (nint)(&it));
        }
    }

    // ------------------------------------------------------------ preview

    private void UpdatePreview()
    {
        if (_previewIcon != 0) { Win32.DestroyIcon(_previewIcon); _previewIcon = 0; }
        var px = Math.Min(256, _ui.S(128));
        IconImage? img;
        if (_iconChanged)
        {
            var probe = new DockItem { Kind = _item.Kind, Target = _item.Target, Fallback = _item.Fallback, Icon = PendingIconReference(import: false) };
            img = IconService.ForItem(probe, _app.Store, _app.Tokens, px)();
        }
        else img = IconService.ForItem(_item, _app.Store, _app.Tokens, px)();
        if (img is not null) _previewIcon = Ui.ToHIcon(img);
        Ui.InvalidateRect(_preview, null, true);
    }

    private void DrawPreview(DRAWITEMSTRUCT* d)
    {
        var r = d->rcItem;
        Ui.FillRect(d->hDC, &r, _whiteBrush);
        var rgn = Ui.CreateRoundRectRgn(r.Left, r.Top, r.Right, r.Bottom, _ui.S(12), _ui.S(12));
        Ui.FillRgn(d->hDC, rgn, _panelBrush);
        Win32.DeleteObject(rgn);
        if (_previewIcon == 0) return;
        var px = Math.Min(256, _ui.S(128));
        Ui.DrawIconEx(d->hDC, r.Left + (r.Width - px) / 2, r.Top + (r.Height - px) / 2, _previewIcon, px, px, 0, 0, 3 /* DI_NORMAL */);
    }

    /// <summary>
    /// The icon reference to store. Images are copied into the data folder (so the dock stays
    /// portable); EXE/DLL icons are referenced in place by tokenized path.
    /// </summary>
    private string? PendingIconReference(bool import)
    {
        if (_pendingIcon is null) return null;
        var file = _pendingIcon;
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".exe" or ".dll") return _app.Tokens.Tokenize(file) + ",0";
        var icons = Path.GetFullPath(_app.Store.Location.IconsFolder) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(file).StartsWith(icons, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(Path.GetFullPath(file)) + Path.DirectorySeparatorChar == icons)
            return "icons/" + Path.GetFileName(file);
        return import ? _app.Store.ImportIcon(file) : _app.Tokens.Tokenize(file);
    }

    // ------------------------------------------------------------ OK / Cancel

    /// <summary>A path field's current text with tokens expanded, or null when empty.</summary>
    private string? Expanded(int id) => Field(id) is { Length: > 0 } s ? _app.Tokens.Expand(s) : null;

    private string Field(int id) => Win32.GetDlgItem(Hwnd, id) is var h and not 0 ? Layout.Text(h) : "";
    private int ComboValue(int id) => Win32.GetDlgItem(Hwnd, id) is var h and not 0 ? Layout.Get(h, Ui.CB_GETCURSEL) : 0;
    private bool Checked(int id) => Win32.GetDlgItem(Hwnd, id) is var h and not 0 && Layout.Checked(h);

    private void Apply()
    {
        var old = new DockItem { Icon = _item.Icon, Target = _item.Target };
        string? Opt(string s) => s.Length == 0 ? null : s;
        _item.Name = Field(IdName);
        if (_item.Kind != ItemKind.Separator && Field(IdTarget) is { Length: > 0 } target) _item.Target = _app.Tokens.Tokenize(target)!;
        if (IsStack)
        {
            var st = _item.Stack ??= new StackOptions();
            st.View = (StackView)Math.Max(0, ComboValue(IdView));
            st.SortBy = (StackSort)Math.Max(0, ComboValue(IdSort));
            st.SortDescending = Checked(IdDescending);
            st.FoldersFirst = Checked(IdFoldersFirst);
        }
        else if (_item.Kind != ItemKind.Separator)
        {
            _item.WorkingDirectory = _app.Tokens.Tokenize(Opt(Field(IdDir)));
            _item.Arguments = Opt(Field(IdArgs));
            _item.Run = (RunWindow)Math.Max(0, ComboValue(IdRun));
            _item.OpenRunning = (OpenRunning)Math.Max(0, ComboValue(IdOpenRunning));
            _item.RunAsAdmin = Checked(IdAdmin);
        }
        if (_iconChanged) _item.Icon = PendingIconReference(import: true);
        if ((old.Icon, old.Target) != (_item.Icon, _item.Target)) _app.Dock.ForgetIcon(old);
        _app.Save();
        _app.Dock.Relayout();
    }

    protected override nint WndProc(uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case Win32.WM_COMMAND:
            {
                var id = (int)(w & 0xFFFF);
                switch (id)
                {
                    case IdOk: Apply(); Win32.DestroyWindow(Hwnd); return 0;
                    case IdCancel: Win32.DestroyWindow(Hwnd); return 0;
                    case IdDefaultIcon: _iconChanged = true; _pendingIcon = null; UpdatePreview(); return 0;
                    case IdBrowseIcon:
                        if (Dialogs.PickFile(Hwnd, "Choose an icon", "Icons and images\0*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.exe;*.dll\0All files\0*.*\0",
                                initial: _pendingIcon ?? (_selectedFolder >= 0 && _selectedFolder < _folders.Count ? _folders[_selectedFolder].Path : null)) is { } f)
                        { _iconChanged = true; _pendingIcon = f; UpdatePreview(); }
                        return 0;
                    case IdAddFolder:
                        if (Dialogs.PickFolder(Hwnd, "Add an icon folder") is { } folder)
                        {
                            var t = _app.Tokens.Tokenize(folder)!;
                            if (!_app.Settings.IconFolders.Contains(t, StringComparer.OrdinalIgnoreCase)) { _app.Settings.IconFolders.Add(t); _app.Save(); }
                            BuildTree();
                        }
                        return 0;
                    case IdRemoveFolder:
                        if (_selectedFolder >= 0 && _selectedFolder < _folders.Count && _folders[_selectedFolder].Removable)
                        {
                            var path = _folders[_selectedFolder].Path;
                            _app.Settings.IconFolders.RemoveAll(x => string.Equals(_app.Tokens.Expand(x), path, StringComparison.OrdinalIgnoreCase));
                            _app.Save();
                            BuildTree();
                        }
                        return 0;
                    case IdBrowseTarget:
                        if (IsStack)
                        {
                            if (Dialogs.PickFolder(Hwnd, "Folder for this stack", Expanded(IdTarget)) is { } sf) Ui.SetWindowText(Win32.GetDlgItem(Hwnd, IdTarget), _app.Tokens.Tokenize(sf)!);
                        }
                        else if (Dialogs.PickFile(Hwnd, "Target", "Programs and shortcuts\0*.exe;*.lnk;*.bat;*.cmd;*.url\0All files\0*.*\0", dereferenceLinks: false, initial: Expanded(IdTarget)) is { } tf)
                            Ui.SetWindowText(Win32.GetDlgItem(Hwnd, IdTarget), _app.Tokens.Tokenize(tf)!);
                        return 0;
                    case IdBrowseDir:
                        // Empty Start in means "the program's folder", so start the browser there.
                        if (Dialogs.PickFolder(Hwnd, "Start in", Expanded(IdDir) ?? Expanded(IdTarget)) is { } d) Ui.SetWindowText(Win32.GetDlgItem(Hwnd, IdDir), _app.Tokens.Tokenize(d)!);
                        return 0;
                }
                break;
            }
            case Ui.WM_NOTIFY:
            {
                var hdr = (NMHDR*)l;
                if (hdr->idFrom == IdTree && hdr->code == Ui.TVN_SELCHANGEDW)
                    ShowFolder((int)((NMTREEVIEW*)l)->itemNew.lParam);
                else if (hdr->idFrom == IdList && hdr->code == Ui.LVN_ITEMCHANGED)
                {
                    var nm = (NMLISTVIEW*)l;
                    if ((nm->uNewState & 2) != 0 && (nm->uOldState & 2) == 0 && nm->iItem >= 0 && nm->iItem < _files.Count)
                    { _iconChanged = true; _pendingIcon = _files[nm->iItem]; UpdatePreview(); }
                }
                return 0;
            }
            case IconService.WM_ICONS_READY:
                OnIconsReady();
                return 0;
            case Ui.WM_DRAWITEM when ((DRAWITEMSTRUCT*)l)->CtlID == IdPreview:
                DrawPreview((DRAWITEMSTRUCT*)l);
                return 1;
            case Ui.WM_CTLCOLORSTATIC:
            case Ui.WM_CTLCOLORBTN:
                Ui.SetBkColor(w, Ui.GetSysColor(5));
                return _whiteBrush;
            case Win32.WM_CLOSE:
                Win32.DestroyWindow(Hwnd);
                return 0;
            case Win32.WM_DESTROY:
                _app.Icons.RemoveListener(Hwnd);
                if (Current == this) Current = null;
                if (_previewIcon != 0) Win32.DestroyIcon(_previewIcon);
                if (_imageList != 0) Ui.ImageList_Destroy(_imageList);
                if (_font != 0) Win32.DeleteObject(_font);
                if (_panelBrush != 0) Win32.DeleteObject(_panelBrush);
                return 0;
        }
        return Default(msg, w, l);
    }
}
