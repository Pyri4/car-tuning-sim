using CarSim.Core.Engines;
using CarSim.Core.Parts;

namespace CarSim.Core.Vehicles;

/// <summary>
/// Whether an engine fits a car, from their parts' mounting interfaces — never from which engine family the car was
/// built with. A car names a stock engine (what it ships with); any engine whose parts provide what the car's parts
/// require (a gearbox's bellhousing pattern, say) can go in, which is how engine swaps work: a K20 bellhousing on the
/// Isar's gearbox needs an adapter or a matching gearbox. Engine-internal interfaces are the assembly validator's job.
/// Mounts, clearances and wiring are not modelled yet (ENGINE_AUTHORING_GUIDE.md, "Engine swaps").
/// </summary>
public static class VehicleCompatibility
{
    /// <summary>
    /// Requirements of the chassis parts that neither the engine's nor the chassis's other parts provide, as messages.
    /// </summary>
    public static IReadOnlyList<string> InterfaceProblems(IEnumerable<(string Slot, PartDefinition Part)> engineParts,
        IEnumerable<(string Slot, PartDefinition Part)> chassisParts, string engineName)
    {
        var engine = engineParts.ToList();
        var chassis = chassisParts.ToList();
        var problems = new List<string>();
        foreach (var (slot, part) in chassis)
            foreach (var req in part.Requires)
            {
                bool provided = engine.Any(e => e.Part.Provides.Contains(req, StringComparer.Ordinal))
                                || chassis.Any(c => c.Slot != slot && c.Part.Provides.Contains(req, StringComparer.Ordinal));
                if (!provided)
                    problems.Add($"{part.Name} ({slot}) needs '{req}', which {engineName} does not provide: fit a gearbox or adapter that matches the engine.");
            }
        return problems;
    }

    /// <summary>Interface problems between an assembled engine and an assembled car.</summary>
    public static IReadOnlyList<string> InterfaceProblems(EngineAssembly engine, VehicleAssembly chassis) =>
        InterfaceProblems(engine.Installed.Select(kv => (kv.Key, kv.Value.Definition)),
            chassis.Installed.Select(kv => (kv.Key, kv.Value.Definition)), engine.Definition.Name);
}
