namespace NETDock.Core;

public static class PreviewLayout
{
    /// <summary>Scale a window of <paramref name="w"/>×<paramref name="h"/> to fit a box, keeping its shape; never upscales.</summary>
    public static (int W, int H) Fit(int w, int h, int maxW, int maxH)
    {
        if (w <= 0 || h <= 0) return (maxW, maxH);
        var k = Math.Min(1.0, Math.Min(maxW / (double)w, maxH / (double)h));
        return (Math.Max(1, (int)Math.Round(w * k)), Math.Max(1, (int)Math.Round(h * k)));
    }
}
