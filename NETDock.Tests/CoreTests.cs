using NETDock.Core;

namespace NETDock.Tests;

public class PathTokenTests
{
    private static readonly PathTokens T = new([
        ("%ProgramFiles%", @"C:\Program Files"),
        ("%ProgramFiles(x86)%", @"C:\Program Files (x86)"),
        ("%USERPROFILE%", @"C:\Users\matt"),
        ("%LOCALAPPDATA%", @"C:\Users\matt\AppData\Local"),
    ]);

    [Theory]
    [InlineData(@"C:\Program Files\App\a.exe", @"%ProgramFiles%\App\a.exe")]
    [InlineData(@"C:\Program Files (x86)\App\a.exe", @"%ProgramFiles(x86)%\App\a.exe")]
    [InlineData(@"c:\program files (x86)\App", @"%ProgramFiles(x86)%\App")]
    [InlineData(@"C:\Program Files", "%ProgramFiles%")]
    [InlineData(@"C:\Program FilesX\a.exe", @"C:\Program FilesX\a.exe")]
    [InlineData(@"C:\Users\matt\AppData\Local\x.exe", @"%LOCALAPPDATA%\x.exe")]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    public void Tokenize_matches_whole_segments(string path, string expected) => Assert.Equal(expected, T.Tokenize(path));

    [Theory]
    [InlineData(@"C:\Program Files\App\a.exe")]
    [InlineData(@"C:\Program Files (x86)\App\a.exe")]
    [InlineData(@"C:\Users\matt\Desktop")]
    public void Round_trip_is_exact(string path) => Assert.Equal(path, T.Expand(T.Tokenize(path)));
}

public class SettingsTests
{
    [Fact]
    public void Stack_options_and_enums_survive_a_round_trip()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new SettingsStore(new DataLocation(dir, portable: true, cacheFolder: dir));
        var s = new DockSettings();
        s.Items.Add(new DockItem
        {
            Kind = ItemKind.Stack, Name = "Tools", Target = @"%USERPROFILE%\Tools", Icon = "icons/abc.png",
            Stack = new StackOptions { View = StackView.List, SortBy = StackSort.DateModified, Columns = 6 },
        });
        store.Save(s);
        store.Save(s); // second save exercises the File.Replace path
        Assert.Contains("\"stack\"", File.ReadAllText(store.Location.SettingsFile));

        var back = store.Load().Items.Single();
        Assert.Equal(ItemKind.Stack, back.Kind);
        Assert.Equal(StackView.List, back.Stack!.View);
        Assert.Equal(StackSort.DateModified, back.Stack.SortBy);
        Assert.Equal(6, back.Stack.Columns);
        Assert.Equal("icons/abc.png", back.Icon);
    }

    [Fact]
    public void Export_and_import_carry_custom_icons()
    {
        var a = Directory.CreateTempSubdirectory().FullName;
        var b = Directory.CreateTempSubdirectory().FullName;
        var src = new SettingsStore(new DataLocation(a, true, a));
        var iconFile = Path.Combine(a, "my.png");
        File.WriteAllBytes(iconFile, [1, 2, 3]);
        var s = new DockSettings();
        s.Items.Add(new DockItem { Name = "x", Target = "x.exe", Icon = src.ImportIcon(iconFile) });
        var zip = Path.Combine(a, "backup.zip");
        src.Export(s, zip);

        var dst = new SettingsStore(new DataLocation(b, true, b));
        var imported = dst.Import(zip);
        Assert.True(File.Exists(dst.ResolveDataIcon(imported.Items[0].Icon)));
    }

    [Fact]
    public void Sanitize_clamps_hand_edited_values()
    {
        var s = new DockSettings { Dock = { IconSize = 5000, MagnifiedSize = 1, Centre = double.NaN } }.Sanitize();
        Assert.Equal(256, s.Dock.IconSize);
        Assert.Equal(256, s.Dock.MagnifiedSize);
        Assert.Equal(0.5, s.Dock.Centre);
    }
}

public class MagnifierTests
{
    [Fact]
    public void Icon_under_cursor_is_largest_and_far_icons_rest()
    {
        var s = Magnifier.Scales(10, 48, 96, 8, 2, cursor: 24);
        Assert.Equal(2.0, s[0], 3);
        Assert.Equal(1.0, s[9]);
        Assert.True(s[1] > s[2]);
    }
}

