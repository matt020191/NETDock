namespace NETDock.Core;

/// <summary>
/// A RocketDock skin: background.ini names a 9-slice image. The *Margin values are the
/// slice borders inside the image. The Outside-* values say how far the background reaches
/// beyond the icon area (left, top, right, bottom as seen on a bottom-edge dock).
/// </summary>
public sealed record Skin(
    string Folder, string Background, int Left, int Top, int Right, int Bottom,
    int OutLeft, int OutTop, int OutRight, int OutBottom,
    string? Separator, int SepTop, int SepBottom)
{
    public static Skin? Load(string folder, Func<string, string?> readText)
    {
        var bgIni = readText(Path.Combine(folder, "background.ini"));
        if (bgIni is null) return null;
        var bg = RocketDockImport.ParseIni(bgIni).GetValueOrDefault("Background");
        if (bg is null || !bg.TryGetValue("Image", out var image) || !IsBareName(image)) return null;
        int I(Dictionary<string, string>? d, string k) => d is not null && d.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : 0;

        var sepIni = readText(Path.Combine(folder, "separator.ini"));
        var sep = sepIni is null ? null : RocketDockImport.ParseIni(sepIni).GetValueOrDefault("Separator");
        var sepImage = sep is not null && sep.TryGetValue("Image", out var si) && IsBareName(si) ? Path.Combine(folder, si) : null;

        return new Skin(folder, Path.Combine(folder, image),
            I(bg, "LeftMargin"), I(bg, "TopMargin"), I(bg, "RightMargin"), I(bg, "BottomMargin"),
            I(bg, "Outside-LeftMargin"), I(bg, "Outside-TopMargin"), I(bg, "Outside-RightMargin"), I(bg, "Outside-BottomMargin"),
            sepImage, I(sep, "TopMargin"), I(sep, "BottomMargin"));
    }

    /// <summary>
    /// Left/right slice borders wide enough to hold the whole rounded corner. Many skins
    /// declare margins smaller than their corners and drop shadows (CrystalXP.net says 8 px;
    /// its corners are ~20 px), and stretching those columns smears the corner along the whole
    /// dock. Grows each side until the next column matches the centre column.
    /// </summary>
    public static (int L, int T, int R, int B) StretchMargins(ReadOnlySpan<byte> bgra, int w, int h, int l, int t, int r, int b, int tolerance = 6)
    {
        var cx = w / 2;
        bool ColumnLikeCentre(ReadOnlySpan<byte> px, int x)
        {
            for (var y = 0; y < h; y++)
                for (var c = 0; c < 4; c++)
                    if (Math.Abs(px[(y * w + x) * 4 + c] - px[(y * w + cx) * 4 + c]) > tolerance) return false;
            return true;
        }
        while (l < cx && !ColumnLikeCentre(bgra, l)) l++;
        while (r < w - cx - 1 && !ColumnLikeCentre(bgra, w - 1 - r)) r++;
        // Only along the dock. Vertically, skins carry a gradient (no two rows match), and the
        // panel is about the image's own height, so the declared top/bottom margins are kept.
        return (l, t, r, b);
    }

    /// <summary>Skin images must sit in the skin folder itself: no paths, drives or UNC shares.</summary>
    private static bool IsBareName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\', ':']) < 0;

    /// <summary>Width and height from a PNG header, or null if it isn't a PNG.</summary>
    public static (int W, int H)? PngSize(ReadOnlySpan<byte> head)
    {
        if (head.Length < 24 || head[0] != 0x89 || head[1] != (byte)'P') return null;
        static int Be(ReadOnlySpan<byte> b) => b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3];
        return (Be(head[16..20]), Be(head[20..24]));
    }
}
