using CarSim.Core.Content;
using CarSim.Core.Engines;
using CarSim.Core.Parts;

namespace CarSim.Core.Vehicles;

/// <summary>The chassis parts fitted to a car (slot → part instance). Chassis slots have no install order.</summary>
public sealed class VehicleAssembly
{
    private readonly Dictionary<string, PartInstance> _installed = new(StringComparer.Ordinal);

    public VehicleAssembly(VehicleDefinition definition) => Definition = definition;

    public VehicleDefinition Definition { get; }
    public IReadOnlyDictionary<string, PartInstance> Installed => _installed;
    public int Revision { get; private set; }

    public PartInstance? PartIn(string slotId) => _installed.GetValueOrDefault(slotId);
    public T? SpecIn<T>(string slotId) where T : PartSpec => PartIn(slotId)?.Definition.Spec as T;

    public AssemblyResult Install(string slotId, PartInstance part)
    {
        var slot = Definition.FindSlot(slotId);
        if (slot == null) return AssemblyResult.Fail($"This car has no '{slotId}' slot.");
        if (part.Category != slot.Category) return AssemblyResult.Fail($"{part.Definition.Name} is a {part.Category} part; {slot.Label} takes {slot.Category}.");
        if (_installed.ContainsKey(slotId)) return AssemblyResult.Fail($"{slot.Label} is occupied.", slotId);
        _installed[slotId] = part;
        Revision++;
        return AssemblyResult.Success($"Installed {part.Definition.Name} ({slot.Label}).");
    }

    public AssemblyResult Remove(string slotId, out PartInstance? removed)
    {
        removed = null;
        if (!_installed.Remove(slotId, out removed)) return AssemblyResult.Fail($"Nothing installed in '{slotId}'.");
        Revision++;
        return AssemblyResult.Success($"Removed {removed.Definition.Name}.");
    }

    public IReadOnlyList<EngineSlotDefinition> MissingRequiredSlots() =>
        Definition.Slots.Where(s => s.Required && !_installed.ContainsKey(s.Id)).ToList();

    public double TotalMassKg => _installed.Values.Sum(p => p.Definition.MassKg);

    public static VehicleAssembly CreateStock(VehicleDefinition definition, ContentDatabase content, PartInstanceFactory factory)
    {
        var a = new VehicleAssembly(definition);
        foreach (var (slot, partId) in definition.StockParts) a.Install(slot, factory.Create(content.GetPart(partId)));
        return a;
    }

    /// <summary>Mass of the factory chassis parts (reference for mass adjustments).</summary>
    public static double StockPartsMassKg(VehicleDefinition definition, ContentDatabase content) =>
        definition.StockParts.Values.Sum(id => content.GetPart(id).MassKg);
}
