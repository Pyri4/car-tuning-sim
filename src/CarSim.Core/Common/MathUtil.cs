namespace CarSim.Core.Common;

public static class MathUtil
{
    public static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
    public static double Clamp01(double v) => Clamp(v, 0.0, 1.0);
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Inverse lerp, clamped to [0,1]. Returns 0 when a == b.</summary>
    public static double InverseLerp(double a, double b, double v)
    {
        if (a == b) return 0.0;
        return Clamp01((v - a) / (b - a));
    }

    /// <summary>Hermite smoothstep between edges e0 and e1.</summary>
    public static double SmoothStep(double e0, double e1, double x)
    {
        double t = InverseLerp(e0, e1, x);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>Smooth positive part: ~max(0,x) but differentiable, width k.</summary>
    public static double SoftPlus(double x, double k)
    {
        if (k <= 0) return Math.Max(0.0, x);
        return k * Math.Log(1.0 + Math.Exp(x / k));
    }

    /// <summary>Exponential approach factor for first-order lag: fraction of the gap closed in dt.</summary>
    public static double LagFactor(double dt, double timeConstant)
    {
        if (timeConstant <= 0) return 1.0;
        return 1.0 - Math.Exp(-dt / timeConstant);
    }

    public static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
