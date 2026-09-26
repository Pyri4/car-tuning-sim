using CarSim.Core.Common;

namespace CarSim.Core.Simulation;

/// <summary>
/// Closed-form combustion relationships. Every function is pure so it can be tested in isolation.
/// Constants and their rationale are listed in SIMULATION_SPEC.md.
/// </summary>
public static class CombustionModel
{
    /// <summary>Polytropic exponent used in the Otto-cycle efficiency estimate.</summary>
    public const double EfficiencyGamma = 1.3;

    /// <summary>Fraction of ideal Otto efficiency realised (heat loss, finite burn, blow-by).</summary>
    public const double EfficiencyRealisation = 0.80;

    /// <summary>
    /// Relative torque multiplier vs λ, applied to the energy of the fuel that actually burns
    /// (air-limited when rich, fuel-limited when lean). Peaks near best-power λ ≈ 0.88.
    /// Lean-side values include the slightly higher efficiency of lean burn; beyond λ ≈ 1.4 the flame
    /// becomes unstable and misfires.
    /// </summary>
    public static readonly Curve1D MixtureFactor = Curve1D.FromPoints(
        (0.40, 0.30), (0.50, 0.70), (0.60, 0.90), (0.70, 0.99), (0.75, 1.02), (0.80, 1.035), (0.88, 1.04),
        (0.95, 1.025), (1.00, 1.00), (1.10, 1.034), (1.20, 1.038), (1.30, 1.01), (1.40, 0.92),
        (1.50, 0.675), (1.70, 0.17), (2.00, 0.0));

    /// <summary>Ideal-cycle indicated efficiency 1 − CR^(1−γ) times the realisation factor.</summary>
    public static double IndicatedEfficiency(double compressionRatio)
    {
        if (compressionRatio <= 1.0) return 0.0;
        return (1.0 - Math.Pow(compressionRatio, 1.0 - EfficiencyGamma)) * EfficiencyRealisation;
    }

    /// <summary>
    /// Minimum advance for best torque (MBT), degrees BTDC. Faster piston motion needs more lead;
    /// denser charge burns faster and needs less.
    /// </summary>
    /// <param name="rpm">Engine speed.</param>
    /// <param name="chargeDensityRatio">Trapped charge density relative to 1 atm, 298 K air.</param>
    public static double MbtAdvance(double rpm, double chargeDensityRatio)
    {
        double speed = Math.Sqrt(MathUtil.Clamp(rpm / 6000.0, 0.05, 1.5));
        return 14.0 + 14.0 * speed - 7.0 * (chargeDensityRatio - 1.0);
    }

    /// <summary>Loss coefficient per squared degree away from MBT.</summary>
    public const double SparkLossRetard = 0.0003;
    public const double SparkLossAdvance = 0.0004;

    /// <summary>Torque multiplier for running <paramref name="advance"/> instead of MBT.</summary>
    public static double SparkFactor(double advance, double mbt)
    {
        double d = advance - mbt;
        double k = d < 0 ? SparkLossRetard : SparkLossAdvance;
        return Math.Max(0.1, 1.0 - k * d * d);
    }

    /// <summary>Inputs to the knock-limit estimate.</summary>
    public readonly record struct KnockConditions(
        double Rpm,
        double Octane,
        double CompressionRatio,
        double PortPressurePa,
        double ChargeTemperatureK,
        double CoolantTemperatureK,
        double Lambda,
        double DeckClearanceMm);

    /// <summary>Port pressure ratio below which the knock limit rises steeply (light load).</summary>
    public const double LightLoadPressureRatio = 0.45;

    /// <summary>Extra knock-limit degrees per unit of pressure ratio below <see cref="LightLoadPressureRatio"/>.</summary>
    public const double LightLoadKnockMargin = 60.0;

