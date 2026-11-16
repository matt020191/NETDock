namespace NETDock.Core;

public static class VolumeSteps
{
    /// <summary>Wheel notches to a new volume on a 5 % grid (so 37 % then one notch up is 40 %, not 42 %).</summary>
    public static float Step(float current, int notches, int stepPercent = 5)
    {
        var pct = current * 100;
        var grid = notches > 0
            ? MathF.Floor(pct / stepPercent + 0.001f) + notches
            : MathF.Ceiling(pct / stepPercent - 0.001f) + notches;
        return Math.Clamp(grid * stepPercent / 100f, 0f, 1f);
    }
}
