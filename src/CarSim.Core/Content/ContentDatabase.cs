using CarSim.Core.Engines;
using CarSim.Core.Fuels;
using CarSim.Core.Parts;

namespace CarSim.Core.Content;

/// <summary>Immutable set of loaded definitions, indexed by id.</summary>
public sealed class ContentDatabase
{
    public ContentDatabase(
        IReadOnlyDictionary<string, PartDefinition> parts,
        IReadOnlyDictionary<string, EngineDefinition> engines,
        IReadOnlyDictionary<string, FuelDefinition> fuels,
        IReadOnlyDictionary<string, TuneDocument> tunes,
        IReadOnlyDictionary<string, ScenarioDefinition>? scenarios = null,
        IReadOnlyDictionary<string, Vehicles.VehicleDefinition>? vehicles = null,
        IReadOnlyDictionary<string, SourceDefinition>? sources = null,
        IReadOnlyDictionary<string, EngineDefinition>? abstractEngines = null)
    {
        Sources = sources ?? new Dictionary<string, SourceDefinition>();
        AbstractEngines = abstractEngines ?? new Dictionary<string, EngineDefinition>();
        Vehicles = vehicles ?? new Dictionary<string, Vehicles.VehicleDefinition>();
        Parts = parts;
        Engines = engines;
        Fuels = fuels;
        Tunes = tunes;
        Scenarios = scenarios ?? new Dictionary<string, ScenarioDefinition>();
    }

    public IReadOnlyDictionary<string, Vehicles.VehicleDefinition> Vehicles { get; }

    public Vehicles.VehicleDefinition GetVehicle(string id) =>
        Vehicles.TryGetValue(id, out var v) ? v : throw new KeyNotFoundException($"Unknown vehicle id '{id}'.");

    /// <summary>Starting situations for a new game (which car, how worn, how much money).</summary>
    public IReadOnlyDictionary<string, ScenarioDefinition> Scenarios { get; }

    public IReadOnlyDictionary<string, PartDefinition> Parts { get; }

    /// <summary>Buildable engine definitions (families and their variants), in load order.</summary>
    public IReadOnlyDictionary<string, EngineDefinition> Engines { get; }

    /// <summary>
    /// Abstract family bases (<c>"abstract": true</c>): definitions that exist to be extended by variants. They are not
    /// buildable, not offered to the game and not in <see cref="Engines"/>.
    /// </summary>
    public IReadOnlyDictionary<string, EngineDefinition> AbstractEngines { get; }

    /// <summary>Cited sources that provenance and engine identities refer to (document kind <c>sources</c>).</summary>
    public IReadOnlyDictionary<string, SourceDefinition> Sources { get; }
    public IReadOnlyDictionary<string, FuelDefinition> Fuels { get; }

    /// <summary>ECU calibrations as authored (converted to live tunes by the ECU layer).</summary>
    public IReadOnlyDictionary<string, TuneDocument> Tunes { get; }

    public PartDefinition GetPart(string id) =>
        Parts.TryGetValue(id, out var p) ? p : throw new KeyNotFoundException($"Unknown part id '{id}'.");

    public EngineDefinition GetEngine(string id) =>
        Engines.TryGetValue(id, out var e) ? e : throw new KeyNotFoundException($"Unknown engine id '{id}'.");

    public FuelDefinition GetFuel(string id) =>
        Fuels.TryGetValue(id, out var f) ? f : throw new KeyNotFoundException($"Unknown fuel id '{id}'.");

    public TuneDocument GetTune(string id) =>
        Tunes.TryGetValue(id, out var t) ? t : throw new KeyNotFoundException($"Unknown tune id '{id}'.");

    public ScenarioDefinition GetScenario(string id) =>
        Scenarios.TryGetValue(id, out var sc) ? sc : throw new KeyNotFoundException($"Unknown scenario id '{id}'.");

    /// <summary>All parts of a category, ordered by price then id (deterministic).</summary>
    public IEnumerable<PartDefinition> PartsInCategory(string category) =>
        Parts.Values.Where(p => p.Category == category).OrderBy(p => p.Price).ThenBy(p => p.Id, StringComparer.Ordinal);
}

public sealed record ContentError(string Source, string ItemId, string Message)
{
    public override string ToString() =>
        string.IsNullOrEmpty(ItemId) ? $"{Source}: {Message}" : $"{Source} [{ItemId}]: {Message}";
}

public sealed class ContentLoadResult
{
    public ContentLoadResult(ContentDatabase database, IReadOnlyList<ContentError> errors,
        IReadOnlyList<string>? overrides = null, IReadOnlyList<string>? mods = null)
    {
        Database = database;
        Errors = errors;
        Overrides = overrides ?? Array.Empty<string>();
        Mods = mods ?? Array.Empty<string>();
    }

    public ContentDatabase Database { get; }
    public IReadOnlyList<ContentError> Errors { get; }

    /// <summary>Definitions a mod replaced ("mods/x/parts.json: overrides part 'clutch.oem'").</summary>
    public IReadOnlyList<string> Overrides { get; }

    /// <summary>Mods loaded after the base content, in load order.</summary>
    public IReadOnlyList<string> Mods { get; }
    public bool Success => Errors.Count == 0;

    /// <summary>Returns the database or throws with every collected error.</summary>
    public ContentDatabase GetOrThrow()
    {
        if (!Success)
            throw new InvalidDataException("Content failed to load:\n" + string.Join("\n", Errors));
        return Database;
    }
}
