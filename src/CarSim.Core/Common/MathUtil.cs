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

    /// <summary>
    /// Standard normal cumulative distribution Φ(x), via the Abramowitz–Stegun 7.1.26 error-function
    /// approximation (|error| &lt; 1.5·10⁻⁷, deterministic).
    /// </summary>
    public static double NormalCdf(double x)
    {
        double z = Math.Abs(x) / Math.Sqrt(2.0);
        double t = 1.0 / (1.0 + 0.3275911 * z);
        double erf = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
        return x >= 0 ? 0.5 * (1.0 + erf) : 0.5 * (1.0 - erf);
    }
}
