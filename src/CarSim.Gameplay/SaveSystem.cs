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
/// Older versions are migrated on load (<see cref="Migrate"/>).
/// </summary>
public static class SaveSystem
{
    /// <summary>
    /// 1: first format. 2: tunes carry a speed-density VE table and displacement (version-1 tunes take them
    /// from the engine's stock tune on load). 3: parts carry a damage ledger (<c>exposure</c>; older saves
    /// start with an empty one). 4: tunes carry an injector dead time and a fuel density (older tunes take the
    /// installed injectors' dead time and their fuel's density: the ECU used to meter with the true values).
    /// </summary>
    public const int CurrentVersion = 4;

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

        /// <summary>Damage ledger per failure mode (version 3+).</summary>
        public Dictionary<string, ExposureSave>? Exposure { get; set; }

        public FailureSave? Failure { get; set; }

        /// <summary>Setup settings that differ from the part's defaults (spec field → value).</summary>
        public Dictionary<string, double>? Settings { get; set; }
    }

    public sealed class ExposureSave
    {
        public double PeakRatio { get; set; }
        public double Seconds { get; set; }
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
        Exposure = p.Damage.Exposure.Count == 0 ? null : p.Damage.Exposure.ToDictionary(kv => FailureModeNames.ToSnakeCase(kv.Key),
            kv => new ExposureSave { PeakRatio = kv.Value.PeakRatio, Seconds = kv.Value.Seconds }),
        Failure = p.Damage.Failure is { } f
            ? new FailureSave { Mode = FailureModeNames.ToSnakeCase(f.Mode), Time = f.Time, Collateral = f.Collateral, Note = f.Note }
            : null,
        Settings = p.Settings.Count == 0 ? null : p.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
    };

    /// <summary>Restores a garage. Throws <see cref="InvalidDataException"/> listing every problem if the save does not match the content.</summary>
    public static Garage Deserialize(string json, ContentDatabase content)
    {
        SaveFile? file;
        try { file = JsonSerializer.Deserialize<SaveFile>(json, Options); }
        catch (JsonException ex) { throw new InvalidDataException($"Save file is not valid JSON: {ex.Message}", ex); }
        if (file == null) throw new InvalidDataException("Save file is empty.");
        if (file.Version > CurrentVersion) throw new InvalidDataException($"Save version {file.Version} is newer than this game (v{CurrentVersion}).");

        Migrate(file, content);
        var problems = new List<string>();
        if (!content.Engines.TryGetValue(file.EngineId, out var engineDef)) problems.Add($"Unknown engine '{file.EngineId}'.");
        if (!content.Fuels.ContainsKey(file.FuelId)) problems.Add($"Unknown fuel '{file.FuelId}'.");
        if (file.VehicleId.Length > 0 && !content.Vehicles.ContainsKey(file.VehicleId)) problems.Add($"Unknown vehicle '{file.VehicleId}'.");
        foreach (var ps in file.Installed.Values.Concat(file.Inventory).Concat(file.Chassis.Values))
        {
            if (!content.Parts.ContainsKey(ps.PartId)) problems.Add($"Unknown part '{ps.PartId}' (instance {ps.InstanceId}); was a mod removed?");
            // A failure mode this game does not know would otherwise be dropped, silently healing the part.
            var modes = ps.Fatigue.Keys.Concat(ps.Exposure?.Keys ?? Enumerable.Empty<string>());
            if (ps.Failure != null) modes = modes.Append(ps.Failure.Mode);
            foreach (var mode in modes.Distinct())
                if (!FailureModeNames.TryParse(mode, out _)) problems.Add($"Unknown failure mode '{mode}' on part {ps.PartId} (instance {ps.InstanceId}).");
        }
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

    /// <summary>Brings an older save up to <see cref="CurrentVersion"/> in place.</summary>
    private static void Migrate(SaveFile file, ContentDatabase content)
    {
        if (file.Version < 2 && file.Tune is { } tune && (tune.VolumetricEfficiency == null || tune.DisplacementCc == null)
            && content.Engines.TryGetValue(file.EngineId, out var engine) && content.Tunes.TryGetValue(engine.StockTune, out var stock)
            && stock.VolumetricEfficiency != null)
        {
            // Version-1 ECUs had no fuel map (they fuelled from the true airflow). Give the tune the stock
            // map, resampled onto its own axes: the car runs as it did on a stock engine and drifts off its
            // target λ as far as its breathing differs from stock — exactly what the player now has to tune.
            var stockVe = new CarSim.Core.Common.Table2D(stock.RpmAxis, stock.LoadAxisKpa, stock.VolumetricEfficiency);
            double[][] ve = tune.LoadAxisKpa.Select(load => tune.RpmAxis.Select(rpm => stockVe.Evaluate(rpm, load)).ToArray()).ToArray();
            file.Tune = new TuneDocument
            {
                Id = tune.Id, Name = tune.Name, Description = tune.Description, RpmAxis = tune.RpmAxis, LoadAxisKpa = tune.LoadAxisKpa,
                TargetLambda = tune.TargetLambda, IgnitionAdvanceDeg = tune.IgnitionAdvanceDeg, BoostTargetKpa = tune.BoostTargetKpa,
                RevLimitRpm = tune.RevLimitRpm, IdleRpm = tune.IdleRpm, KnockControlEnabled = tune.KnockControlEnabled,
                InjectorFlowCcMin = tune.InjectorFlowCcMin, FuelStoichAfr = tune.FuelStoichAfr, Source = tune.Source,
                VolumetricEfficiency = tune.VolumetricEfficiency ?? ve,
                DisplacementCc = tune.DisplacementCc ?? stock.DisplacementCc,
                InjectorDeadTimeMs = tune.InjectorDeadTimeMs, FuelDensityKgL = tune.FuelDensityKgL,
            };
        }
        if (file.Version < 4 && file.Tune is { } oldTune)
        {
            // Before version 4 the ECU metered fuel with the real fuel density and ignored injector dead time, which
            // the injectors did not have. Calibrating the tune to the hardware in the save keeps the car running as it did.
            var injectors = file.Installed.Values.Select(ps => content.Parts.GetValueOrDefault(ps.PartId))
                .FirstOrDefault(d => d?.Category == PartCategory.Injectors);
            oldTune.InjectorDeadTimeMs ??= injectors?.Spec is CarSim.Core.Parts.Specs.InjectorSpec inj ? inj.DeadTimeMs : 0.0;
            oldTune.FuelDensityKgL ??= content.Fuels.TryGetValue(file.FuelId, out var fuel) ? fuel.DensityKgL : 0.745;
        }
        file.Version = CurrentVersion;
    }

    private static PartInstance Load(PartSave ps, ContentDatabase content)
    {
        // Failure-mode names were validated in Deserialize.
        var p = new PartInstance(ps.InstanceId, content.GetPart(ps.PartId), ps.Wear);
        static FailureMode Mode(string name) => FailureModeNames.TryParse(name, out var m) ? m : throw new InvalidDataException($"Unknown failure mode '{name}'.");
        var fatigue = ps.Fatigue.Select(kv => new KeyValuePair<FailureMode, double>(Mode(kv.Key), kv.Value));
        var exposure = (ps.Exposure ?? new()).Select(kv => new KeyValuePair<FailureMode, DamageExposure>(Mode(kv.Key), new DamageExposure(kv.Value.PeakRatio, kv.Value.Seconds)));
        PartFailure? failure = ps.Failure == null ? null : new PartFailure(Mode(ps.Failure.Mode), ps.Failure.Time, ps.Failure.Collateral, ps.Failure.Note);
        p.Damage.Restore(fatigue, failure, exposure);
        // Settings the part no longer offers (content changed) are dropped; the rest are re-clamped to today's ranges.
        foreach (var (field, value) in ps.Settings ?? new()) p.Adjust(field, value);
        return p;
    }
}
