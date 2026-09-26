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

    /// <summary>Inputs to the knock model (<see cref="KnockModel"/>).</summary>
    public readonly record struct KnockConditions(
        double Rpm,
        double Octane,
        double CompressionRatio,
        double PortPressurePa,
        double ChargeTemperatureK,
        double CoolantTemperatureK,
        double Lambda,
        double DeckClearanceMm,
        double ExhaustToIntakePressureRatio = 1.0,
        double OctaneSensitivity = 0.0);

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

    /// <summary>
    /// Heat released by full oxidation per mole of O₂ consumed, J/mol (Thornton's rule: nearly the same for
    /// every hydrocarbon and alcohol fuel, on a lower-heating-value basis).
    /// </summary>
    public const double HeatPerMoleOxygen = 406e3;

    /// <summary>
    /// Heating value left in the products per mole of O₂ the mixture is short of (a CO/H₂ mix: 566 kJ per mol
    /// O₂ as CO, 484 as H₂).
    /// </summary>
    public const double UnreleasedHeatPerMoleOxygenDeficit = 525e3;

    /// <summary>
    /// Fraction of the stoichiometric heat release a rich mixture actually releases (1 at λ ≥ 1). The air
    /// burns all the fuel it can, but the oxygen is shared across the excess fuel, which leaves as CO and
    /// H₂ carrying chemical energy: ≈ 96 % at λ 0.88, ≈ 90 % at λ 0.75. This, not an empirical offset, is
    /// why rich mixtures run cool exhausts.
    /// </summary>
    public static double RichHeatRelease(double lambda)
    {
        if (!(lambda < 1.0)) return 1.0;
        double l = Math.Max(lambda, 0.3);
        double released = UnreleasedHeatPerMoleOxygenDeficit - (UnreleasedHeatPerMoleOxygenDeficit - HeatPerMoleOxygen) / l;
        return released / HeatPerMoleOxygen;
    }

    /// <summary>Fraction of burned-fuel energy rejected to oil (piston underside, rings).</summary>
    public const double OilHeatFraction = 0.03;

    /// <summary>Share of friction heat that goes into the oil (the rest into the coolant).</summary>
    public const double FrictionHeatToOil = 0.35;

    /// <summary>
    /// Share of port-injected fuel that evaporates from the intake air before the inlet valve closes (the
    /// rest evaporates off hot port walls and in the cylinder). Sets the charge cooling:
    /// ΔT = share · (F/A) · h_vap / c_p — about 5 K for gasoline at λ 1, 17 K for E85.
    /// </summary>
    public const double EvaporatedBeforeInletValveCloses = 0.2;

    /// <summary>Charge-temperature drop from fuel evaporating in the intake, K.</summary>
    public static double EvaporativeCooling(double fuelAirRatio, double latentHeat) =>
        EvaporatedBeforeInletValveCloses * Math.Max(0.0, fuelAirRatio) * latentHeat / PhysicalConstants.AirCp;

    /// <summary>
    /// While not firing (fuel cut, motoring), the fraction of the coolant-to-charge temperature difference
    /// the gas picks up from the combustion-chamber walls on its way to the exhaust.
    /// </summary>
    public const double MotoringWallHeatPickup = 0.3;

    /// <summary>Where the energy released by the burned fuel goes (W). Sums exactly to the released power.</summary>
    public readonly record struct HeatSplit(double Indicated, double ToCoolant, double ToOil, double ToExhaust);

    /// <summary>
    /// Splits the burned-fuel power into indicated work, wall heat (coolant, oil) and exhaust enthalpy.
    /// The exhaust receives whatever the work and walls do not take, so the split always conserves energy;
    /// if the wall shares ever exceeded what is left after the work, they are scaled down together.
    /// </summary>
    public static HeatSplit SplitHeat(double fuelPower, double indicatedPower, double coolantFraction, double oilFraction)
    {
        double available = fuelPower - indicatedPower;
        double coolant = fuelPower * coolantFraction;
        double oil = fuelPower * oilFraction;
        double wall = coolant + oil;
        if (wall > available && wall > 0)
        {
            double scale = Math.Max(0.0, available) / wall;
            coolant *= scale;
            oil *= scale;
        }
        return new HeatSplit(indicatedPower, coolant, oil, available - coolant - oil);
    }
}
