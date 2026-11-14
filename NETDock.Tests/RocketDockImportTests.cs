using NETDock.Core;

namespace NETDock.Tests;

public class RocketDockImportTests
{
    private static readonly PathTokens T = new([
        ("%ProgramFiles%", @"C:\Program Files"),
        ("%ProgramFiles(x86)%", @"C:\Program Files (x86)"),
        ("%USERPROFILE%", @"C:\Users\matt"),
    ]);

    // Shaped like a real RocketDock 1.3.5 registry export.
    private static Dictionary<string, string> Icons() => new()
    {
        ["count"] = "4",
        ["0-Title"] = "This PC", ["0-Command"] = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
        ["0-FileName"] = @"C:\Windows\System32\imageres.dll?-109",
        ["1-Title"] = "Google Chrome", ["1-Command"] = @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        ["1-WorkingDirectory"] = @"C:\Program Files\Google\Chrome\Application",
        ["1-FileName"] = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        ["2-IsSeparator"] = "1",
        ["3-Title"] = "Games", ["3-FileName"] = @"Icons\games.png", ["3-DockletFile"] = @"Docklets\StackDocklet\StackDocklet.dll",
    };

    // The stack's section is under the wrong index, as RocketDock leaves it after an icon move.
    private const string Docklets = "[Docklet0]\n[Docklet2]\nFolder=C:\\Games\nIcon=Icons\\games.png\nMode=2\nSort=0\n[Docklet3]\n";

    private static bool Exists(string p) =>
        p is @"C:\Program Files\Google\Chrome\Application\chrome.exe" or @"C:\RocketDock\Icons\games.png";

    [Fact]
    public void Repairs_x86_redirected_command_and_drops_default_start_in()
    {
        var r = new RocketDockImport(T, Exists).Import(Icons(), new Dictionary<string, string>(), Docklets, @"C:\RocketDock");
        var chrome = r.Settings.Items[1];
        Assert.Equal(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe", chrome.Target);
        Assert.Null(chrome.WorkingDirectory);
        Assert.Null(chrome.Icon);
        Assert.Contains(r.Notes, n => n.Contains("fixed"));
    }

    [Fact]
    public void Finds_orphaned_stack_section_and_copies_its_icon()
    {
        var r = new RocketDockImport(T, Exists).Import(Icons(), new Dictionary<string, string>(), Docklets, @"C:\RocketDock");
        var games = r.Settings.Items[3];
        Assert.Equal(ItemKind.Stack, games.Kind);
        Assert.Equal(@"C:\Games", games.Target);
        Assert.StartsWith("icons/rd-", games.Icon);
        Assert.Single(r.IconFilesToCopy);
        Assert.Equal(ItemKind.Separator, r.Settings.Items[2].Kind);
        Assert.Equal(@"C:\Windows\System32\imageres.dll,-109", r.Settings.Items[0].Icon);
    }

    [Fact]
    public void Maps_dock_options()
    {
        var root = new Dictionary<string, string> { ["IconMin"] = "48", ["IconMax"] = "112", ["ZoomWidth"] = "4", ["Side"] = "1", ["Monitor"] = "-1" };
        var d = new RocketDockImport(T, Exists).Import(new Dictionary<string, string> { ["count"] = "0" }, root, null, "").Settings.Dock;
        Assert.Equal(48, d.IconSize);
        Assert.Equal(112, d.MagnifiedSize);
        Assert.Equal(2, d.MagnifyRange);
        Assert.Equal(DockEdge.Bottom, d.Edge);
        Assert.Equal(0, d.Monitor);
    }
}

public class SkinTests
{
    [Fact]
    public void Parses_rocketdock_background_and_separator_ini()
    {
        var files = new Dictionary<string, string>
        {
            [Path.Combine("S", "background.ini")] = "[Background]\nImage        = bg.png\nLeftMargin   = 8\nTopMargin    = 8\nRightMargin  = 8\nBottomMargin = 8\nOutside-LeftMargin   = 20\nOutside-TopMargin    = 13\nOutside-RightMargin  = 23\nOutside-BottomMargin = 10\n",
            [Path.Combine("S", "separator.ini")] = "[Separator]\nImage =\t\tsep.png\nTopMargin =\t\t0\nBottomMargin =\t0\n",
        };
        var s = Skin.Load("S", p => files.GetValueOrDefault(p))!;
        Assert.Equal(Path.Combine("S", "bg.png"), s.Background);
        Assert.Equal((8, 8, 8, 8), (s.Left, s.Top, s.Right, s.Bottom));
        Assert.Equal((20, 13, 23, 10), (s.OutLeft, s.OutTop, s.OutRight, s.OutBottom));
        Assert.Equal(Path.Combine("S", "sep.png"), s.Separator);
    }
}

public class StretchMarginTests
{
    [Fact]
    public void Grows_declared_margin_past_a_wide_corner()
    {
        // 40x4 image: columns 0-11 and 28-39 vary (corner), 12-27 are uniform.
        const int w = 40, h = 4;
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var edge = Math.Min(x, w - 1 - x);
            px[(y * w + x) * 4 + 3] = (byte)(edge < 12 ? edge * 10 : 200);
        }
        var (l, t, r, b) = Skin.StretchMargins(px, w, h, 4, 2, 4, 1);
        Assert.Equal((12, 2, 12, 1), (l, t, r, b));
    }
}

public class HostileSkinTests
{
    [Fact]
    public void Negative_and_huge_margins_are_clamped()
    {
        var s = Skin.Load("S", p => p.EndsWith("background.ini")
            ? "[Background]\nImage=bg.png\nLeftMargin=-50\nRightMargin=99999\nOutside-TopMargin=-3\n" : null)!;
        Assert.Equal(0, s.Left);
        Assert.Equal(256, s.Right);
        Assert.Equal(0, s.OutTop);

        var px = new byte[10 * 4 * 4];
        var (l, t, r, b) = Skin.StretchMargins(px, 10, 4, s.Left, -5, s.Right, 999);
        Assert.InRange(l, 0, 5);
        Assert.InRange(r, 0, 5);
        Assert.InRange(t, 0, 2);
        Assert.InRange(b, 0, 2);
    }

    [Fact]
    public void Short_or_mismatched_pixel_buffers_do_not_throw() =>
        Assert.Equal((0, 0, 0, 0), Skin.StretchMargins(new byte[8], 10, 10, 2, 2, 2, 2));

    [Fact]
    public void Truncated_png_header_is_not_a_png() => Assert.Null(Skin.PngSize(new byte[10]));
}
