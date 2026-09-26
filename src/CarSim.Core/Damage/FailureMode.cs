using CarSim.Core.Common;
using CarSim.Core.Parts;

namespace CarSim.Core.Damage;

/// <summary>Physical ways a component can fail. Each has a measurable cause.</summary>
public enum FailureMode
{
    RodTensileOverload,
    RodCompressiveOverload,
    PistonPressureOverload,
    PistonCrownOverheat,
    Detonation,
    HeadGasketBreach,
    BlockDeckFailure,
    CrankshaftOverspeed,
    CrankshaftTorsion,
    FlywheelBurst,
    RodBearingFatigue,
    MainBearingFatigue,
    OilStarvation,
    ValvePistonContact,
    CylinderHeadWarp,
    TurboOverspeed,
    TurbineOverTemperature,

    // Chassis (see Vehicles.ChassisWearModel): wear-driven, accelerated by heat.
    ClutchBurnout,
    BrakePadsWornOut,
    TyresWornOut,
    GearboxOverload,
    DifferentialOverload,
}

public static class FailureModeNames
{
    /// <summary>Parses "head_gasket_breach", "HeadGasketBreach" or "headgasketbreach".</summary>
    public static bool TryParse(string text, out FailureMode mode) =>
        Enum.TryParse(text.Replace("_", "", StringComparison.Ordinal), ignoreCase: true, out mode);

    /// <summary>snake_case name used in content and saves.</summary>
    public static string ToSnakeCase(FailureMode mode)
    {
        var name = mode.ToString();
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}

public enum FailureSeverity
{
    /// <summary>The engine keeps running with a penalty (e.g. blown head gasket, dead turbo).</summary>
    Degraded,
    /// <summary>The engine stops and cannot be restarted until the part is replaced.</summary>
    Catastrophic,
}

/// <summary>How a failure mode accumulates damage from its stress reading.</summary>
public enum DamageLaw
{
    /// <summary>
    /// High-cycle fatigue, counted per load cycle (Miner's rule): each cycle at stress ratio s adds
    /// x^p / N_rated with x = (s − endurance)/(1 − endurance). No damage below the endurance limit; at the
    /// rating the part lasts <see cref="FailureModeInfo.LifeAtRating"/> cycles.
    /// </summary>
    CycleFatigue,

    /// <summary>
    /// Stress rupture under a steady load (creep of a hot, spinning part): the same power law per second
    /// instead of per cycle; <see cref="FailureModeInfo.LifeAtRating"/> is in seconds.
    /// </summary>
    StressRupture,

    /// <summary>
    /// Thermally activated damage (Arrhenius, in kelvin): rate = exp(Θ·(1/T_rated − 1/T)) / t_rated.
    /// Life shrinks by a factor e for every T²/Θ kelvin of extra temperature, with no step anywhere.
    /// </summary>
    Thermal,

