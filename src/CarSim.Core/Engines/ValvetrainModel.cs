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

    /// <summary>Float speed of the whole valvetrain: the lower of the intake and exhaust sides.</summary>
    public static double FloatRpm(CamshaftSpec cams, ValveSpringSpec springs, CylinderHeadSpec head, double springWear = 0.0)
    {
        double force = SpringForceN(springs, springWear);
        double intake = FloatRpm(cams.IntakeDurationDeg, cams.IntakeLift, force, head.ValveMovingMass);
        double exhaust = FloatRpm(cams.ExhaustDurationDeg, cams.ExhaustLift, force, head.ValveMovingMass);
        return Math.Min(intake, exhaust);
    }
}
