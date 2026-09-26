using CarSim.Core.Content;
using CarSim.Core.Damage;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Fuels;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;
using CarSim.Core.Vehicles;

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
        bool engineInCar = true, VehicleAssembly? chassis = null)
    {
        Chassis = chassis;
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

    /// <summary>The car's chassis parts (null for an engine-only game).</summary>
    public VehicleAssembly? Chassis { get; }

    public VehicleDefinition? Vehicle => Chassis?.Definition;
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
        var chassis = sc.Vehicle.Length > 0 ? VehicleAssembly.CreateStock(content.GetVehicle(sc.Vehicle), content, factory) : null;
        if (chassis != null)
        {
            foreach (var (slot, wear) in sc.Wear)
                if (chassis.PartIn(slot) is { } p) p.Wear = wear;
        }
        var garage = new Garage(content, engine, EcuTune.FromDocument(content.GetTune(tuneId)), sc.Fuel, sc.Money, factory, true, chassis);
        foreach (var id in sc.Inventory) garage._inventory.Add(factory.Create(content.GetPart(id)));
        garage.Note($"New game: {sc.Name}.");
        return garage;
    }

    /// <summary>Adds an entry to the workshop log (e.g. a test-drive summary).</summary>
    public void LogEvent(string message) => Note(message);

    private void Note(string message)
    {
        _log.Add(message);
        Revision++;
    }

    // ---- Access -------------------------------------------------------------------------------

    public bool IsChassisSlot(string slotId) => Chassis?.Definition.FindSlot(slotId) != null && Engine.Definition.FindSlot(slotId) == null;

    public bool CanAccess(string slotId) =>
        IsChassisSlot(slotId) || !EngineInCar || (Engine.Definition.FindSlot(slotId)?.AccessibleInVehicle ?? false);

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
        if (IsChassisSlot(slotId))
        {
            var cr = Chassis!.Remove(slotId, out var removed);
            if (!cr.Ok) return ActionResult.Fail(cr.Message);
            _inventory.Add(removed!);
            Note(cr.Message);
            return ActionResult.Success(cr.Message);
        }
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
        if (IsChassisSlot(slotId))
        {
            var cr = Chassis!.Install(slotId, part);
            if (!cr.Ok) return ActionResult.Fail(cr.Message);
            _inventory.Remove(part);
            Note(cr.Message);
            return ActionResult.Success(cr.Message);
        }
        var slot = Engine.Definition.FindSlot(slotId);
        if (slot == null) return ActionResult.Fail($"Unknown slot '{slotId}'.");
        if (!CanAccess(slotId)) return ActionResult.Fail($"{slot.Label} cannot be reached with the engine in the car. Remove the engine first.");
        var r = Engine.Install(slotId, part);
        if (!r.Ok) return ActionResult.Fail(r.Message);
        _inventory.Remove(part);
        Note(r.Message);
        return ActionResult.Success(r.Message);
    }

    /// <summary>Whether <paramref name="part"/> could be fitted to <paramref name="slotId"/> right now (engine or chassis).</summary>
    public ActionResult CanInstall(string slotId, PartInstance part)
    {
        if (IsChassisSlot(slotId))
        {
            var cslot = Chassis!.Definition.FindSlot(slotId)!;
            if (part.Category != cslot.Category) return ActionResult.Fail($"{part.Definition.Name} is a {part.Category} part; {cslot.Label} takes {cslot.Category}.");
            if (Chassis.PartIn(slotId) != null) return ActionResult.Fail($"{cslot.Label} is occupied.");
            return ActionResult.Success("");
        }
        var slot = Engine.Definition.FindSlot(slotId);
        if (slot == null) return ActionResult.Fail($"Unknown slot '{slotId}'.");
        if (!CanAccess(slotId)) return ActionResult.Fail($"{slot.Label} cannot be reached with the engine in the car. Remove the engine first.");
        var r = Engine.CanInstall(slotId, part);
        return r.Ok ? ActionResult.Success("") : ActionResult.Fail(r.Message);
    }

    /// <summary>Slots of the engine or the car that accept <paramref name="part"/>'s category.</summary>
    public IEnumerable<EngineSlotDefinition> SlotsFor(PartInstance part) =>
        Engine.Definition.Slots.Concat(Chassis?.Definition.Slots ?? Array.Empty<EngineSlotDefinition>()).Where(s => s.Category == part.Category);

    /// <summary>Whether <paramref name="slotId"/> currently holds a part (engine or chassis).</summary>
    public bool IsFilled(string slotId) => IsChassisSlot(slotId) ? Chassis!.PartIn(slotId) != null : Engine.IsInstalled(slotId);

    public PartInstance? PartIn(string slotId) => IsChassisSlot(slotId) ? Chassis!.PartIn(slotId) : Engine.PartIn(slotId);

    // ---- Setup --------------------------------------------------------------------------------

    /// <summary>Changes a setup setting (an adjustable spec field) of the part installed in <paramref name="slotId"/>.</summary>
    public ActionResult Adjust(string slotId, string field, double value)
    {
        var part = PartIn(slotId);
        if (part == null) return ActionResult.Fail($"Nothing is installed in '{slotId}'.");
        if (!CanAccess(slotId)) return ActionResult.Fail($"{part.Definition.Name} cannot be reached with the engine in the car.");
        var adjustment = part.Definition.FindAdjustment(field);
        if (adjustment == null) return ActionResult.Fail($"{part.Definition.Name} has no '{field}' adjustment.");
        double before = part.SettingOf(field);
        part.Adjust(field, value);
        double after = part.SettingOf(field);
        if (Math.Abs(after - before) > 1e-9) Note($"{part.Definition.Name}: {adjustment.Label} {before:0.##} → {after:0.##}.");
        return ActionResult.Success($"{adjustment.Label}: {after:0.##}");
    }

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

    /// <summary>Builds a drivable car: the engine must be in the car and able to run, the chassis complete.</summary>
    public (VehicleSimulation? Sim, string Problem) CreateVehicleSimulation()
    {
        if (Chassis == null) return (null, "This game has no car.");
        if (!EngineInCar) return (null, "The engine is on the stand. Install it in the car first.");
        var missing = Chassis.MissingRequiredSlots();
        if (missing.Count > 0) return (null, $"The car is missing: {string.Join(", ", missing.Select(s => s.Label))}.");
        var brokenChassis = Chassis.Installed.Values.Where(p => p.IsFailed).Select(p => p.Definition.Name).ToList();
        if (brokenChassis.Count > 0) return (null, $"Replace the broken parts first: {string.Join(", ", brokenChassis)}.");
        var (engineSim, report) = CreateSimulation();
        if (engineSim == null) return (null, "The engine cannot run: " + string.Join(" ", report.Errors.Select(e => e.Message)));
        if (engineSim.Damage.Seized) return (null, "The engine is seized. Replace the broken parts first.");
        var config = new VehicleConfiguration(Chassis.Definition, Chassis, Engine, Content);
        var sim = new VehicleSimulation(config, engineSim);
        sim.StartIdling();
        return (sim, "");
    }

    /// <summary>Parts (installed or on the shelf) that have failed.</summary>
    public IEnumerable<PartInstance> FailedParts => Engine.AllParts.Concat(Chassis?.Installed.Values ?? Enumerable.Empty<PartInstance>()).Concat(_inventory).Where(p => p.IsFailed);

    internal void RestoreInventory(IEnumerable<PartInstance> parts) => _inventory.AddRange(parts);

    internal void RestoreLog(IEnumerable<string> log) => _log.AddRange(log);
}
