using System.Globalization;

namespace NETDock.Core;

/// <summary>Helpers for the "#AARRGGBB" colour strings in settings.</summary>
public static class ArgbColor
{
    /// <summary>Parses #AARRGGBB or #RRGGBB (opaque); <paramref name="fallback"/> otherwise.</summary>
    public static uint Parse(string? hex, uint fallback)
    {
        if (hex is { Length: 7 or 9 } && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, null, out var v))
            return hex.Length == 7 ? 0xFF000000 | v : v;
        return fallback;
    }

    public static string Format(uint argb) => "#" + argb.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Opacity as 0-100 %.</summary>
    public static int Opacity(string? hex, uint fallback) => (int)Math.Round((Parse(hex, fallback) >> 24) * 100 / 255.0);

    public static string WithOpacity(string? hex, uint fallback, int percent) =>
        Format((Parse(hex, fallback) & 0xFFFFFF) | ((uint)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0, MidpointRounding.AwayFromZero) << 24));

    /// <summary>Keeps the alpha, replaces the colour (rgb as 0xRRGGBB).</summary>
    public static string WithRgb(string? hex, uint fallback, uint rgb) =>
        Format((Parse(hex, fallback) & 0xFF000000) | (rgb & 0xFFFFFF));
}
