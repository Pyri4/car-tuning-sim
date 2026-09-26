using CarSim.Core.Engines;

namespace CarSim.Core.Vehicles;

/// <summary>
/// A car model: body geometry and mass properties, which engine family it takes, and its chassis
/// slots (clutch, gearbox, differential, tyres, suspension, brakes). Mass and weight distribution are
/// for the factory build; part swaps adjust them by the parts' mass differences.
/// </summary>
public sealed class VehicleDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required string Engine { get; init; }

    /// <summary>"rwd" or "fwd".</summary>
    public string Drivetrain { get; init; } = "rwd";

    public required double CurbMassKg { get; init; }
    public required double FrontWeightFraction { get; init; }
    public required double WheelbaseM { get; init; }
    public required double TrackFrontM { get; init; }
    public required double TrackRearM { get; init; }
    public required double CgHeightM { get; init; }
    public required double YawInertiaKgM2 { get; init; }
    public double DragCoefficient { get; init; } = 0.33;
    public double FrontalAreaM2 { get; init; } = 1.9;
    public double MaxSteerDeg { get; init; } = 32;

    public required IReadOnlyList<EngineSlotDefinition> Slots { get; init; }
    public IReadOnlyDictionary<string, string> StockParts { get; init; } = new Dictionary<string, string>();

    public string Source { get; init; } = "";

    public EngineSlotDefinition? FindSlot(string id) => Slots.FirstOrDefault(s => s.Id == id);

    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        if (Drivetrain is not ("rwd" or "fwd")) p.Add("drivetrain must be rwd or fwd.");
        if (!(CurbMassKg >= 300 && CurbMassKg <= 5000)) p.Add("curb_mass_kg out of range.");
        if (!(FrontWeightFraction > 0.2 && FrontWeightFraction < 0.8)) p.Add("front_weight_fraction must be within (0.2, 0.8).");
        if (!(WheelbaseM >= 1.5 && WheelbaseM <= 4.5)) p.Add("wheelbase_m out of range.");
        if (!(TrackFrontM >= 1 && TrackFrontM <= 2.2) || !(TrackRearM >= 1 && TrackRearM <= 2.2)) p.Add("track widths out of range.");
        if (!(CgHeightM >= 0.2 && CgHeightM <= 1.2)) p.Add("cg_height_m out of range.");
        if (!(YawInertiaKgM2 > 100)) p.Add("yaw_inertia_kg_m2 out of range.");
        foreach (var required in new[] { "clutch", "gearbox", "differential", "tires_front", "tires_rear", "suspension", "brakes" })
            if (FindSlot(required) == null) p.Add($"missing slot '{required}'.");
        return p;
    }
}
