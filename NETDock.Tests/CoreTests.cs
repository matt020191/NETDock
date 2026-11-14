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
