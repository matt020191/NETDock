using System.Text.Json.Serialization;

namespace NETDock.Core;

public enum DockEdge { Bottom, Top, Left, Right }

public enum ItemKind
{
    /// <summary>An executable, document or shell parsing name (shell:AppsFolder\AUMID).</summary>
    App,
    /// <summary>A .lnk kept as a .lnk, so the shortcut's own Start In and arguments are what launch it.</summary>
    Shortcut,
    /// <summary>A file-system folder, shown as a stack (grid flyout) of its contents.</summary>
    Stack,
    Separator,
}

/// <summary>Platform: icons stand on a shelf (macOS). Panel: a background wraps the icons (RocketDock).</summary>
public enum DockStyle { Panel, Platform }

public enum StackView { Grid, List, Open }

/// <summary>How a launched program's first window opens (ShellExecute nShow).</summary>
public enum RunWindow { Normal, Minimized, Maximized }

/// <summary>What clicking an item does when its program is already running.</summary>
public enum OpenRunning { UseGlobal, SwitchTo, NewInstance }

/// <summary>Where the dock sits among other windows (RocketDock: Layering).</summary>
public enum DockLayer { AlwaysOnTop, Normal, AlwaysOnBottom }

public enum StackSort { Name, DateModified, DateCreated, Kind }

/// <summary>
/// Everything a stack needs to render its flyout. It lives on the item itself, so it is
/// saved with the dock and survives a restart (RocketDock's Stacks docklet lost this).
/// </summary>
public sealed class StackOptions
{
    public StackView View { get; set; } = StackView.Grid;
    public StackSort SortBy { get; set; } = StackSort.Name;
    public bool SortDescending { get; set; }
    public bool FoldersFirst { get; set; } = true;
    /// <summary>0 = pick from the item count.</summary>
    public int Columns { get; set; }
    public bool ShowHidden { get; set; }
    public int MaxItems { get; set; } = 200;
}

/// <summary>
/// Launch details captured when a .lnk was added. Only used when the .lnk itself is gone
/// (for example on another PC); never written back over the item's own fields.
/// </summary>
public sealed class ShortcutFallback
{
    public string Target { get; set; } = "";
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
}

public sealed class DockItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public ItemKind Kind { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Tokenized path (see <see cref="PathTokens"/>) or a shell parsing name.</summary>
    public string Target { get; set; } = "";
    public string? Arguments { get; set; }

    /// <summary>
    /// Start In, tokenized. Stored exactly as the user set it and never re-derived, so it
    /// cannot drift (Program Files to Program Files (x86)). Null means "the target's folder",
    /// decided at launch time rather than baked in.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Null = the target's own icon. Otherwise a path relative to the data folder
    /// ("icons/ab12.png", portable) or a tokenized "file.exe,index" reference.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>Recycle Bin only: icon while it has something in it (RocketDock's FileName2).</summary>
    public string? IconFull { get; set; }

    public bool RunAsAdmin { get; set; }
    public RunWindow Run { get; set; }
    public OpenRunning OpenRunning { get; set; }

    public StackOptions? Stack { get; set; }
    public ShortcutFallback? Fallback { get; set; }
}

public sealed class DockOptions
{
    public DockEdge Edge { get; set; } = DockEdge.Bottom;
    /// <summary>0 = primary monitor; otherwise the 1-based index in enumeration order.</summary>
    public int Monitor { get; set; }
    public int IconSize { get; set; } = 48;
    public bool Magnify { get; set; } = true;
    public int MagnifiedSize { get; set; } = 96;
    /// <summary>How many icons either side of the cursor grow.</summary>
    public double MagnifyRange { get; set; } = 2.5;
    /// <summary>Gap between icons at rest, in DIPs. RocketDock packs icons almost edge to edge.</summary>
    public int Spacing { get; set; } = 2;
    /// <summary>Gap either side of a separator, in DIPs (RocketDock sets this apart from icon spacing).</summary>
    public int SeparatorSpacing { get; set; } = 8;
    public DockStyle Style { get; set; } = DockStyle.Panel;
    /// <summary>Extra room above and below the icons inside the background panel, in DIPs.</summary>
    public int PanelPadding { get; set; } = 4;
    /// <summary>
    /// A RocketDock-format skin folder (background.ini + bg.png, separator.ini + sep.png).
    /// "skins/Name" = copied into the data folder (portable); null = the built-in panel.
    /// </summary>
    public string? Skin { get; set; }
    /// <summary>Gap between the dock and the screen edge, in DIPs.</summary>
    public int EdgeOffset { get; set; } = 4;
    /// <summary>0..1 position of the dock's centre along its edge.</summary>
    public double Centre { get; set; } = 0.5;
    /// <summary>Icon opacity at rest, 10-100 %.</summary>
    public int IconOpacity { get; set; } = 100;
    /// <summary>Panel, skin or shelf opacity in %, separate from the icons (RocketDock: background opacity).</summary>
    public int BackgroundOpacity { get; set; } = 100;
    /// <summary>Animation when an icon is clicked to launch (or has files dropped on it).</summary>
    public IconEffect ClickEffect { get; set; } = IconEffect.Bounce;
    /// <summary>Minimised windows appear as icons at the end of the dock; clicking one restores it.</summary>
    public bool MinimizeToDock { get; set; }
    /// <summary>Repeating animation while one of the app's windows flashes for attention.</summary>
    public IconEffect AttentionEffect { get; set; } = IconEffect.Bounce;
    /// <summary>Magnified icons are drawn fully opaque whatever <see cref="IconOpacity"/> says.</summary>
    public bool ZoomOpaque { get; set; } = true;
    /// <summary>Roughly how long icons take to grow or shrink, in ms.</summary>
    public int ZoomDurationMs { get; set; } = 150;
    /// <summary>Global hotkey that hides or shows the whole dock (RocketDock: Ctrl+Alt+R). Blank = none.</summary>
    public string? ToggleHotkey { get; set; } = "Ctrl+Alt+R";
    public DockLayer Layer { get; set; } = DockLayer.AlwaysOnTop;
    /// <summary>When not always on top: resting the mouse on the screen edge brings the dock over other windows.</summary>
    public bool PopUp { get; set; } = true;
    public int PopUpDelayMs { get; set; } = 250;
    public bool AutoHide { get; set; }
    public int AutoHideDelayMs { get; set; } = 600;
    /// <summary>How long the auto-hide slide takes, in ms (0 = instant).</summary>
    public int AutoHideDurationMs { get; set; } = 200;
    public bool ShowLabels { get; set; } = true;
    public bool ShowRunningIndicators { get; set; } = true;
    public bool Reflections { get; set; } = true;
    /// <summary>Prevents drag-reordering and drag-off removal.</summary>
    public bool Locked { get; set; }
    /// <summary>Clicking a running app brings its window forward instead of starting another copy.</summary>
    public bool ActivateIfRunning { get; set; } = true;
    /// <summary>Glass shelf colour, #AARRGGBB.</summary>
    public string ShelfColor { get; set; } = "#70182030";
    public string ShelfBorderColor { get; set; } = "#90FFFFFF";
    public string LabelColor { get; set; } = "#FFFFFFFF";
    public string LabelFont { get; set; } = "Segoe UI";
    /// <summary>Label text size in px at 100 % scaling.</summary>
    public int LabelSize { get; set; } = 13;
    public bool LabelBold { get; set; } = true;
    /// <summary>Outline around the letters; alpha 0 = none (RocketDock: outline colour + opacity).</summary>
    public string LabelOutlineColor { get; set; } = "#00000000";
    public string LabelShadowColor { get; set; } = "#99000000";
    /// <summary>Rounded pill behind the label; alpha 0 = none.</summary>
    public string LabelBackgroundColor { get; set; } = "#C70D1219";
    public string IndicatorColor { get; set; } = "#FFBFE4FF";
}

