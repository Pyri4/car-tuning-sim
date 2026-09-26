using CarSim.Core.Common;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Damage;

public enum FindingSeverity { Good, Minor, Major, Failed }

public sealed record InspectionFinding(FindingSeverity Severity, string Text);

/// <summary>
/// What a mechanic sees when inspecting a removed part: wear and early fatigue signs appear before the
/// part fails, so careful players can catch problems (e.g. detonation pitting) and fix the cause.
/// </summary>
public static class PartInspector
{
    public const double MinorThreshold = 0.25;
    public const double MajorThreshold = 0.6;

    public static IReadOnlyList<InspectionFinding> Inspect(PartInstance part)
    {
        var list = new List<InspectionFinding>();
        var d = part.Damage;
        if (d.Failure is { } failure)
        {
            var info = FailureModeInfo.Of(failure.Mode);
            string text = failure.Collateral && failure.Note.Length > 0 ? $"Destroyed: {failure.Note}." : $"FAILED: {info.Title.ToLowerInvariant()} — {info.Cause.ToLowerInvariant()}.";
            list.Add(new InspectionFinding(FindingSeverity.Failed, text));
            return list;
        }
        foreach (var (mode, fatigue) in d.Fatigue.OrderByDescending(kv => kv.Value))
        {
            if (fatigue < MinorThreshold) continue;
            bool major = fatigue >= MajorThreshold;
            list.Add(new InspectionFinding(major ? FindingSeverity.Major : FindingSeverity.Minor, FatigueSign(mode, major)));
        }
        if (part.Wear >= 0.1)
            list.Add(new InspectionFinding(part.Wear >= MajorThreshold ? FindingSeverity.Major : FindingSeverity.Minor, WearText(part)));
        if (list.Count == 0) list.Add(new InspectionFinding(FindingSeverity.Good, "Within specification."));
        return list;
    }

    public static string FatigueSign(FailureMode mode, bool major) => (mode, major) switch
    {
        (FailureMode.RodTensileOverload, false) => "Rod bolts show measurable stretch.",
        (FailureMode.RodTensileOverload, true) => "Rod bolts stretched beyond specification; big ends out of round.",
        (FailureMode.RodCompressiveOverload, false) => "Slight bow found on a rod straightness check.",
        (FailureMode.RodCompressiveOverload, true) => "Rods visibly bent.",
        (FailureMode.PistonPressureOverload, false) => "Hairline cracks starting at the ring lands.",
        (FailureMode.PistonPressureOverload, true) => "Ring lands cracked.",
        (FailureMode.PistonCrownOverheat, false) => "Piston crowns discoloured by heat.",
        (FailureMode.PistonCrownOverheat, true) => "Crown edges eroding and starting to melt.",
        (FailureMode.Detonation, false) => "Pepper-pitting around the crown edges (detonation).",
        (FailureMode.Detonation, true) => "Severe detonation erosion; ring lands cracking.",
        (FailureMode.HeadGasketBreach, false) => "Fire rings slightly distorted.",
        (FailureMode.HeadGasketBreach, true) => "Fire ring crushed; combustion traces across the gasket.",
        (FailureMode.BlockDeckFailure, false) => "Deck slightly out of flat around the bores.",
        (FailureMode.BlockDeckFailure, true) => "Cracks between the cylinders.",
        (FailureMode.CrankshaftOverspeed or FailureMode.CrankshaftTorsion, false) => "Fatigue marks at the journal fillets (crack test advised).",
        (FailureMode.CrankshaftOverspeed or FailureMode.CrankshaftTorsion, true) => "Crack found at a journal fillet.",
        (FailureMode.FlywheelBurst, false) => "Fine surface crazing.",
        (FailureMode.FlywheelBurst, true) => "Radial cracks.",
        (FailureMode.RodBearingFatigue or FailureMode.MainBearingFatigue, false) => "Overlay fatigue marks on the bearing shells.",
        (FailureMode.RodBearingFatigue or FailureMode.MainBearingFatigue, true) => "Bearing overlay flaking.",
        (FailureMode.ValvePistonContact, false) => "Valves slightly off their seats; guides worn.",
        (FailureMode.ValvePistonContact, true) => "Valves bent; piston contact marks.",
        (FailureMode.CylinderHeadWarp, false) => "Head slightly out of flat.",
        (FailureMode.CylinderHeadWarp, true) => "Head warped.",
        (FailureMode.TurboOverspeed or FailureMode.TurbineOverTemperature, false) => "Shaft play; compressor blade tips have rubbed.",
        (FailureMode.TurboOverspeed or FailureMode.TurbineOverTemperature, true) => "Compressor wheel damaged; turbine housing cracking.",
        _ => major ? "Serious fatigue damage." : "Early fatigue signs.",
    };

