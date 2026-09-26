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

/// <summary>
/// Static description of a failure mode: which part it damages, how fatigue accumulates and what it
/// is called. Stress ratio r = load / rating. No fatigue below <see cref="Endurance"/>; between the
/// endurance limit and the rating the rate grows cubically; above the rating it grows very fast;
/// at <see cref="InstantRatio"/> the part fails immediately. At exactly the rating the part lasts
/// <see cref="SecondsToFailureAtRating"/>.
/// </summary>
public sealed record FailureModeInfo(
    FailureMode Mode,
    string Category,
    double Endurance,
    double SecondsToFailureAtRating,
    FailureSeverity Severity,
    string Title,
    string Cause,
    double InstantRatio = 1.3)
{
    public static readonly IReadOnlyDictionary<FailureMode, FailureModeInfo> All = new[]
    {
        new FailureModeInfo(FailureMode.RodTensileOverload, PartCategory.ConnectingRods, 0.80, 30, FailureSeverity.Catastrophic,
            "Connecting rod failure", "Connecting rod tensile overload (inertia at high engine speed)"),
        new FailureModeInfo(FailureMode.RodCompressiveOverload, PartCategory.ConnectingRods, 0.80, 30, FailureSeverity.Catastrophic,
            "Bent connecting rod", "Connecting rod compressive overload (excessive cylinder pressure)"),
        new FailureModeInfo(FailureMode.PistonPressureOverload, PartCategory.Pistons, 0.80, 30, FailureSeverity.Catastrophic,
            "Cracked piston", "Piston ring-land fracture from excessive cylinder pressure"),
        new FailureModeInfo(FailureMode.PistonCrownOverheat, PartCategory.Pistons, 0.90, 20, FailureSeverity.Catastrophic,
            "Holed piston", "Piston crown overheated and melted"),
        new FailureModeInfo(FailureMode.Detonation, PartCategory.Pistons, 0.0, 30, FailureSeverity.Catastrophic,
            "Detonation damage", "Sustained detonation (knock) eroded and cracked the pistons"),
        new FailureModeInfo(FailureMode.HeadGasketBreach, PartCategory.HeadGasket, 0.85, 20, FailureSeverity.Degraded,
            "Blown head gasket", "Head gasket could not seal the combustion pressure or overheating"),
        new FailureModeInfo(FailureMode.BlockDeckFailure, PartCategory.Block, 0.85, 40, FailureSeverity.Catastrophic,
            "Cracked cylinder block", "Cylinder pressure exceeded what the block deck and head bolts can hold"),
        new FailureModeInfo(FailureMode.CrankshaftOverspeed, PartCategory.Crankshaft, 0.95, 60, FailureSeverity.Catastrophic,
            "Broken crankshaft", "Crankshaft torsional-vibration fatigue from excessive engine speed"),
        new FailureModeInfo(FailureMode.CrankshaftTorsion, PartCategory.Crankshaft, 0.85, 60, FailureSeverity.Catastrophic,
            "Broken crankshaft", "Crankshaft torsional overload from excessive torque"),
        new FailureModeInfo(FailureMode.FlywheelBurst, PartCategory.Flywheel, 0.95, 10, FailureSeverity.Catastrophic,
            "Flywheel burst", "Flywheel exceeded its burst speed"),
        new FailureModeInfo(FailureMode.RodBearingFatigue, PartCategory.RodBearings, 0.80, 60, FailureSeverity.Catastrophic,
            "Rod bearing failure", "Rod bearing overlay fatigued under excessive load"),
        new FailureModeInfo(FailureMode.MainBearingFatigue, PartCategory.MainBearings, 0.80, 60, FailureSeverity.Catastrophic,
            "Main bearing failure", "Main bearing overlay fatigued under excessive load"),
        new FailureModeInfo(FailureMode.OilStarvation, PartCategory.RodBearings, 0.0, 30, FailureSeverity.Catastrophic,
            "Spun rod bearing", "Oil starvation: the bearings wore through without an oil film"),
        new FailureModeInfo(FailureMode.ValvePistonContact, PartCategory.CylinderHead, 0.936, 3, FailureSeverity.Catastrophic,
            "Bent valves", "Floating valves were struck by the pistons (over-rev beyond valve float)", InstantRatio: 1.05),
        new FailureModeInfo(FailureMode.CylinderHeadWarp, PartCategory.CylinderHead, 0.0, 60, FailureSeverity.Degraded,
            "Warped cylinder head", "Severe overheating warped the cylinder head"),
        new FailureModeInfo(FailureMode.TurboOverspeed, PartCategory.Turbocharger, 0.97, 40, FailureSeverity.Degraded,
            "Turbocharger failure", "Compressor wheel over-sped and failed"),
        new FailureModeInfo(FailureMode.TurbineOverTemperature, PartCategory.Turbocharger, 0.95, 120, FailureSeverity.Degraded,
            "Turbocharger failure", "Turbine inlet temperature exceeded the housing and wheel rating"),
        new FailureModeInfo(FailureMode.ClutchBurnout, PartCategory.Clutch, 0.0, 60, FailureSeverity.Degraded,
            "Burnt-out clutch", "The clutch facings wore through from slipping (torque above the clutch's capacity, or heat)"),
        new FailureModeInfo(FailureMode.BrakePadsWornOut, PartCategory.Brakes, 0.0, 60, FailureSeverity.Degraded,
            "Brake pads worn out", "The pads wore down to their backing plates"),
        new FailureModeInfo(FailureMode.TyresWornOut, PartCategory.Tires, 0.0, 60, FailureSeverity.Degraded,
            "Tyres worn out", "The tread wore down to the cords"),
    }.ToDictionary(i => i.Mode);

    public static FailureModeInfo Of(FailureMode mode) => All[mode];

    /// <summary>Fatigue accumulation rate (fraction of life per second) at stress ratio <paramref name="r"/>.</summary>
    public double FatigueRate(double r)
    {
        if (r <= Endurance) return 0.0;
        double k = 1.0 / SecondsToFailureAtRating;
        if (r <= 1.0)
        {
            double x = (r - Endurance) / (1.0 - Endurance);
            return k * x * x * x;
        }
        double over = 1.0 + 50.0 * (r - 1.0);
        return k * over * over;
    }
}