    /// <summary>Accumulated by a dedicated process in the damage model (knock, starvation, overheating, wear).</summary>
    Process,
}

/// <summary>What one load cycle is, for <see cref="DamageLaw.CycleFatigue"/>.</summary>
public enum LoadCycle
{
    /// <summary>No cycle count (time-based laws).</summary>
    None,
    /// <summary>One per cylinder per four-stroke cycle (rpm/120): combustion pressure, rod loads — each part in a set sees this.</summary>
    Combustion,
    /// <summary>Every firing of the engine (rpm/120 × cylinders): crankshaft torque pulses.</summary>
    Firing,
    /// <summary>One per revolution of the part's shaft (rpm/60): bearings, speed-rated rotating parts, gear teeth.</summary>
    Revolution,
}

/// <summary>
/// Static description of a failure mode: which part it damages, how damage accumulates and what it is
/// called. Readings compare a load with the part's rating; <see cref="StressExponent"/> turns that into a
/// stress ratio (2 for parts rated in rpm: centrifugal and inertia stress grow with speed²). Mechanical
/// parts fail at once at <see cref="InstantRatio"/> (stress); hot parts <see cref="InstantMarginK"/>
/// above their rated temperature.
/// </summary>
public sealed record FailureModeInfo(
    FailureMode Mode,
    string Category,
    FailureSeverity Severity,
    string Title,
    string Cause,
    DamageLaw Law = DamageLaw.Process,
    LoadCycle Cycle = LoadCycle.None,
    double Endurance = 0.0,
    double LifeAtRating = 0.0,
    double StressExponent = 1.0,
    double InstantRatio = 1.3,
    double ActivationK = 0.0,
    double InstantMarginK = 0.0)
{
    /// <summary>Basquin-type exponent of the cycle-fatigue and stress-rupture laws.</summary>
    public const double FatigueExponent = 6.0;

    /// <summary>
    /// Valves start touching the pistons once float lets them hang open past the valve-to-piston clearance —
    /// about 3 % above the float speed on an interference engine (readings are against 110 % of float).
    /// </summary>
    public const double ValveContactOnset = 1.03 / 1.10;

    public static readonly IReadOnlyDictionary<FailureMode, FailureModeInfo> All = new[]
    {
        Cyclic(FailureMode.RodTensileOverload, PartCategory.ConnectingRods, FailureSeverity.Catastrophic,
            "Connecting rod failure", "Connecting rod tensile overload (inertia at high engine speed)", LoadCycle.Combustion, 0.80, 3e4),
        Cyclic(FailureMode.RodCompressiveOverload, PartCategory.ConnectingRods, FailureSeverity.Catastrophic,
            "Bent connecting rod", "Connecting rod compressive overload (excessive cylinder pressure)", LoadCycle.Combustion, 0.80, 3e4),
        Cyclic(FailureMode.PistonPressureOverload, PartCategory.Pistons, FailureSeverity.Catastrophic,
            "Cracked piston", "Piston ring-land fracture from excessive cylinder pressure", LoadCycle.Combustion, 0.80, 3e4),
        new FailureModeInfo(FailureMode.PistonCrownOverheat, PartCategory.Pistons, FailureSeverity.Catastrophic,
            "Holed piston", "Piston crown overheated and melted", DamageLaw.Thermal, LifeAtRating: 1200, ActivationK: 20_000, InstantMarginK: 100),
        new FailureModeInfo(FailureMode.Detonation, PartCategory.Pistons, FailureSeverity.Catastrophic,
            "Detonation damage", "Sustained detonation (knock) eroded and cracked the pistons"),
        Cyclic(FailureMode.HeadGasketBreach, PartCategory.HeadGasket, FailureSeverity.Degraded,
            "Blown head gasket", "Head gasket could not seal the combustion pressure or overheating", LoadCycle.Combustion, 0.85, 3e4),
        Cyclic(FailureMode.BlockDeckFailure, PartCategory.Block, FailureSeverity.Catastrophic,
            "Cracked cylinder block", "Cylinder pressure exceeded what the block deck and head bolts can hold", LoadCycle.Combustion, 0.85, 3e4),
        // The crank's speed rating is a fatigue limit (torsional vibration, inertia bending): exceeding it
        // wears it out quickly but does not snap it at once.
        Cyclic(FailureMode.CrankshaftOverspeed, PartCategory.Crankshaft, FailureSeverity.Catastrophic,
            "Broken crankshaft", "Crankshaft torsional-vibration fatigue from excessive engine speed", LoadCycle.Revolution, 0.85, 1e5,
            stressExponent: 2, instant: 1.6),
        Cyclic(FailureMode.CrankshaftTorsion, PartCategory.Crankshaft, FailureSeverity.Catastrophic,
            "Broken crankshaft", "Crankshaft torsional overload from excessive torque", LoadCycle.Firing, 0.85, 1.2e5),
        Cyclic(FailureMode.FlywheelBurst, PartCategory.Flywheel, FailureSeverity.Catastrophic,
            "Flywheel burst", "Flywheel exceeded its burst speed", LoadCycle.Revolution, 0.85, 1e5, stressExponent: 2),
        Cyclic(FailureMode.RodBearingFatigue, PartCategory.RodBearings, FailureSeverity.Catastrophic,
            "Rod bearing failure", "Rod bearing overlay fatigued under excessive load", LoadCycle.Revolution, 0.80, 1e5),
        Cyclic(FailureMode.MainBearingFatigue, PartCategory.MainBearings, FailureSeverity.Catastrophic,
            "Main bearing failure", "Main bearing overlay fatigued under excessive load", LoadCycle.Revolution, 0.80, 1e5),
        new FailureModeInfo(FailureMode.OilStarvation, PartCategory.RodBearings, FailureSeverity.Catastrophic,
            "Spun rod bearing", "Oil starvation: the bearings wore through without an oil film"),
        Cyclic(FailureMode.ValvePistonContact, PartCategory.CylinderHead, FailureSeverity.Catastrophic,
            "Bent valves", "Floating valves were struck by the pistons (over-rev beyond valve float)", LoadCycle.Revolution, ValveContactOnset, 300,
            instant: 1.05),
        new FailureModeInfo(FailureMode.CylinderHeadWarp, PartCategory.CylinderHead, FailureSeverity.Degraded,
            "Warped cylinder head", "Severe overheating warped the cylinder head"),
        new FailureModeInfo(FailureMode.TurboOverspeed, PartCategory.Turbocharger, FailureSeverity.Degraded,
            "Turbocharger failure", "Compressor wheel over-sped and failed", DamageLaw.StressRupture, Endurance: 0.90, LifeAtRating: 1800,
            StressExponent: 2),
        new FailureModeInfo(FailureMode.TurbineOverTemperature, PartCategory.Turbocharger, FailureSeverity.Degraded,
            "Turbocharger failure", "Turbine inlet temperature exceeded the housing and wheel rating", DamageLaw.Thermal, LifeAtRating: 1800,
            ActivationK: 50_000, InstantMarginK: 150),
        new FailureModeInfo(FailureMode.ClutchBurnout, PartCategory.Clutch, FailureSeverity.Degraded,
            "Burnt-out clutch", "The clutch facings wore through from slipping (torque above the clutch's capacity, or heat)"),
        new FailureModeInfo(FailureMode.BrakePadsWornOut, PartCategory.Brakes, FailureSeverity.Degraded,
            "Brake pads worn out", "The pads wore down to their backing plates"),
        new FailureModeInfo(FailureMode.TyresWornOut, PartCategory.Tires, FailureSeverity.Degraded,
            "Tyres worn out", "The tread wore down to the cords"),
        // Gear teeth: one bending cycle per revolution of the shaft; brief shock loads well above the
        // continuous rating are survivable (instant fracture only at twice the rating).
        Cyclic(FailureMode.GearboxOverload, PartCategory.Gearbox, FailureSeverity.Degraded,
            "Broken gearbox", "Gear teeth sheared: more torque went through the gearbox than it is built for", LoadCycle.Revolution, 0.9, 2e5,
            instant: 2.0),
        Cyclic(FailureMode.DifferentialOverload, PartCategory.Differential, FailureSeverity.Degraded,
            "Broken differential", "Crown wheel and pinion teeth sheared: more torque than the differential is built for", LoadCycle.Revolution, 0.9, 2e5,
            instant: 2.0),
    }.ToDictionary(i => i.Mode);

    private static FailureModeInfo Cyclic(FailureMode mode, string category, FailureSeverity severity, string title, string cause,
        LoadCycle cycle, double endurance, double cyclesAtRating, double stressExponent = 1.0, double instant = 1.3) =>
        new(mode, category, severity, title, cause, DamageLaw.CycleFatigue, cycle, endurance, cyclesAtRating, stressExponent, instant);

    public static FailureModeInfo Of(FailureMode mode) => All[mode];

    /// <summary>Stress ratio for a load/rating ratio (speed-rated parts: stress ∝ speed²).</summary>
    public double StressRatio(double loadRatio) => StressExponent == 1.0 ? loadRatio : Math.Pow(Math.Max(0.0, loadRatio), StressExponent);

    /// <summary>
    /// Load cycles per second a part in this mode sees at <paramref name="rpm"/> (for gearbox and
    /// differential, the speed of the shaft carrying the load).
    /// </summary>
    public double CyclesPerSecond(double rpm, int cylinders) => Cycle switch
    {
        LoadCycle.Combustion => Math.Abs(rpm) / 120.0,
        LoadCycle.Firing => Math.Abs(rpm) / 120.0 * cylinders,
        LoadCycle.Revolution => Math.Abs(rpm) / 60.0,
        _ => 0.0,
    };

    /// <summary>
    /// Damage (fraction of life) per second at a reading. <paramref name="loadRatio"/> is load/rating;
    /// thermal modes take the temperature and its rating in °C and work in kelvin.
    /// </summary>
    public double DamageRate(double loadRatio, double load, double rating, double rpm, int cylinders)
    {
        switch (Law)
        {
            case DamageLaw.CycleFatigue:
                return FatiguePerCycle(StressRatio(loadRatio)) * CyclesPerSecond(rpm, cylinders);
            case DamageLaw.StressRupture:
                return FatiguePerCycle(StressRatio(loadRatio));
            case DamageLaw.Thermal:
            {
                double t = Units.CToK(load), tRated = Units.CToK(rating);
                if (t <= 0 || tRated <= 0) return 0.0;
                return Math.Exp(ActivationK * (1.0 / tRated - 1.0 / t)) / LifeAtRating;
            }
            default:
                return 0.0;
        }
    }

    /// <summary>
    /// Fraction of life used per load cycle (per second for <see cref="DamageLaw.StressRupture"/>) at a stress
    /// ratio: x^p / life at rating, x = (s − endurance)/(1 − endurance). One smooth curve through the rating.
    /// </summary>
    public double FatiguePerCycle(double stressRatio)
    {
        if (stressRatio <= Endurance || LifeAtRating <= 0) return 0.0;
        double x = (stressRatio - Endurance) / (1.0 - Endurance);
        return Math.Pow(x, FatigueExponent) / LifeAtRating;
    }

    /// <summary>Whether the reading breaks the part outright (fracture, burst, melting).</summary>
    public bool IsInstant(double loadRatio, double load, double rating) => Law switch
    {
        DamageLaw.Thermal => InstantMarginK > 0 && load >= rating + InstantMarginK,
        DamageLaw.Process => false,
        _ => StressRatio(loadRatio) >= InstantRatio,
    };

    /// <summary>Load ratio at which damage starts (the endurance limit expressed on the reading's scale).</summary>
    public double DamageOnsetLoadRatio => StressExponent == 1.0 ? Endurance : Math.Pow(Endurance, 1.0 / StressExponent);
}
