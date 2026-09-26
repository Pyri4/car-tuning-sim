using System.Text.Json.Serialization;
using CarSim.Core.Common;

namespace CarSim.Core.Parts.Specs;

public sealed class HeadGasketSpec : PartSpec
{
    /// <summary>Gasket fire-ring bore. Must be at least the cylinder bore.</summary>
    public required double BoreMm { get; init; }

    public required double CompressedThicknessMm { get; init; }

    /// <summary>Peak cylinder pressure the gasket can seal.</summary>
    public required double MaxCylinderPressureBar { get; init; }

    public string Material { get; init; } = "mls";

    [JsonIgnore] public double Bore => Units.MmToM(BoreMm);
    [JsonIgnore] public double CompressedThickness => Units.MmToM(CompressedThicknessMm);
    [JsonIgnore] public double MaxCylinderPressure => Units.BarToPa(MaxCylinderPressureBar);

    protected override void Validate(SpecChecker check)
    {
        check.Range("bore_mm", BoreMm, 40, 160);
        check.Range("compressed_thickness_mm", CompressedThicknessMm, 0.2, 5.0);
        check.Range("max_cylinder_pressure_bar", MaxCylinderPressureBar, 30, 500);
    }
}

/// <summary>
/// Cylinder head. Port flow is authored like flow-bench data: pairs of [valve lift mm, CFM at
/// 28 inH2O] per cylinder (all intake valves of one cylinder together, likewise exhaust).
/// </summary>
public sealed class CylinderHeadSpec : PartSpec
{
    public required double ChamberVolumeCc { get; init; }
    public required IReadOnlyList<double[]> IntakePortFlowCfm { get; init; }
    public required IReadOnlyList<double[]> ExhaustPortFlowCfm { get; init; }

    /// <summary>Effective moving mass per valve (valve + retainer + follower + ⅓ spring).</summary>
    public required double ValveMovingMassG { get; init; }

    public int ValvesPerCylinder { get; init; } = 4;
    public string Material { get; init; } = "aluminium";

    [JsonIgnore] public double ChamberVolume => Units.CcToM3(ChamberVolumeCc);
    [JsonIgnore] public double ValveMovingMass => Units.GToKg(ValveMovingMassG);

    /// <summary>Intake flow (CFM @ 28") as a function of valve lift in mm.</summary>
    public Curve1D IntakeFlowCurve() => ToCurve(IntakePortFlowCfm);

    /// <summary>Exhaust flow (CFM @ 28") as a function of valve lift in mm.</summary>
    public Curve1D ExhaustFlowCurve() => ToCurve(ExhaustPortFlowCfm);

    private static Curve1D ToCurve(IReadOnlyList<double[]> pairs) =>
        new(pairs.Select(p => p[0]).ToArray(), pairs.Select(p => p[1]).ToArray());

    protected override void Validate(SpecChecker check)
    {
        check.Range("chamber_volume_cc", ChamberVolumeCc, 5, 300);
        check.CurvePairs("intake_port_flow_cfm", IntakePortFlowCfm);
        check.CurvePairs("exhaust_port_flow_cfm", ExhaustPortFlowCfm);
        check.Range("valve_moving_mass_g", ValveMovingMassG, 20, 500);
        check.Range("valves_per_cylinder", ValvesPerCylinder, 2, 5);
    }
}

/// <summary>
/// Camshaft pair (intake + exhaust). Durations are crank degrees at 1 mm valve lift; lift is peak
/// valve lift.
/// </summary>
public sealed class CamshaftSpec : PartSpec
{
    public required double IntakeDurationDeg { get; init; }
    public required double IntakeLiftMm { get; init; }
    public required double ExhaustDurationDeg { get; init; }
    public required double ExhaustLiftMm { get; init; }

    /// <summary>Lobe separation angle in cam degrees.</summary>
    public required double LobeSeparationDeg { get; init; }

    [JsonIgnore] public double IntakeLift => Units.MmToM(IntakeLiftMm);
    [JsonIgnore] public double ExhaustLift => Units.MmToM(ExhaustLiftMm);
    [JsonIgnore] public double MaxLiftMm => Math.Max(IntakeLiftMm, ExhaustLiftMm);

    /// <summary>Valve overlap (crank degrees at 1 mm lift), derived from durations and LSA.</summary>
    [JsonIgnore]
    public double OverlapDeg => Math.Max(0.0, (IntakeDurationDeg + ExhaustDurationDeg) / 2.0 - 2.0 * LobeSeparationDeg);

    protected override void Validate(SpecChecker check)
    {
        check.Range("intake_duration_deg", IntakeDurationDeg, 150, 340);
        check.Range("exhaust_duration_deg", ExhaustDurationDeg, 150, 340);
        check.Range("intake_lift_mm", IntakeLiftMm, 3, 20);
        check.Range("exhaust_lift_mm", ExhaustLiftMm, 3, 20);
        check.Range("lobe_separation_deg", LobeSeparationDeg, 95, 125);
    }
}

public sealed class ValveSpringSpec : PartSpec
{
    public required double SeatForceN { get; init; }

    /// <summary>Spring force at full lift. Determines the speed at which the valves float.</summary>
    public required double OpenForceN { get; init; }

    /// <summary>Maximum usable valve lift before coil bind / retainer-to-seal contact.</summary>
    public required double MaxLiftMm { get; init; }

    protected override void Validate(SpecChecker check)
    {
        check.Positive("seat_force_n", SeatForceN);
        check.Positive("open_force_n", OpenForceN);
        check.That(OpenForceN >= SeatForceN, "open_force_n must be >= seat_force_n.");
        check.Range("max_lift_mm", MaxLiftMm, 3, 25);
    }
}
