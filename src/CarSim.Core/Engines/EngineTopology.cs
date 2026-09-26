using CarSim.Core.Parts;

namespace CarSim.Core.Engines;

/// <summary>
/// What the engine model can represent. Engine families are data, but the mean-value model has one air
/// path, one set of internals and one cooling circuit: it reads exactly one part (a set, for per-cylinder
/// parts — pistons, rods, injectors; a pair of heads on a V engine would be one set) from each required
/// category, and at most one from each optional one. Families outside that shape are rejected when the
/// content loads, and assemblies missing a required part fail validation, so building a configuration
/// never throws on valid data. Twin turbos, per-bank intake/exhaust, dry sumps and superchargers need
/// model work first (see ARCHITECTURE.md).
/// </summary>
public static class EngineTopology
{
    /// <summary>Categories the simulation reads every step; an engine cannot run without one of each.</summary>
    public static readonly IReadOnlyList<string> RequiredCategories = new[]
    {
        PartCategory.Block, PartCategory.MainBearings, PartCategory.Crankshaft, PartCategory.RodBearings,
        PartCategory.ConnectingRods, PartCategory.Pistons, PartCategory.HeadGasket, PartCategory.CylinderHead,
        PartCategory.ValveSprings, PartCategory.Camshafts, PartCategory.IntakeManifold, PartCategory.ThrottleBody,
        PartCategory.Injectors, PartCategory.FuelPump, PartCategory.ExhaustManifold, PartCategory.Exhaust,
        PartCategory.OilPump, PartCategory.OilPan, PartCategory.Radiator, PartCategory.Flywheel, PartCategory.Ecu,
    };

    /// <summary>
    /// Categories with an explicit fallback when empty: no turbocharger = naturally aspirated, no
    /// intercooler = the compressor outlet goes straight to the throttle.
    /// </summary>
    public static readonly IReadOnlyList<string> OptionalCategories = new[] { PartCategory.Turbocharger, PartCategory.Intercooler };

    /// <summary>Recognised <see cref="EngineDefinition.Layout"/> values (descriptive: the model has no balance or firing-order terms yet).</summary>
    public static readonly IReadOnlyList<string> Layouts = new[] { "inline", "v", "flat" };

    /// <summary>Problems that make an engine family impossible to simulate; empty when it fits the model.</summary>
    public static IReadOnlyList<string> CheckFamily(EngineDefinition engine)
    {
        var problems = new List<string>();
        foreach (string category in RequiredCategories)
        {
            var slots = engine.Slots.Where(s => s.Category == category).ToList();
            if (slots.Count == 0)
                problems.Add($"No slot for '{category}': the engine model needs one.");
            else if (slots.Any(s => !s.Required))
                problems.Add($"Slot '{slots[0].Id}' ({category}) cannot be optional: the engine model has no fallback without one.");
        }
        foreach (var group in engine.Slots.GroupBy(s => s.Category))
            if (group.Count() > 1 && (RequiredCategories.Contains(group.Key) || OptionalCategories.Contains(group.Key)))
                problems.Add($"Slots {string.Join(", ", group.Select(s => $"'{s.Id}'"))} all take '{group.Key}': the engine model uses one " +
                             "(sell per-cylinder or per-bank parts as a set in one slot).");
        if (!Layouts.Contains(engine.Layout))
            problems.Add($"Unknown layout '{engine.Layout}' (expected {string.Join(", ", Layouts)}).");
        return problems;
    }

    /// <summary>Required categories with no part installed in the assembly.</summary>
    public static IEnumerable<string> MissingCategories(EngineAssembly assembly) =>
        RequiredCategories.Where(c => assembly.FindByCategory(c) == null);
}