public class DataReferenceTests
{
    [Theory]
    [InlineData("icons/../../evil.png")]
    [InlineData(@"icons/..\x.png")]
    [InlineData(@"icons/\server\share\x.png")]
    [InlineData("icons/")]
    public void Data_icon_references_cannot_escape_the_data_folder(string reference)
    {
        var store = new SettingsStore(new DataLocation(@"C:\dock", true, @"C:\cache"));
        Assert.Null(store.ResolveDataIcon(reference));
    }

    [Fact]
    public void Plain_data_icon_reference_resolves() =>
        Assert.Equal(@"C:\dock\icons\a.png", new SettingsStore(new DataLocation(@"C:\dock", true, @"C:\cache")).ResolveDataIcon("icons/a.png"));
}

public class CopyOverTests
{
    [Fact]
    public void Read_only_source_can_be_copied_over_twice()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var src = Path.Combine(dir, "src.ico");
        File.WriteAllBytes(src, [1]);
        File.SetAttributes(src, FileAttributes.ReadOnly);
        var dest = Path.Combine(dir, "dest.ico");
        SettingsStore.CopyOver(src, dest);
        SettingsStore.CopyOver(src, dest);
        Assert.False(File.GetAttributes(dest).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(src, FileAttributes.Normal);
    }
}

public class SkinReferenceTests
{
    [Theory]
    [InlineData(@"\server\share\skin")]
    [InlineData(@"C:\Program Files (x86)\RocketDock\Skins\Vista")]
    [InlineData("skins/../../x")]
    [InlineData("skins/")]
    public void Sanitize_drops_skins_outside_the_data_folder(string skin) =>
        Assert.Null(new DockSettings { Dock = { Skin = skin } }.Sanitize().Dock.Skin);

    [Fact]
    public void Sanitize_keeps_data_folder_skin() =>
        Assert.Equal("skins/CrystalXP.net", new DockSettings { Dock = { Skin = "skins/CrystalXP.net" } }.Sanitize().Dock.Skin);

    [Theory]
    [InlineData(@"\evil\share\bg.png")]
    [InlineData(@"..\bg.png")]
    [InlineData(@"C:\x\bg.png")]
    public void Skin_images_must_be_bare_names(string image)
    {
        var s = Skin.Load("S", p => p.EndsWith("background.ini") ? $"[Background]\nImage={image}\n" : null);
        Assert.Null(s);
    }

