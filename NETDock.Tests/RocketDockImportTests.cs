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
