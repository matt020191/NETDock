namespace NETDock.Core;

/// <summary>Where the dock's accent tint comes from.</summary>
public enum AccentSource { None, Windows, Wallpaper }

/// <summary>Picks a wallpaper's accent colour: the most prominent vivid hue, not the muddy average.</summary>
public static class AccentColor
{
    /// <summary>Dominant colour of 32-bit BGRA pixels as (r, g, b) 0..255, or null for no pixels.</summary>
    public static (byte R, byte G, byte B)? Dominant(ReadOnlySpan<byte> bgra)
    {
        const int Buckets = 24; // 15° hue slices
        var weight = new double[Buckets];
        var sumR = new double[Buckets];
        var sumG = new double[Buckets];
        var sumB = new double[Buckets];
        double allR = 0, allG = 0, allB = 0;
        var count = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128) continue;
            double b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            allR += r; allG += g; allB += b; count++;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var delta = max - min;
            if (max < 40 || delta < 25) continue; // near-black or grey: no hue to speak of
            var sat = delta / max;
            double hue = max == r ? (g - b) / delta % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
            if (hue < 0) hue += 6;
            var k = Math.Min(Buckets - 1, (int)(hue / 6 * Buckets));
            var w = sat * (max / 255.0);
            weight[k] += w; sumR[k] += r * w; sumG[k] += g * w; sumB[k] += b * w;
        }
        if (count == 0) return null;
        var best = 0;
        for (var k = 1; k < Buckets; k++) if (weight[k] > weight[best]) best = k;
        // A mostly grey picture (under ~3 % vivid weight): use its plain average.
        if (weight[best] < count * 0.03)
            return (Byte(allR / count), Byte(allG / count), Byte(allB / count));
        return (Byte(sumR[best] / weight[best]), Byte(sumG[best] / weight[best]), Byte(sumB[best] / weight[best]));
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <summary>A lighter version (toward white) for small marks like running indicators.</summary>
    public static (byte R, byte G, byte B) Lighten((byte R, byte G, byte B) c, double amount) =>
        ((byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));
}