    public static string WearText(PartInstance part)
    {
        double w = part.Wear;
        string pct = $"{w * 100:F0} %";
        switch (part.Definition.Spec)
        {
            case BearingSpec b:
            {
                double clearance = b.ClearanceMm * (1.0 + w);
                return w < MajorThreshold
                    ? $"Bearing overlay wearing through; clearance {clearance:F3} mm (new {b.ClearanceMm:F3} mm)."
                    : $"Bearings heavily worn and scored; clearance {clearance:F3} mm (new {b.ClearanceMm:F3} mm).";
            }
            case PistonSpec:
                return $"Rings and skirts worn ({pct}); expect blow-by and reduced compression.";
            case ValveSpringSpec s:
                return $"Springs have sagged: about {s.OpenForceN * (1 - ValvetrainModel.SpringForceLossAtFullWear * w):F0} N at full lift (new {s.OpenForceN:F0} N).";
            case InjectorSpec:
                return $"Injectors partially clogged: flow down about {Simulation.FuelSystem.InjectorWearFlowLoss * w * 100:F0} %.";
            case FuelPumpSpec:
                return $"Pump output down about {Simulation.FuelSystem.PumpWearFlowLoss * w * 100:F0} %.";
            case CrankshaftSpec:
                return w < MajorThreshold ? "Journals lightly scored." : "Journals heavily scored: regrind or replace.";
            case BlockSpec:
                return w < MajorThreshold ? "Cylinder bores lightly scored." : "Cylinder bores badly scored: bore oversize or replace.";
            case ConnectingRodSpec:
                return "Big ends discoloured from overheating.";
            case ClutchSpec c:
                return $"Friction facings worn ({pct}): clamp capacity down to about {c.MaxTorqueNm * (1 - Vehicles.ChassisWearModel.ClutchWearCapacityLoss * w):F0} N·m (new {c.MaxTorqueNm:F0} N·m).";
            case TurbochargerSpec:
                return $"Turbo bearings worn ({pct}): shaft play lets the wheels drag, so it spools more slowly. Surge (lifting off at boost) wears them.";
            case BrakeSpec:
                return w < MajorThreshold ? $"Pads worn ({pct})." : $"Pads nearly down to the backing plates ({pct}).";
            case TireSpec:
                return $"Tread worn ({pct}): about {Vehicles.ChassisWearModel.TyreWearGripLoss * w * 100:F0} % less grip.";
            default:
                return $"Worn ({pct}).";
        }
    }
}

/// <summary>Workshop diagnostic tests on an assembled engine.</summary>
public static class EngineDiagnostics
{
    /// <summary>
    /// Cranking compression pressure (gauge, bar): the lowest bank's reading (what the mechanic writes down first).
    /// Healthy K20: ~13–14 bar. Worn rings, a blown gasket, bent valves or a holed piston all show up here.
    /// </summary>
    public static double CompressionTestBar(EngineAssembly a) =>
        CompressionTestByBank(a) is { Count: > 0 } banks ? banks.Min() : 0.0;

    /// <summary>
    /// Cranking compression pressure per bank (gauge, bar; bank declaration order). A failed gasket or head shows on
    /// its own bank's cylinders; the pistons, rods and rings are one set and show on every bank.
    /// </summary>
    public static IReadOnlyList<double> CompressionTestByBank(EngineAssembly a)
    {
        var g = EngineGeometry.TryCreate(a, out _);
        if (g == null) return Array.Empty<double>();
        // Cranking speed is slow, so the effective compression is lower than the static ratio suggests.
        double absolute = 1.0 * Math.Pow(g.CompressionRatio, 1.2) * 0.85;
        var result = new double[a.Definition.Banks.Count];
        for (int b = 0; b < result.Length; b++)
        {
            double factor = 1.0;
            var pistons = a.FindByCategory(PartCategory.Pistons);
            if (pistons != null) factor *= 1.0 - 0.3 * pistons.Wear;
            if (pistons?.IsFailed == true) factor *= 0.1;
            if (a.PartFor(PartCategory.HeadGasket, b)?.IsFailed == true) factor *= 0.35;
            var head = a.PartFor(PartCategory.CylinderHead, b);
            if (head?.Damage.Failure?.Mode == FailureMode.ValvePistonContact) factor *= 0.15;
            if (head?.Damage.Failure?.Mode == FailureMode.CylinderHeadWarp) factor *= 0.6;
            if (a.FindByCategory(PartCategory.ConnectingRods)?.IsFailed == true) factor = 0.0;
            result[b] = Math.Max(0.0, absolute * factor - 1.0);
        }
        return result;
    }
}
