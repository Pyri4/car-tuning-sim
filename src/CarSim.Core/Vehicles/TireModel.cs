using CarSim.Core.Common;
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

    // ---- Inflation pressure and temperature --------------------------------------------------

    /// <summary>Temperature at which the cold pressure is set (the garage), K.</summary>
    public const double ColdReferenceK = 298.15;

    /// <summary>Atmospheric pressure, kPa (gauge ↔ absolute).</summary>
    public const double AtmosphereKpa = 101.325;

    /// <summary>Grip lost per unit of squared relative pressure error ((p − p_opt)/p_opt)².</summary>
    public const double PressureGripLoss = 0.8;

    /// <summary>Exponents of (p_opt/p) on the peak slip angle and ratio: a softer carcass needs more slip.</summary>
    public const double PressureSlipAngleExponent = 0.5, PressureSlipRatioExponent = 0.3;

    /// <summary>Exponent of (p_opt/p) on rolling resistance.</summary>
    public const double PressureRollingExponent = 0.6;

    /// <summary>Gauge pressure of the tyre at tread temperature <paramref name="temperatureK"/> (ideal gas, fixed volume).</summary>
    public static double HotPressureKpa(TireSpec t, double temperatureK) =>
        (t.ColdPressureKpa + AtmosphereKpa) * temperatureK / ColdReferenceK - AtmosphereKpa;

    /// <summary>Pressure at the compound's optimal temperature: what the cold setting becomes on track.</summary>
    public static double OperatingPressureKpa(TireSpec t) => HotPressureKpa(t, Units.CToK(t.OptimalTemperatureC));

    /// <summary>Grip multiplier from inflation: 1 at the optimal pressure, lower either side.</summary>
    public static double PressureGripFactor(TireSpec t, double pressureKpa)
    {
        double e = (pressureKpa - t.OptimalPressureKpa) / t.OptimalPressureKpa;
        return Math.Max(0.5, 1.0 - PressureGripLoss * e * e);
    }

    /// <summary>Grip multiplier from tread temperature: 1 in the middle of the compound's window.</summary>
    public static double ThermalGripFactor(TireSpec t, double temperatureK)
    {
        double x = (Units.KToC(temperatureK) - t.OptimalTemperatureC) / t.TemperatureWindowC;
        return 1.0 - t.TemperatureGripLoss * (1.0 - Math.Exp(-x * x));
    }

    /// <summary>Peak slip angle (rad) at <paramref name="pressureKpa"/>.</summary>
    public static double PeakSlipAngle(TireSpec t, double pressureKpa) =>
        t.PeakSlipAngle * Math.Pow(t.OptimalPressureKpa / Math.Max(20, pressureKpa), PressureSlipAngleExponent);

    public static double PeakSlipRatio(TireSpec t, double pressureKpa) =>
        t.PeakSlipRatio * Math.Pow(t.OptimalPressureKpa / Math.Max(20, pressureKpa), PressureSlipRatioExponent);

    public static double RollingResistance(TireSpec t, double pressureKpa) =>
        t.RollingResistance * Math.Pow(t.OptimalPressureKpa / Math.Max(20, pressureKpa), PressureRollingExponent);

    // ---- Camber -------------------------------------------------------------------------------

    /// <summary>
    /// Camber (relative to the road) at which lateral grip peaks: leaning this far into the corner,
    /// degrees (negative = top of the tyre leaning towards the centre of the turn).
    /// </summary>
    public const double OptimalCamberDeg = -2.0;

    /// <summary>Lateral grip lost far from the optimum, and the width of the camber window (degrees).</summary>
    public const double CamberGripLoss = 0.08, CamberWindowDeg = 2.5;

    /// <summary>
    /// Offset from the optimum that the authored peak friction corresponds to (a typical road
    /// alignment in a corner), so stock cars keep their calibrated grip and a better alignment gains.
    /// </summary>
    public const double ReferenceCamberOffsetDeg = 2.5;

    /// <summary>Longitudinal grip lost per degree of camber (the contact patch rolls onto one edge).</summary>
    public const double CamberLongitudinalLossPerDeg = 0.012;

    private static double CamberLoss(double offsetDeg) =>
        CamberGripLoss * (1.0 - Math.Exp(-(offsetDeg / CamberWindowDeg) * (offsetDeg / CamberWindowDeg)));

    /// <summary>
    /// Lateral grip multiplier for a tyre whose top leans <paramref name="leanIntoForceDeg"/> degrees
    /// towards the direction it is pushing the car (i.e. into the corner).
    /// </summary>
    public static double CamberLateralFactor(double leanIntoForceDeg) =>
        (1.0 - CamberLoss(-leanIntoForceDeg - OptimalCamberDeg)) / (1.0 - CamberLoss(ReferenceCamberOffsetDeg));

    /// <summary>Longitudinal grip multiplier for camber <paramref name="camberDeg"/> relative to the road.</summary>
    public static double CamberLongitudinalFactor(double camberDeg) =>
        (1.0 - CamberLongitudinalLossPerDeg * Math.Abs(camberDeg)) / (1.0 - CamberLongitudinalLossPerDeg * 0.5);

    /// <summary>Peak friction coefficient at vertical load <paramref name="fz"/> (at the tyre's operating pressure and temperature).</summary>
    public static double Friction(TireSpec t, double fz) => Friction(t, fz, TyreState.Operating(t));

    /// <summary>Peak friction coefficient at vertical load <paramref name="fz"/> in a given pressure/temperature state.</summary>
    public static double Friction(TireSpec t, double fz, TyreState state)
    {
        double ratio = Math.Max(0.05, fz / ReferenceLoad);
        double mu = t.PeakFriction * (1.0 - t.LoadSensitivity * Math.Log2(ratio));
        return Math.Clamp(mu, 0.3 * t.PeakFriction, 1.3 * t.PeakFriction) * PressureGripFactor(t, state.PressureKpa) * ThermalGripFactor(t, state.TemperatureK);
    }

    /// <summary>
    /// Tyre forces in the wheel frame. Positive slip ratio (wheel faster than the ground) pushes
    /// forward; positive slip angle (wheel moving to its left) produces a force to the right.
    /// <paramref name="leanLeftDeg"/> is the tyre's camber relative to the road, as a lean of its top
    /// towards the wheel's left (0 = upright). <paramref name="state"/> is its pressure and tread
    /// temperature (default: warmed up to the compound's optimum).
    /// </summary>
    public static (double Fx, double Fy) Forces(TireSpec t, double fz, double slipRatio, double slipAngle, double leanLeftDeg = 0.0, TyreState? state = null)
    {
        if (fz <= 0) return (0.0, 0.0);
        var st = state ?? TyreState.Operating(t);
        double sx = slipRatio / PeakSlipRatio(t, st.PressureKpa);
        double sy = Math.Tan(Math.Clamp(slipAngle, -1.5, 1.5)) / Math.Tan(PeakSlipAngle(t, st.PressureKpa));
        double s = Math.Sqrt(sx * sx + sy * sy);
        if (s < 1e-12) return (0.0, 0.0);
        double f = Friction(t, fz, st) * fz * Shape(s);
        double fx = f * sx / s, fy = -f * sy / s;
        if (leanLeftDeg != 0.0 || fy != 0.0)
        {
            // Leaning into the force (the top towards where the tyre pushes the car) helps; away hurts.
            double leanIntoForce = Math.Sign(fy) * leanLeftDeg;
            fy *= CamberLateralFactor(leanIntoForce);
            fx *= CamberLongitudinalFactor(leanLeftDeg);
        }
        return (fx, fy);
    }
}

/// <summary>A tyre's running state: gauge pressure (kPa) and tread temperature (K).</summary>
public readonly record struct TyreState(double PressureKpa, double TemperatureK)
{
    /// <summary>Warmed up to the compound's optimal temperature, at the pressure the cold setting gives there.</summary>
    public static TyreState Operating(TireSpec t)
    {
        double k = Units.CToK(t.OptimalTemperatureC);
        return new TyreState(TireModel.HotPressureKpa(t, k), k);
    }

    public static TyreState At(TireSpec t, double temperatureK) => new(TireModel.HotPressureKpa(t, temperatureK), temperatureK);
}
