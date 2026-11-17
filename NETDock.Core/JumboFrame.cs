namespace NETDock.Core;

/// <summary>
/// When a program has no icon as big as the dock asks for, Windows hands back a "jumbo" image: a faint
/// tile border with the small icon sitting in the middle (Bambu Studio's 48 px icon in a 128 px frame). Drawn as is,
/// it looks half the size of its neighbours. This finds that frame and the icon inside it so the icon can be cropped
/// out and scaled up like RocketDock does.
/// </summary>
public static class JumboFrame
{
    /// <summary>
    /// The square inside a framed image that holds the real icon, or null when <paramref name="bgra"/> (top-down,
    /// 4 bytes a pixel) isn't a jumbo frame.
    /// </summary>
    public static (int X, int Y, int Size)? Inner(byte[] bgra, int w, int h)
    {
        if (w < 64 || h < 64 || w != h || bgra.Length < w * h * 4) return null;
        byte A(int x, int y) => bgra[(y * w + x) * 4 + 3];

        // The border: one to a few rings in which nearly every pixel is drawn (Windows 11 draws three).
        bool RingDrawn(int t)
        {
            int ring = 0, drawn = 0;
            for (var i = t; i < w - t; i++)
                foreach (var (x, y) in new[] { (i, t), (i, h - 1 - t), (t, i), (w - 1 - t, i) })
                {
                    ring++;
                    if (A(x, y) != 0) drawn++;
                }
            return drawn >= ring * 9 / 10;
        }
        var border = 0;
        while (border < 5 && RingDrawn(border)) border++;
        if (border == 0 || border == 5) return null;

        // Just inside it, an empty margin (a real icon that fills its square to the edge has no such gap).
        var band = border + Math.Max(3, w / 12);
        for (var y = border; y < band; y++)
            for (var x = border; x < w - border; x++)
                if (A(x, y) != 0 || A(x, h - 1 - y) != 0 || A(y, x) != 0 || A(w - 1 - y, x) != 0) return null;

        // The icon: everything drawn inside the border.
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (var y = border; y < h - border; y++)
            for (var x = border; x < w - border; x++)
                if (A(x, y) != 0) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        if (x1 < 0) return null;

        // A square around it, centred, with the icon's own small margin kept.
        var size = Math.Max(x1 - x0, y1 - y0) + 1;
        size = Math.Min(w - 4, size + size / 12);
        var cx = (x0 + x1 + 1) / 2;
        var cy = (y0 + y1 + 1) / 2;
        var sx = Math.Clamp(cx - size / 2, 0, w - size);
        var sy = Math.Clamp(cy - size / 2, 0, h - size);
        return (sx, sy, size);
    }

    /// <summary>Copy a square out of a top-down BGRA image.</summary>
    public static byte[] Crop(byte[] bgra, int w, int x, int y, int size)
    {
        var o = new byte[size * size * 4];
        for (var r = 0; r < size; r++)
            Buffer.BlockCopy(bgra, ((y + r) * w + x) * 4, o, r * size * 4, size * 4);
        return o;
    }
}
