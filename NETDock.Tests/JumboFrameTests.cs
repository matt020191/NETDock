using NETDock.Core;
using Xunit;

namespace NETDock.Tests;

public class JumboFrameTests
{
    private static byte[] Image(int size, bool frame, (int X0, int Y0, int X1, int Y1)? icon)
    {
        var px = new byte[size * size * 4];
        void Set(int x, int y) { var o = (y * size + x) * 4; px[o] = px[o + 1] = px[o + 2] = 200; px[o + 3] = 255; }
        if (frame)
            for (var i = 0; i < size; i++) { Set(i, 0); Set(i, size - 1); Set(0, i); Set(size - 1, i); }
        if (icon is { } r)
            for (var y = r.Y0; y <= r.Y1; y++) for (var x = r.X0; x <= r.X1; x++) Set(x, y);
        return px;
    }

    [Fact]
    public void Small_icon_in_a_frame_is_found()
    {
        var r = JumboFrame.Inner(Image(128, frame: true, icon: (40, 40, 87, 87)), 128, 128);
        Assert.NotNull(r);
        var (x, y, size) = r.Value;
        Assert.InRange(size, 48, 56);
        Assert.True(x <= 40 && y <= 40 && x + size >= 88 && y + size >= 88);
        Assert.Equal(size * size * 4, JumboFrame.Crop(Image(128, true, (40, 40, 87, 87)), 128, x, y, size).Length);
    }

    [Fact]
    public void Thick_border_like_windows_11_is_found()
    {
        var px = Image(128, frame: false, icon: (40, 40, 87, 87));
        for (var t = 0; t < 3; t++)
            for (var i = 0; i < 128; i++)
                foreach (var (x, y) in new[] { (i, t), (i, 127 - t), (t, i), (127 - t, i) }) px[(y * 128 + x) * 4 + 3] = 40;
        Assert.NotNull(JumboFrame.Inner(px, 128, 128));
    }

    [Fact]
    public void Full_size_icons_are_left_alone()
    {
        Assert.Null(JumboFrame.Inner(Image(128, frame: false, icon: (8, 8, 119, 119)), 128, 128));  // no border
        Assert.Null(JumboFrame.Inner(Image(128, frame: true, icon: (1, 1, 126, 126)), 128, 128));   // fills its square
        Assert.Null(JumboFrame.Inner(Image(48, frame: true, icon: (16, 16, 31, 31)), 48, 48));      // small sizes never framed
        Assert.Null(JumboFrame.Inner(Image(128, frame: true, icon: null), 128, 128));               // nothing inside
    }
}
