using System.Text.Json;
using CarSim.Core.Engines;
using CarSim.Core.Fuels;
using CarSim.Core.Parts;

namespace CarSim.Core.Content;

/// <summary>
/// Loads content JSON files. Each file is an object with any of the arrays
/// <c>parts</c>, <c>engines</c>, <c>fuels</c>, <c>tunes</c>. The loader never stops at the first
/// problem: it collects every error (with file and item id) so content authors can fix them in one pass.
/// </summary>
public static class ContentLoader
{
    /// <summary>Loads every *.json file under <paramref name="root"/> (recursive, ordinal path order).</summary>
    public static ContentLoadResult LoadDirectory(string root)
    {
        if (!Directory.Exists(root))
        {
            return new ContentLoadResult(Empty(), new[] { new ContentError(root, "", "Content directory does not exist.") });
        }
        return LoadFromStrings(Files(root, ""));
    }

    /// <summary>Loads content from (source name, JSON text) pairs.</summary>
    public static ContentLoadResult LoadFromStrings(IEnumerable<(string source, string json)> documents) =>
        LoadFromLayers(new[] { documents });

    /// <summary>
    /// Loads layers of documents in order (base game first, then mods). Later layers may add content
    /// and redefine ids from earlier layers (see <see cref="ContentLoadResult.Overrides"/>).
    /// </summary>
    public static ContentLoadResult LoadFromLayers(IEnumerable<IEnumerable<(string source, string json)>> layers)
    {
        var builder = new Builder();
        foreach (var layer in layers)
        {
            foreach (var (source, json) in layer) builder.AddDocument(source, json);
            builder.Layer++;
        }
        return builder.Build();
    }

    /// <summary>
    /// Loads the base content, then every mod: each subdirectory of <paramref name="modsDir"/> (in
    /// ordinal name order) is one layer. A missing mods directory simply means no mods.
    /// </summary>
    public static ContentLoadResult LoadWithMods(string baseDir, string? modsDir)
    {
        if (!Directory.Exists(baseDir))
            return new ContentLoadResult(Empty(), new[] { new ContentError(baseDir, "", "Content directory does not exist.") });
        var mods = modsDir != null && Directory.Exists(modsDir)
            ? Directory.GetDirectories(modsDir).OrderBy(d => d, StringComparer.Ordinal).ToList()
            : new List<string>();
        var layers = new List<IEnumerable<(string, string)>> { Files(baseDir, "") };
        layers.AddRange(mods.Select(m => Files(m, $"mods/{Path.GetFileName(m)}/")));
        var result = LoadFromLayers(layers);
        return new ContentLoadResult(result.Database, result.Errors, result.Overrides, mods.Select(Path.GetFileName).ToList()!);
    }

