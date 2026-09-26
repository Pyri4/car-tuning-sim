using CarSim.Core.Content;
using CarSim.Core.Damage;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Fuels;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;

namespace CarSim.Gameplay;

public sealed record ActionResult(bool Ok, string Message)
{
    public static ActionResult Success(string message) => new(true, message);
    public static ActionResult Fail(string message) => new(false, message);
}

/// <summary>
/// The player's workshop: the project car's engine, a shelf of loose parts, money, the ECU tune and
/// the fuel in the tank. All rules about what may be removed or installed come from the engine
/// family's slot graph; the garage adds only the "engine in the car" access restriction and money.
/// </summary>
public sealed class Garage
{
    /// <summary>Fraction of the new price paid for a used part in perfect condition.</summary>
    public const double ResaleFraction = 0.45;

    /// <summary>Fraction of the new price paid for a failed part (scrap value).</summary>
    public const double ScrapFraction = 0.05;

    private readonly List<PartInstance> _inventory = new();
    private readonly List<string> _log = new();

    public Garage(ContentDatabase content, EngineAssembly engine, EcuTune tune, string fuelId, double money, PartInstanceFactory factory,
        bool engineInCar = true)
    {
        Content = content;
        Engine = engine;
        Tune = tune;
        FuelId = fuelId;
        Money = money;
        Factory = factory;
        EngineInCar = engineInCar;
    }

    public ContentDatabase Content { get; }
    public EngineAssembly Engine { get; }
    public PartInstanceFactory Factory { get; }
    public EcuTune Tune { get; set; }
    public string FuelId { get; private set; }
    public FuelDefinition Fuel => Content.GetFuel(FuelId);
    public double Money { get; private set; }
    public bool EngineInCar { get; private set; }
    public IReadOnlyList<PartInstance> Inventory => _inventory;
    public IReadOnlyList<string> Log => _log;

    /// <summary>Incremented on every change so views know to refresh.</summary>
    public int Revision { get; private set; }

    public static Garage NewGame(ContentDatabase content, string scenarioId)
    {
        var sc = content.GetScenario(scenarioId);
        var factory = new PartInstanceFactory();
        var engineDef = content.GetEngine(sc.Engine);
        var engine = EngineAssembly.CreateStock(engineDef, content, factory);
        foreach (var (slot, wear) in sc.Wear)
            if (engine.PartIn(slot) is { } p) p.Wear = wear;
        foreach (var (slot, modes) in sc.Fatigue)
        {
            if (engine.PartIn(slot) is not { } p) continue;
            foreach (var (name, value) in modes)
                if (FailureModeNames.TryParse(name, out var mode)) p.Damage.Accumulate(mode, value);
        }
        string tuneId = sc.Tune.Length > 0 ? sc.Tune : engineDef.StockTune;
        var garage = new Garage(content, engine, EcuTune.FromDocument(content.GetTune(tuneId)), sc.Fuel, sc.Money, factory);
        foreach (var id in sc.Inventory) garage._inventory.Add(factory.Create(content.GetPart(id)));
        garage.Note($"New game: {sc.Name}.");
        return garage;
    }

    private void Note(string message)
    {
        _log.Add(message);
        Revision++;
    }

    // ---- Access -------------------------------------------------------------------------------

    public bool CanAccess(string slotId) => !EngineInCar || (Engine.Definition.FindSlot(slotId)?.AccessibleInVehicle ?? false);

    public ActionResult RemoveEngineFromCar()
    {
        if (!EngineInCar) return ActionResult.Fail("The engine is already on the stand.");
        EngineInCar = false;
        Note("Engine removed from the car and mounted on the engine stand.");
        return ActionResult.Success("Engine is on the stand: every part is now accessible.");
    }

    public ActionResult InstallEngineInCar()
    {
        if (EngineInCar) return ActionResult.Fail("The engine is already in the car.");
        var missing = Engine.MissingRequiredSlots();
        if (missing.Count > 0) return ActionResult.Fail($"Finish the engine first: {string.Join(", ", missing.Select(s => s.Label))} missing.");
        EngineInCar = true;
        Note("Engine installed in the car.");
        return ActionResult.Success("Engine installed in the car.");
    }

    // ---- Parts --------------------------------------------------------------------------------

