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

    /// <summary>
    /// Engine slots only: the banks the part in this slot serves (bank ids); empty = every bank. A V8's left cylinder
    /// head serves the left bank; its single pushrod camshaft, common plenum or one turbocharger serves both.
    /// </summary>
    public IReadOnlyList<string> Banks { get; init; } = Array.Empty<string>();

    public string Label => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;
}

/// <summary>
/// A bank: a row of cylinders sharing a cylinder head (an inline engine has one, a V or flat engine two, a W more).
/// Cylinders are numbered 1..N across the whole engine, as in the firing order.
/// </summary>
public sealed class EngineBankDefinition
{
    public required string Id { get; init; }
    public required IReadOnlyList<int> Cylinders { get; init; }

    public override string ToString() => $"bank '{Id}'";
}

/// <summary>
/// An engine family: its architecture (cylinders, banks, layout, firing order), the slot graph any build of it uses
/// and the factory configuration. Individual builds are <see cref="EngineAssembly"/> instances. Nothing in the
/// simulation knows a family by id: everything it needs is here or in the parts (see ENGINE_AUTHORING_GUIDE.md).
/// </summary>
public sealed class EngineDefinition
{
    /// <summary>Id of the implicit bank of a family that declares none (one bank holding every cylinder).</summary>
    public const string SingleBankId = "main";

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int Cylinders { get; init; }

    /// <summary>"inline", "v" or "flat" (<see cref="EngineTopology.Layouts"/>); must agree with the bank count.</summary>
    public string Layout { get; init; } = "inline";

    private readonly IReadOnlyList<EngineBankDefinition>? _banks;
    private IReadOnlyList<EngineBankDefinition>? _implicitBanks;

    /// <summary>The banks; a family that declares none has one bank holding cylinders 1..N.</summary>
    public IReadOnlyList<EngineBankDefinition> Banks
    {
        get => _banks ?? (_implicitBanks ??= new[]
        {
            new EngineBankDefinition { Id = SingleBankId, Cylinders = Enumerable.Range(1, Math.Max(0, Cylinders)).ToArray() },
        });
        init => _banks = value;
    }

    /// <summary>Angle between the banks of a V (180° for a flat engine); null when unspecified or inline.</summary>
    public double? BankAngleDeg { get; init; }

    /// <summary>Cylinder numbers in firing order; empty when not authored (descriptive: see SIMULATION_SPEC.md).</summary>
    public IReadOnlyList<int> FiringOrder { get; init; } = Array.Empty<int>();

    public required IReadOnlyList<EngineSlotDefinition> Slots { get; init; }

    /// <summary>Factory part for each slot (slot id → part id).</summary>
    public IReadOnlyDictionary<string, string> StockParts { get; init; } = new Dictionary<string, string>();

    /// <summary>Factory ECU calibration id.</summary>
    public string StockTune { get; init; } = "";

    public string Description { get; init; } = "";
    public string Source { get; init; } = "";

    /// <summary>Who and what this engine is (metadata only; nothing in the simulation reads it). Null when not authored.</summary>
    public EngineIdentity? Identity { get; init; }

    /// <summary>The family definition this variant extends (<c>extends</c>), or null. Resolved at load; informational.</summary>
    public string? Extends { get; init; }

    /// <summary>
    /// Provenance of the authored architecture fields (<c>cylinders</c>, <c>bank_angle_deg</c>, <c>firing_order</c>, …),
    /// keyed by field name, expanded over the fields actually authored.
    /// </summary>
    public IReadOnlyDictionary<string, Content.ValueProvenance> Provenance { get; init; } =
        new Dictionary<string, Content.ValueProvenance>();

    private Dictionary<string, EngineSlotDefinition>? _byId;

    public EngineSlotDefinition? FindSlot(string slotId)
    {
        _byId ??= Slots.ToDictionary(s => s.Id, StringComparer.Ordinal);
        return _byId.GetValueOrDefault(slotId);
    }

    public EngineSlotDefinition GetSlot(string slotId) =>
        FindSlot(slotId) ?? throw new KeyNotFoundException($"Engine '{Id}' has no slot '{slotId}'.");

    /// <summary>Index of the bank with id <paramref name="bankId"/>, or −1.</summary>
    public int BankIndex(string bankId)
    {
        for (int i = 0; i < Banks.Count; i++)
            if (Banks[i].Id == bankId) return i;
        return -1;
    }

    /// <summary>Indices of the banks the part in <paramref name="slot"/> serves (every bank when the slot names none).</summary>
    public IReadOnlyList<int> BanksServedBy(EngineSlotDefinition slot) =>
        slot.Banks.Count == 0
            ? Enumerable.Range(0, Banks.Count).ToArray()
            : slot.Banks.Select(BankIndex).Where(i => i >= 0).ToArray();

    /// <summary>Whether <paramref name="slot"/> serves bank <paramref name="bank"/>.</summary>
    public bool Serves(EngineSlotDefinition slot, int bank) =>
        slot.Banks.Count == 0 || slot.Banks.Contains(Banks[bank].Id, StringComparer.Ordinal);

    /// <summary>Cylinders served by the part in <paramref name="slot"/>.</summary>
    public int CylindersServedBy(EngineSlotDefinition slot) => BanksServedBy(slot).Sum(b => Banks[b].Cylinders.Count);

    /// <summary>
    /// How a bank is named in messages: "bank 'left'" on an engine with several banks, nothing on a single-bank engine
    /// (where "the cylinder head" needs no qualifier).
    /// </summary>
    public string BankLabel(int bank) => Banks.Count > 1 ? $"bank '{Banks[bank].Id}'" : "";

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
