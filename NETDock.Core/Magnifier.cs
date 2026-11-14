namespace NETDock.Core;

/// <summary>
/// Icon scales along the dock axis. Pure maths so it can be tested; the renderer
/// smooths between successive results.
/// </summary>
public static class Magnifier
{
    /// <param name="cursor">Cursor position along the axis, relative to the start of the
    /// un-magnified row, or null when the mouse is away.</param>
    /// <returns>Scale of each icon: 1 at rest, up to max/base under the cursor.</returns>
    public static double[] Scales(int count, double baseSize, double maxSize, double spacing, double range, double? cursor)
    {
        var scales = new double[count];
        Array.Fill(scales, 1.0);
        if (cursor is not { } c || maxSize <= baseSize) return scales;
        var pitch = baseSize + spacing;
        var gain = maxSize / baseSize - 1;
        for (var i = 0; i < count; i++)
        {
            var centre = i * pitch + baseSize / 2;
            var d = Math.Abs(c - centre) / (pitch * range);
            if (d < 1) scales[i] = 1 + gain * (Math.Cos(d * Math.PI) + 1) / 2;
        }
        return scales;
    }

    /// <summary>Total length of a row with the given scales.</summary>
    public static double Length(ReadOnlySpan<double> scales, double baseSize, double spacing)
    {
        double total = 0;
        foreach (var s in scales) total += s * baseSize;
        return total + Math.Max(0, scales.Length - 1) * spacing;
    }
}
