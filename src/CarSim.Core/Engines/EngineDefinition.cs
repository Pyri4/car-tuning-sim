namespace CarSim.Core.Engines;

/// <summary>
/// One mounting position in an engine family (e.g. "pistons", "turbocharger").
/// <see cref="InstallAfter"/> encodes assembly order: a slot can only be filled once every slot it
/// lists is filled, and a slot can only be emptied once no filled slot lists it.
/// </summary>
public sealed class EngineSlotDefinition
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public string DisplayName { get; init; } = "";
    public bool Required { get; init; } = true;
    public IReadOnlyList<string> InstallAfter { get; init; } = Array.Empty<string>();

    /// <summary>Whether the part can be serviced with the engine still in the car.</summary>
    public bool AccessibleInVehicle { get; init; }

    /// <summary>Vehicle slots only: "front" or "rear" for parts fitted per axle (tyres); empty otherwise.</summary>
    public string Axle { get; init; } = "";

    public string Label => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;
}

/// <summary>
/// An engine family: the slot graph that any build of this engine uses, plus the factory
/// configuration. Individual builds are <see cref="EngineAssembly"/> instances.
/// </summary>
public sealed class EngineDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int Cylinders { get; init; }
    public string Layout { get; init; } = "inline";
    public required IReadOnlyList<EngineSlotDefinition> Slots { get; init; }

    /// <summary>Factory part for each slot (slot id → part id).</summary>
    public IReadOnlyDictionary<string, string> StockParts { get; init; } = new Dictionary<string, string>();

    /// <summary>Factory ECU calibration id.</summary>
    public string StockTune { get; init; } = "";

    public string Description { get; init; } = "";
    public string Source { get; init; } = "";

    private Dictionary<string, EngineSlotDefinition>? _byId;

    public EngineSlotDefinition? FindSlot(string slotId)
    {
        _byId ??= Slots.ToDictionary(s => s.Id, StringComparer.Ordinal);
        return _byId.GetValueOrDefault(slotId);
    }

    public EngineSlotDefinition GetSlot(string slotId) =>
        FindSlot(slotId) ?? throw new KeyNotFoundException($"Engine '{Id}' has no slot '{slotId}'.");

    /// <summary>Slots whose part must be installed after <paramref name="slotId"/> (direct dependents).</summary>
    public IEnumerable<EngineSlotDefinition> DependentsOf(string slotId) =>
        Slots.Where(s => s.InstallAfter.Contains(slotId, StringComparer.Ordinal));

    /// <summary>Slots in a valid assembly order (topological, stable by declaration order).</summary>
    public IReadOnlyList<EngineSlotDefinition> AssemblyOrder()
    {
        var order = new List<EngineSlotDefinition>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var remaining = Slots.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(s => s.InstallAfter.All(placed.Contains));
            if (next == null) throw new InvalidOperationException($"Engine '{Id}' slot graph has a cycle.");
            order.Add(next);
            placed.Add(next.Id);
            remaining.Remove(next);
        }
        return order;
    }

    public override string ToString() => $"{Name} [{Id}]";
}
