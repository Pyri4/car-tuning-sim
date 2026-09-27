using CarSim.Core.Engines;
using CarSim.Core.Parts;

namespace CarSim.Core.Vehicles;

/// <summary>
/// A car model: body geometry and mass properties, the engine family it ships with, and its chassis
/// slots (clutch, gearbox, differential, tyres, suspension, brakes). Mass and weight distribution are
/// for the factory build; part swaps adjust them by the parts' mass differences. Which engines fit is decided by
/// mounting interfaces (<see cref="VehicleCompatibility"/>), not by <see cref="Engine"/>.
/// </summary>
public sealed class VehicleDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    /// <summary>
    /// The engine family the car ships with (its stock engine). Other engines fit when their parts provide what the
    /// car's parts require (<see cref="VehicleCompatibility"/>): nothing in the simulation checks this id.
    /// </summary>
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

    /// <summary>Wheel travel from the factory ride height to the bump stops, mm (lowering takes it away).</summary>
    public double BumpTravelMm { get; init; } = 75;

    public required IReadOnlyList<EngineSlotDefinition> Slots { get; init; }
    public IReadOnlyDictionary<string, string> StockParts { get; init; } = new Dictionary<string, string>();

    public string Source { get; init; } = "";

    public EngineSlotDefinition? FindSlot(string id) => Slots.FirstOrDefault(s => s.Id == id);

    /// <summary>Chassis categories the vehicle model reads one of (tyres are one per axle).</summary>
    public static readonly IReadOnlyList<string> SingleCategories = new[]
    {
        PartCategory.Clutch, PartCategory.Gearbox, PartCategory.Differential, PartCategory.Suspension, PartCategory.Brakes,
    };

    public const string FrontAxle = "front";
    public const string RearAxle = "rear";

    /// <summary>
    /// The slot that plays a role in the vehicle model: the only slot of <paramref name="category"/>, or for
    /// per-axle parts the one on <paramref name="axle"/>. Roles come from the data (category, axle), not
    /// from slot ids, so a car may name its slots as it likes.
    /// </summary>
    public string SlotFor(string category, string axle = "") =>
        Slots.Single(s => s.Category == category && (axle.Length == 0 || s.Axle == axle)).Id;

    public string TireSlot(bool front) => SlotFor(PartCategory.Tires, front ? FrontAxle : RearAxle);

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
        if (!(BumpTravelMm >= 20 && BumpTravelMm <= 250)) p.Add("bump_travel_mm out of range (20–250).");
        void CheckRole(string what, List<EngineSlotDefinition> slots)
        {
            if (slots.Count == 0) p.Add($"needs a slot for {what}.");
            else if (slots.Count > 1) p.Add($"slots {string.Join(", ", slots.Select(s => $"'{s.Id}'"))} are all {what}: the vehicle model uses one.");
            else if (!slots[0].Required) p.Add($"slot '{slots[0].Id}' ({what}) cannot be optional.");
        }
        foreach (string category in SingleCategories)
            CheckRole($"'{category}'", Slots.Where(s => s.Category == category).ToList());
        foreach (string axle in new[] { FrontAxle, RearAxle })
            CheckRole($"'{PartCategory.Tires}' on the {axle} axle", Slots.Where(s => s.Category == PartCategory.Tires && s.Axle == axle).ToList());
        foreach (var slot in Slots)
            if (slot.Axle.Length > 0 && (slot.Axle is not (FrontAxle or RearAxle) || slot.Category != PartCategory.Tires))
                p.Add($"slot '{slot.Id}': axle '{slot.Axle}' is only valid as \"front\"/\"rear\" on tyre slots.");
            else if (slot.Category == PartCategory.Tires && slot.Axle.Length == 0)
                p.Add($"tyre slot '{slot.Id}' needs \"axle\": \"front\" or \"rear\".");
        return p;
    }
}