    [Fact]
    public void Backup_import_ignores_entries_outside_skins_name_file()
    {
        var a = Directory.CreateTempSubdirectory().FullName;
        var zip = Path.Combine(a, "b.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("settings.json").Open())) w.Write("{}");
            using (var w = new StreamWriter(z.CreateEntry("skins/../../evil.txt").Open())) w.Write("x");
            using (var w = new StreamWriter(z.CreateEntry("skins-evil/x/y.txt").Open())) w.Write("x");
            using (var w = new StreamWriter(z.CreateEntry("skins/Good/bg.png").Open())) w.Write("x");
        }
        var dest = Path.Combine(a, "data");
        new SettingsStore(new DataLocation(dest, true, a)).Import(zip);
        Assert.True(File.Exists(Path.Combine(dest, "skins", "Good", "bg.png")));
        Assert.False(File.Exists(Path.Combine(a, "evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(dest, "skins-evil")));
    }
}

public class RecycleBinTests
{
    private static DockItem Bin(string? icon = null, string? full = null) => new() { Target = RecycleBin.Target, Icon = icon, IconFull = full };

    [Fact]
    public void Default_bin_switches_between_windows_empty_and_full_icons()
    {
        Assert.Equal(RecycleBin.EmptyIcon, RecycleBin.IconFor(Bin(), full: false));
        Assert.Equal(RecycleBin.FullIcon, RecycleBin.IconFor(Bin(), full: true));
        Assert.Equal(RecycleBin.FullIcon, RecycleBin.IconFor(Bin(@"%systemroot%\system32\imageres.dll,-55"), full: true));
    }

    [Fact]
    public void Custom_icons_are_respected()
    {
        Assert.Equal("icons/full.png", RecycleBin.IconFor(Bin("icons/empty.png", "icons/full.png"), full: true));
        Assert.Equal("icons/mine.png", RecycleBin.IconFor(Bin("icons/mine.png"), full: true));
    }

    [Fact]
    public void Only_the_bin_target_counts() =>
        Assert.False(RecycleBin.Is(new DockItem { Target = @"%SystemRoot%\explorer.exe" }));
}

public class DropRuleTests
{
    private static readonly DockItem App = new() { Kind = ItemKind.App, Target = @"%ProgramFiles%\Adobe\Photoshop.exe" };
    private static readonly DockItem Stack = new() { Kind = ItemKind.Stack, Target = @"C:\Games" };
    private static readonly DockItem Bin = new() { Target = RecycleBin.Target };
    private static readonly DockItem ThisPc = new() { Target = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}" };

    [Theory]
    [InlineData(0.5, DropAction.OpenWith)]
    [InlineData(0.25, DropAction.OpenWith)]
    [InlineData(0.1, DropAction.Insert)]
    [InlineData(0.95, DropAction.Insert)]
    public void Centre_of_an_app_opens_edges_insert(double pos, DropAction expected) =>
        Assert.Equal(expected, DropRules.Decide(App, pos));

    [Fact]
    public void Folders_copy_bin_recycles_gaps_and_shell_folders_insert()
    {
        Assert.Equal(DropAction.CopyInto, DropRules.Decide(Stack, 0.5));
        Assert.Equal(DropAction.Recycle, DropRules.Decide(Bin, 0.5));
        Assert.Equal(DropAction.Insert, DropRules.Decide(null, 0.5));
        Assert.Equal(DropAction.Insert, DropRules.Decide(new DockItem { Kind = ItemKind.Separator }, 0.5));
        Assert.Equal(DropAction.Insert, DropRules.Decide(ThisPc, 0.5));
    }

    [Fact]
    public void Open_with_arguments_quote_each_path_after_existing_ones() =>
        Assert.Equal(@"-new ""C:\a b.png"" ""D:\c.png""", DropRules.OpenWithArguments("-new", [@"C:\a b.png", @"D:\c.png"]));
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+R", Hotkey.Ctrl | Hotkey.Alt, (uint)'R')]
    [InlineData("ctrl + shift + f12", Hotkey.Ctrl | Hotkey.Shift, 0x7Bu)]
    [InlineData("Win+Space", Hotkey.Win, 0x20u)]
    [InlineData("Alt+1", Hotkey.Alt, (uint)'1')]
    public void Parses(string text, uint mods, uint vk) => Assert.Equal(new Hotkey(mods, vk), Hotkey.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("R")]              // no modifier: would swallow a normal key
    [InlineData("Ctrl+Alt")]       // no key
    [InlineData("Ctrl+R+T")]       // two keys
    [InlineData("Ctrl+Banana")]
    public void Rejects(string text) => Assert.Null(Hotkey.Parse(text));
}

public class ArgbColorTests
{
    [Fact]
    public void Parses_both_forms_and_falls_back()
    {
        Assert.Equal(0xFF102030u, ArgbColor.Parse("#102030", 0));
        Assert.Equal(0x80102030u, ArgbColor.Parse("#80102030", 0));
        Assert.Equal(0x12345678u, ArgbColor.Parse("red", 0x12345678));
    }

    [Fact]
    public void Opacity_and_rgb_edit_independently()
    {
        var c = ArgbColor.WithOpacity("#FF102030", 0, 50);
        Assert.Equal("#80102030", c);
        Assert.Equal(50, ArgbColor.Opacity(c, 0));
        Assert.Equal("#80AABBCC", ArgbColor.WithRgb(c, 0, 0xAABBCC));
    }
}

public class IconEffectTests
{
    [Theory]
    [InlineData(IconEffect.Bounce)]
    [InlineData(IconEffect.Zoom)]
    [InlineData(IconEffect.Pulse)]
    [InlineData(IconEffect.Wobble)]
    public void Effects_move_mid_run_and_rest_at_the_ends(IconEffect e)
    {
        Assert.Equal(EffectFrame.Rest, IconEffects.Sample(e, 0));
        Assert.Equal(EffectFrame.Rest, IconEffects.Sample(e, IconEffects.Duration));
        Assert.NotEqual(EffectFrame.Rest, IconEffects.Sample(e, 0.17));
    }

    [Fact]
    public void None_never_moves() => Assert.Equal(EffectFrame.Rest, IconEffects.Sample(IconEffect.None, 0.2));
}

public class SpecialItemTests
{
    [Fact]
    public void Special_items_are_recognised_and_never_take_drops()
    {
        var s = SpecialItems.Create(SpecialItems.Settings);
        Assert.True(SpecialItems.Is(s));
        Assert.False(SpecialItems.Is(new DockItem { Target = @"C:\x.exe" }));
        Assert.Equal(DropAction.Insert, DropRules.Decide(s, 0.5));
        Assert.Equal(DropAction.Insert, DropRules.Decide(SpecialItems.Create(SpecialItems.Quit), 0.5));
    }
}

public class MonitorSpanTests
{
    // 1920x1040 work area (taskbar at the bottom) beside a taller 2560x1440 monitor with no taskbar.
    private static readonly (int, int, int, int)[] Two = [(0, 0, 1920, 1040), (1920, -200, 4480, 1240)];

    [Fact]
    public void Bottom_dock_spans_both_and_sits_on_the_highest_bottom() =>
        Assert.Equal((0, -200, 4480, 1040), MonitorSpan.Union(Two, DockEdge.Bottom));

    [Fact]
    public void Top_dock_sits_on_the_lowest_top() =>
        Assert.Equal((0, 0, 4480, 1240), MonitorSpan.Union(Two, DockEdge.Top));
}

public class LanguageTests
{
    [Fact]
    public void Missing_or_broken_language_files_fall_back_to_english()
    {
        var dir = Directory.CreateTempSubdirectory("netdock-lang").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Pirate.json"), """{ "Exit NETDock": "Abandon ship", "Monitor {0} ({1}x{2})": "Spyglass {0}" }""");
            File.WriteAllText(Path.Combine(dir, "Broken.json"), "{ not json");
            File.WriteAllText(Path.Combine(dir, "template.json"), "{}");
            Assert.Equal(["Broken", "Pirate"], L.Available(dir));
            Assert.Null(L.Find(@"..\Pirate", dir));

            L.Load(L.Find("Pirate", dir), "Pirate");
            Assert.Equal("Abandon ship", L.T("Exit NETDock"));
            Assert.Equal("Hide dock", L.T("Hide dock"));
            Assert.Equal("Spyglass 2", L.F("Monitor {0} ({1}x{2})", 2, 1920, 1080));

            L.Load(L.Find("Broken", dir), "Broken");
            Assert.Null(L.Current);
            Assert.Equal("Exit NETDock", L.T("Exit NETDock"));
        }
        finally { L.Load(null, null); Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Keeps NETDock.App/languages/template.json in step with every L.T / L.F string in the code.
    /// When it is out of date the test rewrites it and fails once, so the change gets committed.
    /// </summary>
    [Fact]
    public void Translation_template_lists_every_ui_string()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "NETDock.slnx"))) root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("repo root not found");
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        var call = new System.Text.RegularExpressions.Regex(@"\bL\.[TF]\(""((?:[^""\\]|\\.)*)""");
        foreach (var dir in new[] { "NETDock.App", "NETDock.Core" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                foreach (System.Text.RegularExpressions.Match m in call.Matches(File.ReadAllText(file)))
                    keys.Add(System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value));
            }
        var json = new System.Text.StringBuilder("{\n");
        var i = 0;
        foreach (var k in keys)
            json.Append("  ").Append(System.Text.Json.JsonSerializer.Serialize(k, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })).Append(": \"\"").Append(++i < keys.Count ? ",\n" : "\n");
        json.Append("}\n");

        var path = Path.Combine(root, "NETDock.App", "languages", "template.json");
        var expected = json.ToString();
        var actual = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
        if (actual != expected)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, expected);
        }
        Assert.True(actual == expected, "languages/template.json was out of date and has been regenerated; commit it.");
        Assert.True(keys.Count > 150);
    }
}
