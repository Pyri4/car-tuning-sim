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
        IReadOnlyDictionary<string, TuneDocument> tunes)
    {
        Parts = parts;
        Engines = engines;
        Fuels = fuels;
        Tunes = tunes;
    }

    public IReadOnlyDictionary<string, PartDefinition> Parts { get; }
    public IReadOnlyDictionary<string, EngineDefinition> Engines { get; }
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
    public ContentLoadResult(ContentDatabase database, IReadOnlyList<ContentError> errors)
    {
        Database = database;
        Errors = errors;
    }

    public ContentDatabase Database { get; }
    public IReadOnlyList<ContentError> Errors { get; }
    public bool Success => Errors.Count == 0;

    /// <summary>Returns the database or throws with every collected error.</summary>
    public ContentDatabase GetOrThrow()
    {
        if (!Success)
            throw new InvalidDataException("Content failed to load:\n" + string.Join("\n", Errors));
        return Database;
    }
}