    public ActionResult RemovePart(string slotId)
    {
        var slot = Engine.Definition.FindSlot(slotId);
        if (slot == null) return ActionResult.Fail($"Unknown slot '{slotId}'.");
        if (!CanAccess(slotId)) return ActionResult.Fail($"{slot.Label} cannot be reached with the engine in the car. Remove the engine first.");
        var r = Engine.Remove(slotId, out var part);
        if (!r.Ok) return ActionResult.Fail(r.Message);
        _inventory.Add(part!);
        Note(r.Message);
        return ActionResult.Success(r.Message);
    }

    public ActionResult InstallPart(PartInstance part, string slotId)
    {
        if (!_inventory.Contains(part)) return ActionResult.Fail($"{part.Definition.Name} is not on the shelf.");
        var slot = Engine.Definition.FindSlot(slotId);
        if (slot == null) return ActionResult.Fail($"Unknown slot '{slotId}'.");
        if (!CanAccess(slotId)) return ActionResult.Fail($"{slot.Label} cannot be reached with the engine in the car. Remove the engine first.");
        var r = Engine.Install(slotId, part);
        if (!r.Ok) return ActionResult.Fail(r.Message);
        _inventory.Remove(part);
        Note(r.Message);
        return ActionResult.Success(r.Message);
    }

    /// <summary>Slots of the engine that accept <paramref name="part"/>'s category.</summary>
    public IEnumerable<EngineSlotDefinition> SlotsFor(PartInstance part) =>
        Engine.Definition.Slots.Where(s => s.Category == part.Category);

    // ---- Economy ------------------------------------------------------------------------------

    public static double SellPrice(PartInstance part) =>
        Math.Round(part.IsFailed ? part.Definition.Price * ScrapFraction : part.Definition.Price * ResaleFraction * part.Condition);

    public ActionResult Buy(string partId)
    {
        if (!Content.Parts.TryGetValue(partId, out var def)) return ActionResult.Fail($"Unknown part '{partId}'.");
        if (def.Price > Money) return ActionResult.Fail($"{def.Name} costs {def.Price:N0}; you have {Money:N0}.");
        Money -= def.Price;
        _inventory.Add(Factory.Create(def));
        Note($"Bought {def.Name} for {def.Price:N0}.");
        return ActionResult.Success($"Bought {def.Name}.");
    }

    public ActionResult Sell(PartInstance part)
    {
        if (!_inventory.Remove(part)) return ActionResult.Fail($"{part.Definition.Name} is not on the shelf.");
        double price = SellPrice(part);
        Money += price;
        Note($"Sold {part.Definition.Name} for {price:N0}.");
        return ActionResult.Success($"Sold {part.Definition.Name} for {price:N0}.");
    }

    public ActionResult SetFuel(string fuelId)
    {
        if (!Content.Fuels.ContainsKey(fuelId)) return ActionResult.Fail($"Unknown fuel '{fuelId}'.");
        FuelId = fuelId;
        Note($"Tank filled with {Fuel.Name}.");
        return ActionResult.Success($"Now running {Fuel.Name}.");
    }

    public void TuneChanged() => Note("ECU calibration changed.");

    // ---- Running the engine -------------------------------------------------------------------

    public ValidationReport Validate() =>
        AssemblyValidator.Validate(Engine, new ValidationContext(Tune.RevLimitRpm, Tune.MaxBoostTargetKpa));

    /// <summary>Builds a simulation of the current engine (warm). Fails if the engine cannot run.</summary>
    public (EngineSimulation? Sim, ValidationReport Report) CreateSimulation()
    {
        var result = EngineConfiguration.Build(Engine, Fuel, new ValidationContext(Tune.RevLimitRpm, Tune.MaxBoostTargetKpa));
        if (!result.Success) return (null, result.Report);
        return (new EngineSimulation(result.Configuration!, Tune, EngineState.Warm()), result.Report);
    }

    /// <summary>Parts (installed or on the shelf) that have failed.</summary>
    public IEnumerable<PartInstance> FailedParts => Engine.AllParts.Concat(_inventory).Where(p => p.IsFailed);

    internal void RestoreInventory(IEnumerable<PartInstance> parts) => _inventory.AddRange(parts);

    internal void RestoreLog(IEnumerable<string> log) => _log.AddRange(log);
}
