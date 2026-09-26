using System.Text.Json.Serialization;
using CarSim.Core.Common;

namespace CarSim.Core.Parts.Specs;

public sealed class ClutchSpec : PartSpec
{
    /// <summary>Torque the clutch can transmit fully engaged before it slips.</summary>
    public required double MaxTorqueNm { get; init; }

    /// <summary>Facing temperature where friction starts to fade and wear accelerates (organic ≈ 250 °C, cerametallic ≈ 400 °C).</summary>
    public double FadeStartC { get; init; } = 250;

    /// <summary>Slip energy the facings absorb over their life at normal temperatures.</summary>
    public double LifeMj { get; init; } = 150;

    /// <summary>Thermal mass of the friction surfaces (pressure plate, flywheel face, disc).</summary>
    public double HeatCapacityJPerK { get; init; } = 4000;

    protected override void Validate(SpecChecker check)
    {
        check.Range("max_torque_nm", MaxTorqueNm, 50, 3000);
        check.Range("fade_start_c", FadeStartC, 150, 800);
        check.Range("life_mj", LifeMj, 1, 10000);
        check.Range("heat_capacity_j_per_k", HeatCapacityJPerK, 500, 50000);
    }
}

public sealed class GearboxSpec : PartSpec
{
    /// <summary>Forward gear ratios, first gear first.</summary>
    public required IReadOnlyList<double> Ratios { get; init; }

    public double ReverseRatio { get; init; } = 3.2;
    public double Efficiency { get; init; } = 0.95;
    public required double MaxTorqueNm { get; init; }
    public double ShiftTimeS { get; init; } = 0.25;

    /// <summary>Inertia of the gearbox input side (clutch disc, input shaft), kg·m².</summary>
    public double InputInertiaKgM2 { get; init; } = 0.02;

    protected override void Validate(SpecChecker check)
    {
        check.That(Ratios != null && Ratios.Count >= 2 && Ratios.Count <= 10, "ratios needs 2–10 forward gears.");
        if (Ratios != null)
        {
            for (int i = 0; i < Ratios.Count; i++)
            {
                check.Range($"ratios[{i}]", Ratios[i], 0.3, 6.0);
                if (i > 0) check.That(Ratios[i] < Ratios[i - 1], "ratios must decrease from first to top gear.");
            }
        }
        check.Range("reverse_ratio", ReverseRatio, 1, 6);
        check.Range("efficiency", Efficiency, 0.7, 1.0);
        check.Positive("max_torque_nm", MaxTorqueNm);
        check.Range("shift_time_s", ShiftTimeS, 0.02, 2);
        check.Range("input_inertia_kg_m2", InputInertiaKgM2, 0.001, 0.5);
    }
}

public sealed class DifferentialSpec : PartSpec
{
    public required double FinalDriveRatio { get; init; }

    /// <summary>"open", "clutch_lsd" or "locked".</summary>
    public required string Type { get; init; }

    /// <summary>Clutch-LSD locking torque with no drive torque.</summary>
    public double PreloadNm { get; init; }

    /// <summary>Clutch-LSD locking torque as a fraction of input torque under drive (0–1).</summary>
    public double LockingAccel { get; init; }

    /// <summary>Clutch-LSD locking torque as a fraction of input torque on overrun (0–1).</summary>
    public double LockingDecel { get; init; }

    public double MaxTorqueNm { get; init; } = 2500;

    protected override void Validate(SpecChecker check)
    {
        check.Range("final_drive_ratio", FinalDriveRatio, 2, 7);
        check.That(Type is "open" or "clutch_lsd" or "locked", "type must be open, clutch_lsd or locked.");
        check.NonNegative("preload_nm", PreloadNm);
        check.Range("locking_accel", LockingAccel, 0, 1);
        check.Range("locking_decel", LockingDecel, 0, 1);
        check.Positive("max_torque_nm", MaxTorqueNm);
    }
}

/// <summary>A pair of tyres (one axle). Size sets the rolling radius; compound sets grip.</summary>
public sealed class TireSpec : PartSpec
{
    public required double WidthMm { get; init; }
    public required double AspectRatio { get; init; }
    public required double RimDiameterIn { get; init; }

    /// <summary>Peak friction coefficient at the reference load.</summary>
    public required double PeakFriction { get; init; }

    /// <summary>Loss of friction coefficient per doubling of load relative to the reference load (0–0.3).</summary>
    public double LoadSensitivity { get; init; } = 0.12;

