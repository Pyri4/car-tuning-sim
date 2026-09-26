using CarSim.Core.Common;
using CarSim.Core.Content;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Vehicles;

/// <summary>
/// Derived chassis constants: mass and its distribution (adjusted for part swaps), axle geometry,
/// roll/pitch stiffness and damping from the suspension, and the fitted drivetrain and tyre specs.
/// </summary>
public sealed class VehicleConfiguration
{
    public VehicleConfiguration(VehicleDefinition def, VehicleAssembly chassis, EngineAssembly engine, ContentDatabase content)
    {
        Definition = def;
        Chassis = chassis;
        var missing = chassis.MissingRequiredSlots();
        if (missing.Count > 0) throw new InvalidOperationException("Chassis incomplete: " + string.Join(", ", missing.Select(s => s.Label)));

        Clutch = chassis.SpecIn<ClutchSpec>("clutch")!;
        Gearbox = chassis.SpecIn<GearboxSpec>("gearbox")!;
        Differential = chassis.SpecIn<DifferentialSpec>("differential")!;
        TiresFront = chassis.SpecIn<TireSpec>("tires_front")!;
        TiresRear = chassis.SpecIn<TireSpec>("tires_rear")!;
        Suspension = chassis.SpecIn<SuspensionSpec>("suspension")!;
        Brakes = chassis.SpecIn<BrakeSpec>("brakes")!;
        RearWheelDrive = def.Drivetrain == "rwd";

        // Mass: factory curb mass adjusted by how much heavier/lighter the fitted parts are.
        var engineDef = content.GetEngine(def.Engine);
        double stockEngineMass = engineDef.StockParts.Values.Sum(id => content.GetPart(id).MassKg);
        double engineDelta = engine.TotalMassKg - stockEngineMass;
        double chassisDelta = chassis.TotalMassKg - VehicleAssembly.StockPartsMassKg(def, content);
        Mass = def.CurbMassKg + engineDelta + chassisDelta;
        // The engine sits over the front axle; chassis parts are split evenly.
        double frontMass = def.CurbMassKg * def.FrontWeightFraction + engineDelta + 0.5 * chassisDelta;
        FrontWeightFraction = Math.Clamp(frontMass / Mass, 0.25, 0.75);
        Wheelbase = def.WheelbaseM;
        CgToFront = Wheelbase * (1.0 - FrontWeightFraction);
        CgToRear = Wheelbase * FrontWeightFraction;
        TrackFront = def.TrackFrontM;
        TrackRear = def.TrackRearM;
        CgHeight = Math.Max(0.15, def.CgHeightM + 0.8 * Units.MmToM(Suspension.RideHeightOffsetMm));
        YawInertia = def.YawInertiaKgM2 * Mass / def.CurbMassKg;
        DragArea = def.DragCoefficient * def.FrontalAreaM2;
        MaxSteer = Units.DegToRad(def.MaxSteerDeg);

        // Roll: springs (wheel rates) give k·t²/2 per axle; anti-roll bars add directly.
        double kf = Suspension.FrontSpringNMm * 1000, kr = Suspension.RearSpringNMm * 1000;
        RollStiffnessFront = kf * TrackFront * TrackFront / 2 + Suspension.FrontArbNmDeg * 180 / Math.PI;
        RollStiffnessRear = kr * TrackRear * TrackRear / 2 + Suspension.RearArbNmDeg * 180 / Math.PI;
        double rollDamping = Suspension.FrontDamperNsM * TrackFront * TrackFront / 2 + Suspension.RearDamperNsM * TrackRear * TrackRear / 2;
        double rollInertia = Mass * 0.4 * Math.Pow((TrackFront + TrackRear) / 2 / 1.5, 2);
        double kRoll = RollStiffnessFront + RollStiffnessRear;
        RollFrequency = Math.Sqrt(kRoll / rollInertia);
        RollDampingRatio = rollDamping / (2 * Math.Sqrt(kRoll * rollInertia));
        FrontRollShare = RollStiffnessFront / kRoll;

        double kPitch = 2 * kf * CgToFront * CgToFront + 2 * kr * CgToRear * CgToRear;
        double cPitch = 2 * Suspension.FrontDamperNsM * CgToFront * CgToFront + 2 * Suspension.RearDamperNsM * CgToRear * CgToRear;
        double pitchInertia = Mass * Wheelbase * Wheelbase / 4 * 0.9;
        PitchFrequency = Math.Sqrt(kPitch / pitchInertia);
        PitchDampingRatio = cPitch / (2 * Math.Sqrt(kPitch * pitchInertia));
    }

    public VehicleDefinition Definition { get; }
    public VehicleAssembly Chassis { get; }
    public ClutchSpec Clutch { get; }
    public GearboxSpec Gearbox { get; }
    public DifferentialSpec Differential { get; }
    public TireSpec TiresFront { get; }
    public TireSpec TiresRear { get; }
    public SuspensionSpec Suspension { get; }
    public BrakeSpec Brakes { get; }
    public bool RearWheelDrive { get; }

    public double Mass { get; }
    public double FrontWeightFraction { get; }
    public double Wheelbase { get; }
    public double CgToFront { get; }
    public double CgToRear { get; }
    public double TrackFront { get; }
    public double TrackRear { get; }
    public double CgHeight { get; }
    public double YawInertia { get; }
    public double DragArea { get; }
    public double MaxSteer { get; }

    public double RollStiffnessFront { get; }
    public double RollStiffnessRear { get; }
    public double FrontRollShare { get; }
    public double RollFrequency { get; }
    public double RollDampingRatio { get; }
    public double PitchFrequency { get; }
    public double PitchDampingRatio { get; }

    public TireSpec TireOf(int wheel) => wheel < 2 ? TiresFront : TiresRear;
    public bool IsDriven(int wheel) => RearWheelDrive ? wheel >= 2 : wheel < 2;

    /// <summary>Overall ratio from engine to driven wheels in <paramref name="gear"/> (0 = neutral).</summary>
    public double OverallRatio(int gear) => gear switch
    {
        0 => 0.0,
        < 0 => -Gearbox.ReverseRatio * Differential.FinalDriveRatio,
        _ => Gearbox.Ratios[Math.Min(gear, Gearbox.Ratios.Count) - 1] * Differential.FinalDriveRatio,
    };
}
