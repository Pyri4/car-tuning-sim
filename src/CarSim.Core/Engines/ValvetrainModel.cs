using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Engines;

/// <summary>
/// Valve-float estimate. The cam is approximated by a harmonic (1 − cos) lift profile over the
/// advertised event duration, whose peak deceleration is a = 2π²·L / t². The spring's open force
/// must supply m·a to keep the follower on the lobe; the speed where it no longer can is the float
/// speed:  rpm_float = (D/6) · sqrt(F / (2π²·m·L)), with D the advertised duration in crank degrees.
/// </summary>
public static class ValvetrainModel
{
    /// <summary>Advertised duration ≈ duration at 1 mm lift plus the opening/closing ramps.</summary>
    public const double RampAllowanceDeg = 50.0;

    /// <summary>Fraction of spring force lost when the springs are fully worn (fatigued/sagged).</summary>
    public const double SpringForceLossAtFullWear = 0.15;

    public static double FloatRpm(double durationAt1mmDeg, double liftM, double springOpenForceN, double valveMovingMassKg)
    {
        double advertised = durationAt1mmDeg + RampAllowanceDeg;
        double denom = 2.0 * Math.PI * Math.PI * valveMovingMassKg * liftM;
        if (denom <= 0) return double.PositiveInfinity;
        return advertised / 6.0 * Math.Sqrt(springOpenForceN / denom);
    }

    /// <summary>Spring force at full lift after wear (sag) has taken its share.</summary>
    public static double SpringForceN(ValveSpringSpec springs, double springWear) =>
        springs.OpenForceN * (1.0 - SpringForceLossAtFullWear * Math.Clamp(springWear, 0.0, 1.0));

    /// <summary>Float speed of the whole valvetrain on the camshafts' base profile: the lower of the intake and exhaust sides.</summary>
    public static double FloatRpm(CamshaftSpec cams, ValveSpringSpec springs, CylinderHeadSpec head, double springWear = 0.0) =>
        FloatRpm(cams.Profile(false), springs, head, springWear);

    /// <summary>
    /// Float speed on one cam profile. A variable-lift camshaft's high-lift profile may float earlier or later on the
    /// same springs: its longer event gives them more time (float ∝ duration), its extra lift asks for more force
    /// (float ∝ 1/√lift).
    /// </summary>
    public static double FloatRpm(CamProfileSpec profile, ValveSpringSpec springs, CylinderHeadSpec head, double springWear = 0.0)
    {
        double force = SpringForceN(springs, springWear);
        double intake = FloatRpm(profile.IntakeDurationDeg, Common.Units.MmToM(profile.IntakeLiftMm), force, head.ValveMovingMass);
        double exhaust = FloatRpm(profile.ExhaustDurationDeg, Common.Units.MmToM(profile.ExhaustLiftMm), force, head.ValveMovingMass);
        return Math.Min(intake, exhaust);
    }

    /// <summary>The lowest float speed over every profile the camshafts can run (what a rev limit must respect).</summary>
    public static double LowestFloatRpm(CamshaftSpec cams, ValveSpringSpec springs, CylinderHeadSpec head, double springWear = 0.0) =>
        cams.HighLiftProfile == null
            ? FloatRpm(cams, springs, head, springWear)
            : Math.Min(FloatRpm(cams, springs, head, springWear), FloatRpm(cams.HighLiftProfile, springs, head, springWear));
}
