using System.Text.Json;
using System.Text.Json.Nodes;
using CarSim.Core.Engines;
using CarSim.Core.Fuels;
using CarSim.Core.Parts;

namespace CarSim.Core.Content;

/// <summary>
/// Loads content JSON files. Each file is an object with any of the arrays
/// <c>parts</c>, <c>engines</c>, <c>fuels</c>, <c>tunes</c>, <c>scenarios</c>, <c>vehicles</c>, <c>sources</c>. Parts and
/// engines may <c>extends</c> another of their kind (a variant); variants are resolved after every layer has loaded, so a
/// mod that redefines a parent reaches its variants. The loader never stops at the first
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
        public List<JsonElement>? Sources { get; set; }
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
        public string? Extends { get; set; }
        public Dictionary<string, ValueProvenance>? Provenance { get; set; }
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
        public string? Extends { get; set; }
        public bool Abstract { get; set; }
        public EngineIdentity? Identity { get; set; }
        public Dictionary<string, ValueProvenance>? Provenance { get; set; }
    }

    /// <summary>Engine fields a <c>provenance</c> map may key (the architecture facts a source can state).</summary>
    public static readonly IReadOnlyList<string> EngineProvenanceFields =
        new[] { "bank_angle_deg", "banks", "cylinders", "firing_order", "layout" };

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
        private readonly Dictionary<string, SourceDefinition> _sources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EngineDefinition> _abstractEngines = new(StringComparer.Ordinal);

        // Raw documents, kept so variants can be resolved after every layer has loaded (a mod may redefine a parent).
        private readonly Dictionary<string, (string Source, JsonElement Element)> _partRaw = new(StringComparer.Ordinal);
        private readonly List<string> _pendingParts = new();
        private readonly Dictionary<string, (string Source, JsonElement Element)> _engineRaw = new(StringComparer.Ordinal);
        private readonly List<string> _engineOrder = new();

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

        /// <summary>Checks that need every layer: every source a provenance record or an identity cites exists.</summary>
        private void CheckSources()
        {
            foreach (var part in _parts.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
                foreach (var (field, record) in part.Provenance)
                    if (!string.IsNullOrWhiteSpace(record.Source) && !_sources.ContainsKey(record.Source))
                        Error(part.Source, part.Id, $"provenance '{field}': unknown source '{record.Source}'.");
            foreach (var engine in _engines.Values.Concat(_abstractEngines.Values).OrderBy(e => e.Id, StringComparer.Ordinal))
            {
                foreach (var (field, record) in engine.Provenance)
                    if (!string.IsNullOrWhiteSpace(record.Source) && !_sources.ContainsKey(record.Source))
                        Error(engine.Source, engine.Id, $"provenance '{field}': unknown source '{record.Source}'.");
                if (engine.Identity?.Reference is { Length: > 0 } reference && !_sources.ContainsKey(reference))
                    Error(engine.Source, engine.Id, $"identity: unknown reference source '{reference}'.");
            }
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
            foreach (var e in file.Sources ?? new()) AddSource(source, e);
        }

        private void AddSource(string source, JsonElement element)
        {
            string peekId = PeekId(element);
            SourceDefinition? s;
            try { s = element.Deserialize<SourceDefinition>(ContentJson.Options); }
            catch (JsonException ex) { Error(source, peekId, ex.Message); return; }
            if (s == null || string.IsNullOrWhiteSpace(s.Id)) { Error(source, peekId, "Source is missing 'id'."); return; }
            foreach (var p in s.Validate()) Error(source, s.Id, p);
            if (!Claim("source", s.Id, source)) return;
            _sources[s.Id] = s with { LoadedFrom = source };
        }

        private static string? PeekString(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

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
            // A variant (extends) is resolved once every layer has loaded; a plain part is built now, exactly as before.
            if (PeekString(element, "extends") != null)
            {
                string id = PeekId(element);
                if (string.IsNullOrWhiteSpace(id)) { Error(source, "", "Part is missing 'id'."); return; }
                if (!Claim("part", id, source)) return;
                _parts.Remove(id);
                _pendingParts.Remove(id);
                _pendingParts.Add(id);
                _partRaw[id] = (source, element.Clone());
                return;
            }
            var part = CreatePart(source, element, null, null);
            if (part == null || !Claim("part", part.Id, source)) return;
            _pendingParts.Remove(part.Id);
            _partRaw[part.Id] = (source, element.Clone());
            _parts[part.Id] = part;
        }

        /// <summary>
        /// Builds a part from its (merged) document. For a variant, <paramref name="parent"/> is the resolved parent and
        /// <paramref name="own"/> the variant's own document: provenance is inherited only for the fields the variant does
        /// not restate, so a changed value never carries its parent's source.
        /// </summary>
        private PartDefinition? CreatePart(string source, JsonElement element, PartDefinition? parent, JsonElement? own)
        {
            string peekId = PeekId(element);
            PartDto? dto;
            try { dto = element.Deserialize<PartDto>(ContentJson.Options); }
            catch (JsonException ex) { Error(source, peekId, ex.Message); return null; }
            if (dto == null) { Error(source, peekId, "Part entry is null."); return null; }

            if (string.IsNullOrWhiteSpace(dto.Id)) { Error(source, "", "Part is missing 'id'."); return null; }
            if (string.IsNullOrWhiteSpace(dto.Name)) Error(source, dto.Id, "Part is missing 'name'.");
            if (string.IsNullOrWhiteSpace(dto.Category)) { Error(source, dto.Id, "Part is missing 'category'."); return null; }
            if (!PartSpecRegistry.TryGetSpecType(dto.Category, out var specType))
            {
                Error(source, dto.Id, $"Unknown category '{dto.Category}'. Known: {string.Join(", ", PartSpecRegistry.Categories)}.");
                return null;
            }
            if (dto.Spec.ValueKind != JsonValueKind.Object) { Error(source, dto.Id, "Part is missing an object 'spec'."); return null; }
            if (dto.Price < 0) Error(source, dto.Id, "price must be >= 0.");
            if (dto.MassKg < 0) Error(source, dto.Id, "mass_kg must be >= 0.");

            PartSpec? spec;
            try { spec = (PartSpec?)dto.Spec.Deserialize(specType, ContentJson.Options); }
            catch (JsonException ex) { Error(source, dto.Id, $"spec: {ex.Message}"); return null; }
            if (spec == null) { Error(source, dto.Id, "spec is null."); return null; }
            foreach (var problem in spec.Validate()) Error(source, dto.Id, $"spec: {problem}");
            var adjustments = ParseAdjustments(source, dto.Id, spec, dto.Adjustable);

            // Provenance: keys must be this category's spec fields (or mass_kg); records must be well formed.
            var authored = dto.Spec.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var allowed = new HashSet<string>(ProvenanceFields.Of(specType), StringComparer.Ordinal) { "mass_kg", ProvenanceFields.Default };
            foreach (var (field, record) in dto.Provenance ?? new())
            {
                if (!allowed.Contains(field))
                    Error(source, dto.Id, $"provenance: '{field}' is not a spec field of a '{dto.Category}' part (or mass_kg).");
                if (record == null) { Error(source, dto.Id, $"provenance '{field}' is null."); continue; }
                foreach (var problem in record.Validate(field, _ => true)) Error(source, dto.Id, problem);
            }
            var validProvenance = (dto.Provenance ?? new()).Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);
            IReadOnlyDictionary<string, ValueProvenance> provenance;
            if (parent == null)
            {
                var fields = element.TryGetProperty("mass_kg", out _) ? authored.Append("mass_kg") : authored;
                provenance = ProvenanceFields.Expand(validProvenance, fields);
            }
            else
            {
                var ownElement = own!.Value;
                var ownFields = (ownElement.TryGetProperty("spec", out var ownSpec) && ownSpec.ValueKind == JsonValueKind.Object
                        ? ownSpec.EnumerateObject().Select(p => p.Name) : Enumerable.Empty<string>())
                    .Concat(ownElement.TryGetProperty("mass_kg", out _) ? new[] { "mass_kg" } : Array.Empty<string>())
                    .ToHashSet(StringComparer.Ordinal);
                var merged = new SortedDictionary<string, ValueProvenance>(StringComparer.Ordinal);
                foreach (var (field, record) in parent.Provenance.Where(kv => !ownFields.Contains(kv.Key))) merged[field] = record;
                foreach (var (field, record) in ProvenanceFields.Expand(validProvenance, ownFields)) merged[field] = record;
                provenance = merged;
            }

            return new PartDefinition
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
                Extends = parent?.Id,
                AuthoredSpecFields = authored,
                Provenance = provenance,
            };
        }

        /// <summary>
        /// Resolves every part variant: the parent's document (itself resolved) with the variant's fields over it — its
        /// <c>spec</c> merged field by field, every other field replaced. The variant keeps its parent's category and
        /// must have its own name.
        /// </summary>
        private void ResolvePartVariants()
        {
            var resolved = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);

            JsonObject? Resolve(string id)
            {
                if (resolved.TryGetValue(id, out var done)) return done;
                var (source, element) = _partRaw[id];
                string? parentId = PeekString(element, "extends");
                if (parentId == null) return resolved[id] = (JsonObject)JsonSerializer.SerializeToNode(element)!;
                if (!visiting.Add(id)) { Error(source, id, $"extends: cycle through part '{id}'."); return resolved[id] = null; }
                JsonObject? result = null;
                if (!_partRaw.ContainsKey(parentId)) Error(source, id, $"extends: unknown part '{parentId}'.");
                else if (Resolve(parentId) is not { } parentDoc || !_parts.TryGetValue(parentId, out var parent))
                    Error(source, id, $"extends: parent part '{parentId}' did not load.");
                else
                {
                    var own = (JsonObject)JsonSerializer.SerializeToNode(element)!;
                    string? ownCategory = own["category"]?.GetValue<string>();
                    if (ownCategory != null && ownCategory != parent.Category)
                        Error(source, id, $"extends: a variant keeps its parent's category ('{parent.Category}', not '{ownCategory}').");
                    else if (own["name"] == null)
                        Error(source, id, "extends: a variant needs its own 'name'.");
                    else
                    {
                        var merged = (JsonObject)parentDoc.DeepClone();
                        merged.Remove("provenance");
                        foreach (var (key, value) in own)
                        {
                            if (key == "extends") continue;
                            if (key == "spec" && value is JsonObject ownSpec && merged["spec"] is JsonObject parentSpec)
                                foreach (var (field, v) in ownSpec) parentSpec[field] = v?.DeepClone();
                            else merged[key] = value?.DeepClone();
                        }
                        var part = CreatePart(source, JsonSerializer.SerializeToElement(merged), parent, element);
                        if (part != null) { _parts[id] = part; result = merged; }
                    }
                }
                visiting.Remove(id);
                return resolved[id] = result;
            }

            foreach (var id in _pendingParts) Resolve(id);
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
            // A variant (extends) is resolved once every layer has loaded; any other engine is built now, as before.
            if (PeekString(element, "extends") != null)
            {
                string id = PeekId(element);
                if (string.IsNullOrWhiteSpace(id)) { Error(source, "", "Engine is missing 'id'."); return; }
                if (!Claim("engine", id, source)) return;
                _engines.Remove(id);
                _abstractEngines.Remove(id);
                _engineRaw[id] = (source, element.Clone());
                if (!_engineOrder.Contains(id)) _engineOrder.Add(id);
                return;
            }
            var engine = CreateEngine(source, element, null, null, out bool isAbstract);
            if (engine == null || !Claim("engine", engine.Id, source)) return;
            _engineRaw[engine.Id] = (source, element.Clone());
            if (!_engineOrder.Contains(engine.Id)) _engineOrder.Add(engine.Id);
            _engines.Remove(engine.Id);
            _abstractEngines.Remove(engine.Id);
            if (isAbstract) _abstractEngines[engine.Id] = engine;
            else _engines[engine.Id] = engine;
        }

        /// <summary>
        /// Builds an engine definition from its (merged) document. An abstract family base needs no complete build: it
        /// exists to be extended and is not offered to the game. For a variant, provenance is inherited only for the fields
        /// the variant does not restate.
        /// </summary>
        private EngineDefinition? CreateEngine(string source, JsonElement element, EngineDefinition? parent, JsonElement? own,
            out bool isAbstract)
        {
            isAbstract = false;
            string peekId = PeekId(element);
            EngineDto? dto;
            try { dto = element.Deserialize<EngineDto>(ContentJson.Options); }
            catch (JsonException ex) { Error(source, peekId, ex.Message); return null; }
            if (dto == null || string.IsNullOrWhiteSpace(dto.Id)) { Error(source, peekId, "Engine is missing 'id'."); return null; }
            isAbstract = dto.Abstract;
            if (dto.Cylinders <= 0) Error(source, dto.Id, "cylinders must be > 0.");
            if ((dto.Slots == null || dto.Slots.Count == 0) && !dto.Abstract) { Error(source, dto.Id, "Engine has no slots."); return null; }
            dto.Slots ??= new();

            foreach (var problem in dto.Identity?.Validate(_ => true) ?? Enumerable.Empty<string>()) Error(source, dto.Id, problem);
            foreach (var (field, record) in dto.Provenance ?? new())
            {
                if (field != ProvenanceFields.Default && !EngineProvenanceFields.Contains(field))
                    Error(source, dto.Id, $"provenance: '{field}' is not an engine architecture field ({string.Join(", ", EngineProvenanceFields)}).");
                if (record == null) { Error(source, dto.Id, $"provenance '{field}' is null."); continue; }
                foreach (var problem in record.Validate(field, _ => true)) Error(source, dto.Id, problem);
            }
            var validProvenance = (dto.Provenance ?? new()).Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);
            IEnumerable<string> AuthoredArchitecture(JsonElement e) =>
                EngineProvenanceFields.Where(f => e.TryGetProperty(f, out _));
            IReadOnlyDictionary<string, ValueProvenance> provenance;
            if (parent == null) provenance = ProvenanceFields.Expand(validProvenance, AuthoredArchitecture(element));
            else
            {
                var restated = AuthoredArchitecture(own!.Value).ToHashSet(StringComparer.Ordinal);
                var merged = new SortedDictionary<string, ValueProvenance>(StringComparer.Ordinal);
                foreach (var (field, record) in parent.Provenance.Where(kv => !restated.Contains(kv.Key))) merged[field] = record;
                foreach (var (field, record) in ProvenanceFields.Expand(validProvenance, restated)) merged[field] = record;
                provenance = merged;
            }

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
                Identity = dto.Identity,
                Extends = parent?.Id,
                Provenance = provenance,
            };
            try { engine.AssemblyOrder(); }
            catch (InvalidOperationException ex) { Error(source, dto.Id, ex.Message); }
            if (slots.Count > 0 || !dto.Abstract)
                foreach (var problem in EngineTopology.CheckFamily(engine)) Error(source, dto.Id, problem);
            return engine;
        }

        /// <summary>
        /// Resolves every engine variant: the parent's document (itself resolved) with the variant's fields over it —
        /// <c>stock_parts</c> and <c>identity</c> merged entry by entry, every other field replaced (slots and banks
        /// whole). <c>abstract</c> is never inherited: a variant of an abstract family is a buildable engine unless it
        /// says otherwise.
        /// </summary>
        private void ResolveEngineVariants()
        {
            var resolved = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);

            EngineDefinition? Definition(string id) => _engines.GetValueOrDefault(id) ?? _abstractEngines.GetValueOrDefault(id);

            JsonObject? Resolve(string id)
            {
                if (resolved.TryGetValue(id, out var done)) return done;
                var (source, element) = _engineRaw[id];
                string? parentId = PeekString(element, "extends");
                if (parentId == null) return resolved[id] = (JsonObject)JsonSerializer.SerializeToNode(element)!;
                if (!visiting.Add(id)) { Error(source, id, $"extends: cycle through engine '{id}'."); return resolved[id] = null; }
                JsonObject? result = null;
                if (!_engineRaw.ContainsKey(parentId)) Error(source, id, $"extends: unknown engine '{parentId}'.");
                else if (Resolve(parentId) is not { } parentDoc || Definition(parentId) is not { } parent)
                    Error(source, id, $"extends: parent engine '{parentId}' did not load.");
                else
                {
                    var own = (JsonObject)JsonSerializer.SerializeToNode(element)!;
                    var merged = (JsonObject)parentDoc.DeepClone();
                    merged.Remove("provenance");
                    merged.Remove("abstract");
                    foreach (var (key, value) in own)
                    {
                        if (key == "extends") continue;
                        if (key is "stock_parts" or "identity" && value is JsonObject ownMap && merged[key] is JsonObject parentMap)
                            foreach (var (k, v) in ownMap) parentMap[k] = v?.DeepClone();
                        else merged[key] = value?.DeepClone();
                    }
                    var engine = CreateEngine(source, JsonSerializer.SerializeToElement(merged), parent, element, out bool isAbstract);
                    if (engine != null)
                    {
                        if (isAbstract) _abstractEngines[id] = engine;
                        else _engines[id] = engine;
                        result = merged;
                    }
                }
                visiting.Remove(id);
                return resolved[id] = result;
            }

            foreach (var id in _engineOrder.Where(id => PeekString(_engineRaw[id].Element, "extends") != null)) Resolve(id);
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
            ResolvePartVariants();
            ResolveEngineVariants();
            CheckSources();

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
                    else
                    {
                        // Any engine whose stock parts provide what the car's stock parts require fits (an engine swap is
                        // an interface question, not an engine id).
                        var engineParts = engine.StockParts.Where(kv => _parts.ContainsKey(kv.Value)).Select(kv => (kv.Key, _parts[kv.Value]));
                        var carParts = vehicle.StockParts.Where(kv => _parts.ContainsKey(kv.Value)).Select(kv => (kv.Key, _parts[kv.Value]));
                        foreach (var problem in Vehicles.VehicleCompatibility.InterfaceProblems(engineParts, carParts, $"engine '{sc.Engine}'"))
                            Error(sc.Source, sc.Id, $"Vehicle '{sc.Vehicle}' cannot take engine '{sc.Engine}': {problem}");
                    }
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
            var engines = _engineOrder.Where(_engines.ContainsKey).ToDictionary(id => id, id => _engines[id], StringComparer.Ordinal);
            var db = new ContentDatabase(_parts, engines, _fuels, _tunes, _scenarios, _vehicles, _sources,
                _engineOrder.Where(_abstractEngines.ContainsKey).ToDictionary(id => id, id => _abstractEngines[id], StringComparer.Ordinal));
            return new ContentLoadResult(db, _errors, _overrides);
        }
    }
}
