using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Vehicles;

/// <summary>
/// Combined-slip tyre model. Slips are normalised by their peak values (sx = κ/κ_peak,
/// sy = tan α / tan α_peak); the resultant s = |(sx, sy)| goes through a sine-arctan shape curve that
/// peaks at s = 1 and falls towards ~71 % beyond it (sliding). The force is split back along the slip
/// direction, so braking or accelerating while cornering reduces lateral grip (friction ellipse).
/// Friction falls with load (load sensitivity), which is why load transfer costs total grip.
/// </summary>
public static class TireModel
{
    /// <summary>
    /// Shape factor: fully sliding friction → sin(C·π/2) of peak (≈ 0.71 for C = 1.5; ≈ 0.77 of peak at
    /// ten times the peak slip), so locking the wheels or sliding the car costs real grip.
    /// </summary>
    public const double ShapeC = 1.5;

    /// <summary>Reference vertical load for the authored peak friction, N.</summary>
    public const double ReferenceLoad = 3500.0;

    private static readonly double ShapeB = Math.Tan(Math.PI / (2.0 * ShapeC));

    /// <summary>Normalised force for normalised slip s ≥ 0 (1 at s = 1).</summary>
    public static double Shape(double s) => Math.Sin(ShapeC * Math.Atan(ShapeB * s));

    /// <summary>Peak friction coefficient at vertical load <paramref name="fz"/>.</summary>
    public static double Friction(TireSpec t, double fz)
    {
        double ratio = Math.Max(0.05, fz / ReferenceLoad);
        double mu = t.PeakFriction * (1.0 - t.LoadSensitivity * Math.Log2(ratio));
        return Math.Clamp(mu, 0.3 * t.PeakFriction, 1.3 * t.PeakFriction);
    }

    /// <summary>
    /// Tyre forces in the wheel frame. Positive slip ratio (wheel faster than the ground) pushes
    /// forward; positive slip angle (wheel moving to its left) produces a force to the right.
    /// </summary>
    public static (double Fx, double Fy) Forces(TireSpec t, double fz, double slipRatio, double slipAngle)
    {
        if (fz <= 0) return (0.0, 0.0);
        double sx = slipRatio / t.PeakSlipRatio;
        double sy = Math.Tan(Math.Clamp(slipAngle, -1.5, 1.5)) / Math.Tan(t.PeakSlipAngle);
        double s = Math.Sqrt(sx * sx + sy * sy);
        if (s < 1e-12) return (0.0, 0.0);
        double f = Friction(t, fz) * fz * Shape(s);
        return (f * sx / s, -f * sy / s);
    }
}
