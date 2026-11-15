namespace NETDock.Core;

/// <summary>Icon animations for launching and for windows asking for attention (RocketDock: icon effects).</summary>
public enum IconEffect { None, Bounce, Zoom, Pulse, Wobble }

/// <summary>One frame of an effect: lift off the edge (× icon size), scale, opacity, sideways shift (× icon size).</summary>
public readonly record struct EffectFrame(double Lift, double Scale, double Alpha, double Shift)
{
    public static readonly EffectFrame Rest = new(0, 1, 1, 0);
}

public static class IconEffects
{
    /// <summary>Length of one run of the effect, in seconds. Attention loops it.</summary>
    public const double Duration = 1.2;

    public static EffectFrame Sample(IconEffect effect, double t)
    {
        if (t < 0 || t >= Duration) return EffectFrame.Rest;
        var decay = Math.Exp(-t * 2.2);
        return effect switch
        {
            // Two decaying hops.
            IconEffect.Bounce => EffectFrame.Rest with { Lift = Math.Abs(Math.Sin(t * Math.PI * 2.5)) * decay * 0.5 },
            // Swell and settle twice.
            IconEffect.Zoom => EffectFrame.Rest with { Scale = 1 + Math.Abs(Math.Sin(t * Math.PI * 2.5)) * decay * 0.3 },
            // Fade down and back up.
            IconEffect.Pulse => EffectFrame.Rest with { Alpha = 1 - Math.Abs(Math.Sin(t * Math.PI * 2.5)) * decay * 0.75 },
            // Shake side to side.
            IconEffect.Wobble => EffectFrame.Rest with { Shift = Math.Sin(t * Math.PI * 8) * decay * 0.12 },
            _ => EffectFrame.Rest,
        };
    }
}