    private static IEnumerable<(string, string)> Files(string root, string prefix) =>
        Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (prefix + Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)));

    private static ContentDatabase Empty() => new(
        new Dictionary<string, PartDefinition>(), new Dictionary<string, EngineDefinition>(),
        new Dictionary<string, FuelDefinition>(), new Dictionary<string, TuneDocument>(), new Dictionary<string, ScenarioDefinition>());

    // ---- DTOs (file shape) ------------------------------------------------------------------

    private sealed class FileDto
    {
        public List<JsonElement>? Parts { get; set; }
        public List<JsonElement>? Engines { get; set; }
        public List<JsonElement>? Fuels { get; set; }
        public List<JsonElement>? Tunes { get; set; }
        public List<JsonElement>? Scenarios { get; set; }
        public List<JsonElement>? Vehicles { get; set; }
    }

    private sealed class VehicleDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Engine { get; set; }
        public string? Drivetrain { get; set; }
        public double CurbMassKg { get; set; }
        public double FrontWeightFraction { get; set; }
        public double WheelbaseM { get; set; }
        public double TrackFrontM { get; set; }
        public double TrackRearM { get; set; }
        public double CgHeightM { get; set; }
        public double YawInertiaKgM2 { get; set; }
        public double DragCoefficient { get; set; } = 0.33;
        public double FrontalAreaM2 { get; set; } = 1.9;
        public double MaxSteerDeg { get; set; } = 32;
        public double BumpTravelMm { get; set; } = 75;
        public List<SlotDto>? Slots { get; set; }
        public Dictionary<string, string>? StockParts { get; set; }
    }

    private sealed class PartDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Manufacturer { get; set; }
        public string? Description { get; set; }
        public double Price { get; set; }
        public double MassKg { get; set; }
        public List<string>? Provides { get; set; }
        public List<string>? Requires { get; set; }
        public List<string>? Tags { get; set; }
        public JsonElement Spec { get; set; }
        public Dictionary<string, AdjustmentDto>? Adjustable { get; set; }
    }

    private sealed class AdjustmentDto
    {
        public double Min { get; set; }
        public double Max { get; set; }
        public double Step { get; set; }
        public string? Label { get; set; }
    }

    private sealed class SlotDto
    {
        public string? Id { get; set; }
        public string? Category { get; set; }
        public string? DisplayName { get; set; }
        public bool Required { get; set; } = true;
        public List<string>? InstallAfter { get; set; }
        public bool AccessibleInVehicle { get; set; }
        public string? Axle { get; set; }
        public List<string>? Banks { get; set; }
    }

    private sealed class BankDto
    {
        public string? Id { get; set; }
        public List<int>? Cylinders { get; set; }
    }

    private sealed class EngineDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int Cylinders { get; set; }
        public string? Layout { get; set; }
        public List<BankDto>? Banks { get; set; }
        public double? BankAngleDeg { get; set; }
        public List<int>? FiringOrder { get; set; }
        public string? Description { get; set; }
        public List<SlotDto>? Slots { get; set; }
        public Dictionary<string, string>? StockParts { get; set; }
        public string? StockTune { get; set; }
    }

    // ---- Builder -----------------------------------------------------------------------------

    private sealed class Builder
    {
        private readonly List<ContentError> _errors = new();
        private readonly Dictionary<string, PartDefinition> _parts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EngineDefinition> _engines = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FuelDefinition> _fuels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TuneDocument> _tunes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScenarioDefinition> _scenarios = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Vehicles.VehicleDefinition> _vehicles = new(StringComparer.Ordinal);

        private readonly Dictionary<string, int> _claims = new(StringComparer.Ordinal);
        private readonly List<string> _overrides = new();

        /// <summary>Layer of the documents being added: 0 = base game, then one per mod in load order.</summary>
        public int Layer { get; set; }

        private void Error(string source, string id, string message) => _errors.Add(new ContentError(source, id, message));

        /// <summary>
        /// Registers an id. A later layer (a mod) may redefine an id from an earlier one: that replaces
        /// the definition and is reported as an override. Within one layer, a repeated id is an error.
        /// </summary>
        private bool Claim(string kind, string id, string source)
        {
            string key = kind + ":" + id;
            if (!_claims.TryGetValue(key, out int layer) || layer < Layer)
            {
                if (_claims.ContainsKey(key)) _overrides.Add($"{source}: overrides {kind} '{id}'");
                _claims[key] = Layer;
                return true;
            }
            Error(source, id, $"Duplicate {kind} id.");
            return false;
        }

        public void AddDocument(string source, string json)
        {
            FileDto? file;
            try
            {
                file = JsonSerializer.Deserialize<FileDto>(json, ContentJson.Options);
            }
            catch (JsonException ex)
            {
                Error(source, "", $"Invalid JSON: {ex.Message}");
                return;
            }
            if (file == null) { Error(source, "", "Empty document."); return; }

            foreach (var e in file.Parts ?? new()) AddPart(source, e);
            foreach (var e in file.Engines ?? new()) AddEngine(source, e);
            foreach (var e in file.Fuels ?? new()) AddFuel(source, e);
            foreach (var e in file.Tunes ?? new()) AddTune(source, e);
            foreach (var e in file.Scenarios ?? new()) AddScenario(source, e);
            foreach (var e in file.Vehicles ?? new()) AddVehicle(source, e);
        }

        private void AddVehicle(string source, JsonElement element)
        {
            string peekId = PeekId(element);
            VehicleDto? d;
            try { d = element.Deserialize<VehicleDto>(ContentJson.Options); }
            catch (JsonException ex) { Error(source, peekId, ex.Message); return; }
            if (d == null || string.IsNullOrWhiteSpace(d.Id)) { Error(source, peekId, "Vehicle is missing 'id'."); return; }
            var slots = new List<EngineSlotDefinition>();
            foreach (var s in d.Slots ?? new())
            {
                if (string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Category)) { Error(source, d.Id, "Every slot needs 'id' and 'category'."); continue; }
                if (!PartSpecRegistry.IsKnownCategory(s.Category)) Error(source, d.Id, $"Slot '{s.Id}' has unknown category '{s.Category}'.");
                slots.Add(new EngineSlotDefinition { Id = s.Id, Category = s.Category, DisplayName = s.DisplayName ?? "", Required = s.Required, AccessibleInVehicle = true, Axle = s.Axle ?? "" });
            }
            var v = new Vehicles.VehicleDefinition
            {
                Id = d.Id, Name = d.Name ?? d.Id, Description = d.Description ?? "", Engine = d.Engine ?? "", Drivetrain = d.Drivetrain ?? "rwd",
                CurbMassKg = d.CurbMassKg, FrontWeightFraction = d.FrontWeightFraction, WheelbaseM = d.WheelbaseM, TrackFrontM = d.TrackFrontM,
                TrackRearM = d.TrackRearM, CgHeightM = d.CgHeightM, YawInertiaKgM2 = d.YawInertiaKgM2, DragCoefficient = d.DragCoefficient,
                FrontalAreaM2 = d.FrontalAreaM2, MaxSteerDeg = d.MaxSteerDeg, BumpTravelMm = d.BumpTravelMm, Slots = slots,
                StockParts = d.StockParts ?? new Dictionary<string, string>(), Source = source,
            };
            foreach (var p in v.Validate()) Error(source, v.Id, p);
            if (!Claim("vehicle", v.Id, source)) return;
            _vehicles[v.Id] = v;
        }

        private void AddScenario(string source, JsonElement element)
        {
            string peekId = PeekId(element);
            ScenarioDefinition? sc;
            try { sc = element.Deserialize<ScenarioDefinition>(ContentJson.Options); }
            catch (JsonException ex) { Error(source, peekId, ex.Message); return; }
            if (sc == null) { Error(source, peekId, "Scenario entry is null."); return; }
            if (!Claim("scenario", sc.Id, source)) return;
            _scenarios[sc.Id] = new ScenarioDefinition
            {
                Id = sc.Id, Name = sc.Name, Description = sc.Description, Engine = sc.Engine, Vehicle = sc.Vehicle, Money = sc.Money, Fuel = sc.Fuel,
                Tune = sc.Tune, Wear = sc.Wear, Fatigue = sc.Fatigue, Inventory = sc.Inventory, Source = source,
            };
        }

        private static string PeekId(JsonElement e) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? "" : "";

        private void AddPart(string source, JsonElement element)
        {
            string peekId = PeekId(element);
            PartDto? dto;
            try { dto = element.Deserialize<PartDto>(ContentJson.Options); }
            catch (JsonException ex) { Error(source, peekId, ex.Message); return; }
            if (dto == null) { Error(source, peekId, "Part entry is null."); return; }

            if (string.IsNullOrWhiteSpace(dto.Id)) { Error(source, "", "Part is missing 'id'."); return; }
            if (string.IsNullOrWhiteSpace(dto.Name)) Error(source, dto.Id, "Part is missing 'name'.");
            if (string.IsNullOrWhiteSpace(dto.Category)) { Error(source, dto.Id, "Part is missing 'category'."); return; }
            if (!PartSpecRegistry.TryGetSpecType(dto.Category, out var specType))
            {
                Error(source, dto.Id, $"Unknown category '{dto.Category}'. Known: {string.Join(", ", PartSpecRegistry.Categories)}.");
                return;
            }
            if (dto.Spec.ValueKind != JsonValueKind.Object) { Error(source, dto.Id, "Part is missing an object 'spec'."); return; }
            if (dto.Price < 0) Error(source, dto.Id, "price must be >= 0.");
            if (dto.MassKg < 0) Error(source, dto.Id, "mass_kg must be >= 0.");

            PartSpec? spec;
            try { spec = (PartSpec?)dto.Spec.Deserialize(specType, ContentJson.Options); }
            catch (JsonException ex) { Error(source, dto.Id, $"spec: {ex.Message}"); return; }
            if (spec == null) { Error(source, dto.Id, "spec is null."); return; }
            foreach (var problem in spec.Validate()) Error(source, dto.Id, $"spec: {problem}");
            var adjustments = ParseAdjustments(source, dto.Id, spec, dto.Adjustable);

            if (!Claim("part", dto.Id, source)) return;
            _parts[dto.Id] = new PartDefinition
            {
                Id = dto.Id,
                Name = dto.Name ?? dto.Id,
                Category = dto.Category,
                Manufacturer = dto.Manufacturer ?? "",
                Description = dto.Description ?? "",
                Price = dto.Price,
                MassKg = dto.MassKg,
                Provides = dto.Provides?.ToArray() ?? Array.Empty<string>(),
                Requires = dto.Requires?.ToArray() ?? Array.Empty<string>(),
                Tags = dto.Tags?.ToArray() ?? Array.Empty<string>(),
                Source = source,
                Spec = spec,
                Adjustments = adjustments,
            };
        }

        private List<PartAdjustment> ParseAdjustments(string source, string id, PartSpec spec, Dictionary<string, AdjustmentDto>? dtos)
        {
            var list = new List<PartAdjustment>();
            foreach (var (field, a) in dtos ?? new())
            {
                double? authored = SpecAdjuster.Read(spec, field);
                if (authored is not double def) { Error(source, id, $"adjustable: '{field}' is not a numeric spec field of this part."); continue; }
                if (!(a.Max > a.Min)) { Error(source, id, $"adjustable '{field}': max must be greater than min."); continue; }
                if (!(a.Step > 0) || a.Step > a.Max - a.Min) { Error(source, id, $"adjustable '{field}': step must be > 0 and within the range."); continue; }
                if (def < a.Min || def > a.Max) { Error(source, id, $"adjustable '{field}': the part's value {def} is outside [{a.Min}, {a.Max}]."); continue; }
                bool valid = true;
                foreach (double v in new[] { a.Min, a.Max })
                {
                    foreach (var problem in SpecAdjuster.With(spec, new Dictionary<string, double> { [field] = v }).Validate())
                    {
                        Error(source, id, $"adjustable '{field}' at {v}: {problem}");
                        valid = false;
                    }
                }
                if (valid) list.Add(new PartAdjustment(field, a.Label ?? field.Replace('_', ' '), a.Min, a.Max, a.Step, def));
            }
            return list;
        }

        private void AddEngine(string source, JsonElement element)
        {
            string peekId = PeekId(element);
            EngineDto? dto;
            try { dto = element.Deserialize<EngineDto>(ContentJson.Options); }
            catch (JsonException ex) { Error(source, peekId, ex.Message); return; }
            if (dto == null || string.IsNullOrWhiteSpace(dto.Id)) { Error(source, peekId, "Engine is missing 'id'."); return; }
            if (dto.Cylinders <= 0) Error(source, dto.Id, "cylinders must be > 0.");
            if (dto.Slots == null || dto.Slots.Count == 0) { Error(source, dto.Id, "Engine has no slots."); return; }

            var slots = new List<EngineSlotDefinition>();
            foreach (var s in dto.Slots)
            {
                if (string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Category))
                {
                    Error(source, dto.Id, "Every slot needs 'id' and 'category'.");
                    continue;
                }
                if (!PartSpecRegistry.IsKnownCategory(s.Category))
                    Error(source, dto.Id, $"Slot '{s.Id}' has unknown category '{s.Category}'.");
                if (!string.IsNullOrEmpty(s.Axle)) Error(source, dto.Id, $"Slot '{s.Id}': 'axle' is only meaningful on vehicle slots.");
                slots.Add(new EngineSlotDefinition
                {
                    Id = s.Id,
                    Category = s.Category,
                    DisplayName = s.DisplayName ?? "",
                    Required = s.Required,
                    InstallAfter = s.InstallAfter?.ToArray() ?? Array.Empty<string>(),
                    AccessibleInVehicle = s.AccessibleInVehicle,
                    Banks = s.Banks?.ToArray() ?? Array.Empty<string>(),
                });
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in slots)
                if (!ids.Add(s.Id)) Error(source, dto.Id, $"Duplicate slot id '{s.Id}'.");
            foreach (var s in slots)
                foreach (var dep in s.InstallAfter)
                    if (!ids.Contains(dep)) Error(source, dto.Id, $"Slot '{s.Id}' install_after references unknown slot '{dep}'.");

            var banks = dto.Banks?.Select(b => new EngineBankDefinition { Id = b.Id ?? "", Cylinders = b.Cylinders?.ToArray() ?? Array.Empty<int>() }).ToArray();
            var engine = new EngineDefinition
            {
                Id = dto.Id,
                Name = dto.Name ?? dto.Id,
                Cylinders = dto.Cylinders,
                Layout = dto.Layout ?? "inline",
                Banks = banks is { Length: > 0 } ? banks : null!,
                BankAngleDeg = dto.BankAngleDeg,
                FiringOrder = dto.FiringOrder?.ToArray() ?? Array.Empty<int>(),
                Description = dto.Description ?? "",
                Slots = slots,
                StockParts = dto.StockParts ?? new Dictionary<string, string>(),
                StockTune = dto.StockTune ?? "",
                Source = source,
            };
            try { engine.AssemblyOrder(); }
            catch (InvalidOperationException ex) { Error(source, dto.Id, ex.Message); }
            foreach (var problem in EngineTopology.CheckFamily(engine)) Error(source, dto.Id, problem);

            if (!Claim("engine", dto.Id, source)) return;
            _engines[dto.Id] = engine;
        }

        private void AddFuel(string source, JsonElement element)
        {
            string peekId = PeekId(element);
            FuelDefinition? fuel;
            try { fuel = element.Deserialize<FuelDefinition>(ContentJson.Options); }
            catch (JsonException ex) { Error(source, peekId, ex.Message); return; }
            if (fuel == null) { Error(source, peekId, "Fuel entry is null."); return; }
            foreach (var p in fuel.Validate()) Error(source, fuel.Id, p);
            if (!Claim("fuel", fuel.Id, source)) return;
            _fuels[fuel.Id] = fuel;
        }

        private void AddTune(string source, JsonElement element)
        {
            string peekId = PeekId(element);
            TuneDocument? tune;
            try { tune = element.Deserialize<TuneDocument>(ContentJson.Options); }
            catch (JsonException ex) { Error(source, peekId, ex.Message); return; }
            if (tune == null) { Error(source, peekId, "Tune entry is null."); return; }
            foreach (var p in tune.Validate()) Error(source, tune.Id, p);
            if (!Claim("tune", tune.Id, source)) return;
            _tunes[tune.Id] = tune with { Source = source };
        }

        public ContentLoadResult Build()
        {
            // Cross-reference checks that need everything loaded.
            foreach (var engine in _engines.Values)
            {
                foreach (var (slotId, partId) in engine.StockParts)
                {
                    var slot = engine.FindSlot(slotId);
                    if (slot == null) { Error(engine.Source, engine.Id, $"stock_parts references unknown slot '{slotId}'."); continue; }
                    if (!_parts.TryGetValue(partId, out var part)) { Error(engine.Source, engine.Id, $"stock_parts['{slotId}'] references unknown part '{partId}'."); continue; }
                    if (part.Category != slot.Category)
                        Error(engine.Source, engine.Id, $"stock_parts['{slotId}'] is a '{part.Category}' but the slot takes '{slot.Category}'.");
                }
                if (!string.IsNullOrEmpty(engine.StockTune) && !_tunes.ContainsKey(engine.StockTune))
                    Error(engine.Source, engine.Id, $"stock_tune references unknown tune '{engine.StockTune}'.");
            }
            foreach (var sc in _scenarios.Values)
            {
                if (!_engines.TryGetValue(sc.Engine, out var engine)) { Error(sc.Source, sc.Id, $"Unknown engine '{sc.Engine}'."); continue; }
                Vehicles.VehicleDefinition? vehicle = null;
                if (sc.Vehicle.Length > 0)
                {
                    if (!_vehicles.TryGetValue(sc.Vehicle, out vehicle)) Error(sc.Source, sc.Id, $"Unknown vehicle '{sc.Vehicle}'.");
                    else if (vehicle.Engine != sc.Engine) Error(sc.Source, sc.Id, $"Vehicle '{sc.Vehicle}' takes engine '{vehicle.Engine}', not '{sc.Engine}'.");
                }
                bool KnownSlot(string slot) => engine.FindSlot(slot) != null || vehicle?.FindSlot(slot) != null;
                if (!_fuels.ContainsKey(sc.Fuel)) Error(sc.Source, sc.Id, $"Unknown fuel '{sc.Fuel}'.");
                if (sc.Tune.Length > 0 && !_tunes.ContainsKey(sc.Tune)) Error(sc.Source, sc.Id, $"Unknown tune '{sc.Tune}'.");
                foreach (var (slot, wear) in sc.Wear)
                {
                    if (!KnownSlot(slot)) Error(sc.Source, sc.Id, $"wear references unknown slot '{slot}'.");
                    if (!(wear >= 0 && wear <= 1)) Error(sc.Source, sc.Id, $"wear for '{slot}' must be within [0, 1].");
                }
                foreach (var (slot, modes) in sc.Fatigue)
                {
                    if (!KnownSlot(slot)) Error(sc.Source, sc.Id, $"fatigue references unknown slot '{slot}'.");
                    foreach (var (mode, v) in modes)
                    {
                        if (!Damage.FailureModeNames.TryParse(mode, out _)) Error(sc.Source, sc.Id, $"Unknown failure mode '{mode}'.");
                        if (!(v >= 0 && v < 1)) Error(sc.Source, sc.Id, $"fatigue '{mode}' for '{slot}' must be within [0, 1).");
                    }
                }
                foreach (var id in sc.Inventory)
                    if (!_parts.ContainsKey(id)) Error(sc.Source, sc.Id, $"inventory references unknown part '{id}'.");
            }
            foreach (var v in _vehicles.Values)
            {
                if (!_engines.ContainsKey(v.Engine)) Error(v.Source, v.Id, $"Unknown engine '{v.Engine}'.");
                foreach (var (slotId, partId) in v.StockParts)
                {
                    var slot = v.FindSlot(slotId);
                    if (slot == null) { Error(v.Source, v.Id, $"stock_parts references unknown slot '{slotId}'."); continue; }
                    if (!_parts.TryGetValue(partId, out var part)) { Error(v.Source, v.Id, $"stock_parts['{slotId}'] references unknown part '{partId}'."); continue; }
                    if (part.Category != slot.Category) Error(v.Source, v.Id, $"stock_parts['{slotId}'] is a '{part.Category}' but the slot takes '{slot.Category}'.");
                }
                foreach (var slot in v.Slots.Where(s => s.Required))
                    if (!v.StockParts.ContainsKey(slot.Id)) Error(v.Source, v.Id, $"No stock part for required slot '{slot.Id}'.");
            }
            var db = new ContentDatabase(_parts, _engines, _fuels, _tunes, _scenarios, _vehicles);
            return new ContentLoadResult(db, _errors, _overrides);
        }
    }
}