    /// <summary>Slip ratio at peak longitudinal force.</summary>
    public double PeakSlipRatio { get; init; } = 0.10;

    /// <summary>Slip angle at peak lateral force.</summary>
    public double PeakSlipAngleDeg { get; init; } = 7.0;

    public double RollingResistance { get; init; } = 0.012;

    /// <summary>Rotational inertia of one wheel + tyre + brake disc.</summary>
    public double InertiaKgM2 { get; init; } = 1.2;

    public string Compound { get; init; } = "street";

    /// <summary>Friction work (sliding energy) the pair's tread absorbs before it is worn out.</summary>
    public double TreadLifeMj { get; init; } = 80;

    [JsonIgnore] public double Radius => Units.MmToM(RimDiameterIn * 25.4 / 2.0 + WidthMm * AspectRatio / 100.0);
    [JsonIgnore] public double PeakSlipAngle => Units.DegToRad(PeakSlipAngleDeg);

    protected override void Validate(SpecChecker check)
    {
        check.Range("width_mm", WidthMm, 100, 400);
        check.Range("aspect_ratio", AspectRatio, 20, 90);
        check.Range("rim_diameter_in", RimDiameterIn, 12, 24);
        check.Range("peak_friction", PeakFriction, 0.3, 2.5);
        check.Range("load_sensitivity", LoadSensitivity, 0, 0.4);
        check.Range("peak_slip_ratio", PeakSlipRatio, 0.02, 0.3);
        check.Range("peak_slip_angle_deg", PeakSlipAngleDeg, 1, 20);
        check.Range("rolling_resistance", RollingResistance, 0.003, 0.05);
        check.Range("inertia_kg_m2", InertiaKgM2, 0.2, 5);
        check.Range("tread_life_mj", TreadLifeMj, 1, 10000);
    }
}

/// <summary>Springs, dampers and anti-roll bars for both axles (wheel rates).</summary>
public sealed class SuspensionSpec : PartSpec
{
    public required double FrontSpringNMm { get; init; }
    public required double RearSpringNMm { get; init; }

    /// <summary>Damping per wheel, N·s/m.</summary>
    public required double FrontDamperNsM { get; init; }
    public required double RearDamperNsM { get; init; }

    public required double FrontArbNmDeg { get; init; }
    public required double RearArbNmDeg { get; init; }

    /// <summary>Change in ride height vs factory (negative = lowered); moves the centre of gravity.</summary>
    public double RideHeightOffsetMm { get; init; }

    protected override void Validate(SpecChecker check)
    {
        check.Range("front_spring_n_mm", FrontSpringNMm, 5, 400);
        check.Range("rear_spring_n_mm", RearSpringNMm, 5, 400);
        check.Range("front_damper_ns_m", FrontDamperNsM, 200, 20000);
        check.Range("rear_damper_ns_m", RearDamperNsM, 200, 20000);
        check.Range("front_arb_nm_deg", FrontArbNmDeg, 0, 5000);
        check.Range("rear_arb_nm_deg", RearArbNmDeg, 0, 5000);
        check.Range("ride_height_offset_mm", RideHeightOffsetMm, -100, 100);
    }
}

public sealed class BrakeSpec : PartSpec
{
    /// <summary>Maximum brake torque per front wheel at full pedal.</summary>
    public required double FrontMaxTorqueNm { get; init; }
    public required double RearMaxTorqueNm { get; init; }

    /// <summary>Disc/pad temperature where the pads start to fade (street pads ≈ 450 °C, race pads ≈ 600 °C).</summary>
    public double FadeStartC { get; init; } = 450;

    /// <summary>Braking energy one axle's pads absorb over their life at normal temperatures.</summary>
    public double PadLifeMj { get; init; } = 200;

    /// <summary>Thermal mass of the front discs (both), and of the rear discs.</summary>
    public double FrontHeatCapacityJPerK { get; init; } = 6000;
    public double RearHeatCapacityJPerK { get; init; } = 4000;

    protected override void Validate(SpecChecker check)
    {
        check.Range("front_max_torque_nm", FrontMaxTorqueNm, 200, 10000);
        check.Range("rear_max_torque_nm", RearMaxTorqueNm, 100, 10000);
        check.Range("fade_start_c", FadeStartC, 200, 1000);
        check.Range("pad_life_mj", PadLifeMj, 1, 10000);
        check.Range("front_heat_capacity_j_per_k", FrontHeatCapacityJPerK, 500, 50000);
        check.Range("rear_heat_capacity_j_per_k", RearHeatCapacityJPerK, 500, 50000);
    }
}
