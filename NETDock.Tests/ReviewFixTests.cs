using NETDock.Core;
using Xunit;

namespace NETDock.Tests;

/// <summary>Regression tests for the 2026-11 code review fixes.</summary>
public class ReviewFixTests
{
    [Fact]
    public void Dropped_drive_root_keeps_its_quotes()
    {
        // "C:\" would end in \" and swallow the next argument (CommandLineToArgvW).
        Assert.Equal(@"""C:\\"" ""D:\x.txt""", DropRules.OpenWithArguments(null, [@"C:\", @"D:\x.txt"]));
        Assert.Equal(@"""C:\a b""", DropRules.Quote(@"C:\a b"));
        Assert.Equal(@"""\\server\share\\\\""", DropRules.Quote(@"\\server\share\\"));
    }

    private static byte[] PngHeader(uint w, uint h)
    {
        var b = new byte[24];
        b[0] = 0x89; b[1] = (byte)'P'; b[2] = (byte)'N'; b[3] = (byte)'G';
        void Be(int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
        Be(16, w); Be(20, h);
        return b;
    }

    [Theory]
    [InlineData(64u, 32u, true)]
    [InlineData(4096u, 1u, true)]
    [InlineData(4097u, 32u, false)]
    [InlineData(0u, 32u, false)]
    [InlineData(0x80000000u, 32u, false)] // negative as an int
    [InlineData(0x7FFFFFFFu, 1u, false)]
    public void Png_header_sizes_are_bounded(uint w, uint h, bool accepted) =>
        Assert.Equal(accepted, Skin.PngSize(PngHeader(w, h)) is not null);

    [Fact]
    public void Parked_mode_items_are_sanitized_too()
    {
        var s = new DockSettings
        {
            Modes =
            [
                new DockMode { Name = "Default" },
                new DockMode { Name = "Work", Items = [new DockItem { Id = "", Kind = ItemKind.Stack, Stack = null }, new DockItem { Kind = ItemKind.Stack, Stack = new StackOptions { Columns = 5000, MaxItems = 0 } }] },
            ],
            ActiveMode = "Default",
        }.Sanitize();
        var work = s.Modes[1].Items;
        Assert.False(string.IsNullOrEmpty(work[0].Id));
        Assert.NotNull(work[0].Stack);
        Assert.Equal(20, work[1].Stack!.Columns);
        Assert.Equal(1, work[1].Stack!.MaxItems);
    }

    [Fact]
    public void Dangling_active_mode_is_repaired_by_sanitize_not_by_readers()
    {
        var s = new DockSettings { Modes = [new DockMode { Name = "Work" }], ActiveMode = "Gone" }.Sanitize();
        Assert.NotNull(ModeSwitcher.Find(s, s.ActiveMode));
        var count = s.Modes.Count;
        Assert.NotNull(ModeSwitcher.Find(s, s.ActiveMode)); // reading changes nothing
        Assert.Equal(count, s.Modes.Count);
    }
}