    /// <summary>
    /// Knock-limited spark advance (degrees BTDC): the most advance the end gas tolerates.
    /// Reference: RON 95, CR 10.5, 1 atm port pressure, 40 °C charge, 90 °C coolant, λ 1, 3000 rpm → 24°.
    /// </summary>
    public static double KnockLimitedAdvance(in KnockConditions c)
    {
        double klsa = 24.0;
        klsa += 1.3 * (c.Octane - 95.0);
        klsa -= 3.2 * (c.CompressionRatio - 10.5);
        double pr = c.PortPressurePa / PhysicalConstants.StandardPressure;
        klsa -= 14.0 * (pr - 1.0);
        // Light load: too little end-gas pressure to autoignite, whatever the timing (overrun, cruise).
        klsa += LightLoadKnockMargin * Math.Max(0.0, LightLoadPressureRatio - pr);
        klsa -= 0.25 * (c.ChargeTemperatureK - 313.15);
        klsa -= 0.20 * Math.Max(0.0, c.CoolantTemperatureK - 363.15);
        klsa += 20.0 * (1.0 - Math.Min(c.Lambda, 1.3));
        klsa += 2.5 * (c.Rpm - 3000.0) / 1000.0;
        klsa -= 1.5 * Math.Max(0.0, c.DeckClearanceMm - 1.0);
        return klsa;
    }

    /// <summary>Knock intensity: degrees of advance beyond the knock limit (0 = no knock).</summary>
    public static double KnockIntensity(double advance, double knockLimit) => Math.Max(0.0, advance - knockLimit);

    /// <summary>Torque penalty for knocking combustion.</summary>
    public static double KnockTorqueFactor(double knockIntensity) => Math.Max(0.5, 1.0 - 0.01 * knockIntensity);

    /// <summary>
    /// Peak cylinder pressure estimate, Pa: motored compression pressure plus a combustion rise
    /// proportional to gross IMEP, increased by over-advance and knock.
    /// </summary>
    public static double PeakCylinderPressure(double portPressure, double compressionRatio, double imepGross,
        double advance, double mbt, double knockIntensity)
    {
        double compression = portPressure * Math.Pow(compressionRatio, PhysicalConstants.CompressionPolytropicExponent);
        double timing = MathUtil.Clamp(1.0 + 0.025 * (advance - mbt), 0.3, 2.0);
        double rise = 3.4 * Math.Max(0.0, imepGross) * timing;
        return (compression + rise) * (1.0 + 0.04 * knockIntensity);
    }

    /// <summary>
    /// Friction mean effective pressure, Pa. Rubbing friction grows with mean piston speed; the
    /// hydrodynamic part scales with oil viscosity; valvetrain friction with spring force; a small term
    /// with peak cylinder pressure (ring and bearing loading).
    /// </summary>
    public static double FrictionMep(double meanPistonSpeed, double oilViscosity, double springOpenForceN, double peakCylinderPressure)
    {
        double viscosityFactor = Math.Sqrt(Math.Max(0.1, oilViscosity / OilViscosity.Reference));
        double kpa = 45.0
            + 4.0 * meanPistonSpeed * viscosityFactor
            + 0.2 * meanPistonSpeed * meanPistonSpeed
            + 12.0 * springOpenForceN / 560.0
            + 0.004 * peakCylinderPressure / 1000.0;
        return kpa * 1000.0;
    }

    /// <summary>
    /// Fraction of burned-fuel energy rejected to coolant. Less time per cycle at high rpm means less
    /// heat loss; knock scrubs the thermal boundary layer and increases it.
    /// </summary>
    public static double CoolantHeatFraction(double rpm, double knockIntensity) =>
        0.28 - 0.06 * MathUtil.Clamp(rpm / 7000.0, 0.0, 1.4) + 0.01 * Math.Min(knockIntensity, 10.0);

    /// <summary>Fraction of burned-fuel energy rejected to oil (piston underside, rings).</summary>
    public const double OilHeatFraction = 0.03;

    /// <summary>Share of friction heat that goes into the oil (the rest into the coolant).</summary>
    public const double FrictionHeatToOil = 0.35;
}
