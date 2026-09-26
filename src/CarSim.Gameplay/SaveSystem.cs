using System.Text.Json;
using System.Text.Json.Serialization;
using CarSim.Core.Content;
using CarSim.Core.Damage;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Vehicles;

namespace CarSim.Gameplay;

/// <summary>
/// Versioned JSON save format. Definitions are referenced by id, never embedded, so content updates
/// flow into existing saves; runtime state (wear, fatigue, failures, tune) is stored in full.
/// </summary>
public static class SaveSystem
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public sealed class SaveFile
    {
        public int Version { get; set; } = CurrentVersion;
        public string EngineId { get; set; } = "";
        public bool EngineInCar { get; set; }
        public double Money { get; set; }
        public long NextInstanceId { get; set; }
        public string FuelId { get; set; } = "";
        public Dictionary<string, PartSave> Installed { get; set; } = new();
        public string VehicleId { get; set; } = "";
        public Dictionary<string, PartSave> Chassis { get; set; } = new();
        public List<PartSave> Inventory { get; set; } = new();
        public TuneDocument? Tune { get; set; }
        public List<string> Log { get; set; } = new();
    }

    public sealed class PartSave
    {
        public string InstanceId { get; set; } = "";
        public string PartId { get; set; } = "";
        public double Wear { get; set; }
        public Dictionary<string, double> Fatigue { get; set; } = new();
        public FailureSave? Failure { get; set; }
    }

    public sealed class FailureSave
    {
        public string Mode { get; set; } = "";
        public double Time { get; set; }
        public bool Collateral { get; set; }
        public string Note { get; set; } = "";
    }

    public static string Serialize(Garage g)
    {
        var file = new SaveFile
        {
            EngineId = g.Engine.Definition.Id,
            EngineInCar = g.EngineInCar,
            Money = g.Money,
            NextInstanceId = g.Factory.NextId,
            FuelId = g.FuelId,
            Installed = g.Engine.Installed.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => Save(kv.Value)),
            VehicleId = g.Vehicle?.Id ?? "",
            Chassis = g.Chassis?.Installed.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => Save(kv.Value)) ?? new(),
            Inventory = g.Inventory.Select(Save).ToList(),
            Tune = g.Tune.ToDocument(),
            Log = g.Log.TakeLast(200).ToList(),
        };
        return JsonSerializer.Serialize(file, Options);
    }

    private static PartSave Save(PartInstance p) => new()
    {
        InstanceId = p.InstanceId,
        PartId = p.Definition.Id,
        Wear = p.Wear,
        Fatigue = p.Damage.Fatigue.ToDictionary(kv => FailureModeNames.ToSnakeCase(kv.Key), kv => kv.Value),
        Failure = p.Damage.Failure is { } f
            ? new FailureSave { Mode = FailureModeNames.ToSnakeCase(f.Mode), Time = f.Time, Collateral = f.Collateral, Note = f.Note }
            : null,
    };

    /// <summary>Restores a garage. Throws <see cref="InvalidDataException"/> listing every problem if the save does not match the content.</summary>
    public static Garage Deserialize(string json, ContentDatabase content)
    {
        SaveFile? file;
        try { file = JsonSerializer.Deserialize<SaveFile>(json, Options); }
        catch (JsonException ex) { throw new InvalidDataException($"Save file is not valid JSON: {ex.Message}", ex); }
        if (file == null) throw new InvalidDataException("Save file is empty.");
        if (file.Version > CurrentVersion) throw new InvalidDataException($"Save version {file.Version} is newer than this game (v{CurrentVersion}).");

        var problems = new List<string>();
        if (!content.Engines.TryGetValue(file.EngineId, out var engineDef)) problems.Add($"Unknown engine '{file.EngineId}'.");
        if (!content.Fuels.ContainsKey(file.FuelId)) problems.Add($"Unknown fuel '{file.FuelId}'.");
        if (file.VehicleId.Length > 0 && !content.Vehicles.ContainsKey(file.VehicleId)) problems.Add($"Unknown vehicle '{file.VehicleId}'.");
        foreach (var ps in file.Installed.Values.Concat(file.Inventory).Concat(file.Chassis.Values))
            if (!content.Parts.ContainsKey(ps.PartId)) problems.Add($"Unknown part '{ps.PartId}' (instance {ps.InstanceId}); was a mod removed?");
        if (file.Tune == null) problems.Add("Save has no ECU tune.");
        else problems.AddRange(file.Tune.Validate().Select(p => $"Tune: {p}"));
        if (problems.Count > 0) throw new InvalidDataException("Cannot load save:\n" + string.Join("\n", problems));

        var engine = new EngineAssembly(engineDef!);
        foreach (var slot in engineDef!.AssemblyOrder())
        {
            if (!file.Installed.TryGetValue(slot.Id, out var ps)) continue;
            var r = engine.Install(slot.Id, Load(ps, content));
            if (!r.Ok) throw new InvalidDataException($"Cannot restore {slot.Label}: {r.Message}");
        }
        VehicleAssembly? chassis = null;
        if (file.VehicleId.Length > 0)
        {
            chassis = new VehicleAssembly(content.GetVehicle(file.VehicleId));
            foreach (var (slot, ps) in file.Chassis.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var r = chassis.Install(slot, Load(ps, content));
                if (!r.Ok) throw new InvalidDataException($"Cannot restore chassis slot {slot}: {r.Message}");
            }
        }
        var garage = new Garage(content, engine, EcuTune.FromDocument(file.Tune!), file.FuelId, file.Money,
            new PartInstanceFactory(file.NextInstanceId), file.EngineInCar, chassis);
        garage.RestoreInventory(file.Inventory.Select(ps => Load(ps, content)));
        garage.RestoreLog(file.Log);
        return garage;
    }

    private static PartInstance Load(PartSave ps, ContentDatabase content)
    {
        var p = new PartInstance(ps.InstanceId, content.GetPart(ps.PartId), ps.Wear);
        var fatigue = new List<KeyValuePair<FailureMode, double>>();
        foreach (var (name, v) in ps.Fatigue)
            if (FailureModeNames.TryParse(name, out var mode)) fatigue.Add(new(mode, v));
        PartFailure? failure = null;
        if (ps.Failure != null && FailureModeNames.TryParse(ps.Failure.Mode, out var fm))
            failure = new PartFailure(fm, ps.Failure.Time, ps.Failure.Collateral, ps.Failure.Note);
        p.Damage.Restore(fatigue, failure);
        return p;
    }
}