public sealed class DockSettings
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;
    public DockOptions Dock { get; set; } = new();
    public List<DockItem> Items { get; set; } = [];
    public bool LaunchAtStartup { get; set; }
    /// <summary>UI language file name (without .json) from a languages folder; null = English.</summary>
    public string? Language { get; set; }
    /// <summary>Extra folders shown in Icon Settings' library (tokenized paths).</summary>
    public List<string> IconFolders { get; set; } = [];

    /// <summary>Clamp every number into a range the renderer can draw, so a hand-edited file can't break it.</summary>
    public DockSettings Sanitize()
    {
        var d = Dock ??= new DockOptions();
        d.IconSize = Math.Clamp(d.IconSize, 16, 256);
        d.MagnifiedSize = Math.Clamp(d.MagnifiedSize, d.IconSize, 256);
        d.MagnifyRange = Math.Clamp(double.IsFinite(d.MagnifyRange) ? d.MagnifyRange : 2.5, 1, 8);
        d.Spacing = Math.Clamp(d.Spacing, 0, 64);
        d.SeparatorSpacing = Math.Clamp(d.SeparatorSpacing, 0, 64);
        d.PanelPadding = Math.Clamp(d.PanelPadding, 0, 32);
        d.IconOpacity = Math.Clamp(d.IconOpacity, 10, 100);
        d.BackgroundOpacity = Math.Clamp(d.BackgroundOpacity, 0, 100);
        d.ZoomDurationMs = Math.Clamp(d.ZoomDurationMs, 30, 1000);
        IconFolders ??= [];
        IconFolders.RemoveAll(string.IsNullOrWhiteSpace);
        d.EdgeOffset = Math.Clamp(d.EdgeOffset, 0, 200);
        d.Centre = Math.Clamp(double.IsFinite(d.Centre) ? d.Centre : 0.5, 0, 1);
        d.AutoHideDelayMs = Math.Clamp(d.AutoHideDelayMs, 0, 10_000);
        d.PopUpDelayMs = Math.Clamp(d.PopUpDelayMs, 0, 5_000);
        d.AutoHideDurationMs = Math.Clamp(d.AutoHideDurationMs, 0, 3_000);
        d.LabelSize = Math.Clamp(d.LabelSize, 8, 32);
        if (string.IsNullOrWhiteSpace(d.LabelFont) || d.LabelFont.Length > 64) d.LabelFont = "Segoe UI";
        d.Monitor = Math.Max(MonitorSpan.All, d.Monitor);
        // A skin is only ever a copy inside the data folder; anything else (absolute, UNC,
        // "..") from a hand-edited or imported file falls back to the built-in panel.
        if (!SettingsStore.IsDataReference(d.Skin, "skins/")) d.Skin = null;
        Items ??= [];
        Items.RemoveAll(i => i is null);
        foreach (var i in Items)
        {
            if (string.IsNullOrEmpty(i.Id)) i.Id = Guid.NewGuid().ToString("n");
            if (i.Kind == ItemKind.Stack) i.Stack ??= new StackOptions();
            if (i.Stack is { } s)
            {
                s.Columns = Math.Clamp(s.Columns, 0, 20);
                s.MaxItems = Math.Clamp(s.MaxItems, 1, 2000);
            }
        }
        return this;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(DockSettings))]
[JsonSerializable(typeof(DockOptions))]
[JsonSerializable(typeof(DockItem))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class SettingsJson : JsonSerializerContext;
