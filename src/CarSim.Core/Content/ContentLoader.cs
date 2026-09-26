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
        var files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)));
        return LoadFromStrings(files);
    }

    /// <summary>Loads content from (source name, JSON text) pairs.</summary>
    public static ContentLoadResult LoadFromStrings(IEnumerable<(string source, string json)> documents)
    {
        var builder = new Builder();
        foreach (var (source, json) in documents) builder.AddDocument(source, json);
        return builder.Build();
    }

    private static ContentDatabase Empty() => new(
        new Dictionary<string, PartDefinition>(), new Dictionary<string, EngineDefinition>(),
        new Dictionary<string, FuelDefinition>(), new Dictionary<string, TuneDocument>());

    // ---- DTOs (file shape) ------------------------------------------------------------------

    private sealed class FileDto
    {
        public List<JsonElement>? Parts { get; set; }
        public List<JsonElement>? Engines { get; set; }
        public List<JsonElement>? Fuels { get; set; }
        public List<JsonElement>? Tunes { get; set; }
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
    }

    private sealed class SlotDto
    {
        public string? Id { get; set; }
        public string? Category { get; set; }
        public string? DisplayName { get; set; }
        public bool Required { get; set; } = true;
        public List<string>? InstallAfter { get; set; }
        public bool AccessibleInVehicle { get; set; }
    }

    private sealed class EngineDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int Cylinders { get; set; }
        public string? Layout { get; set; }
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

        private void Error(string source, string id, string message) => _errors.Add(new ContentError(source, id, message));

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

            if (_parts.ContainsKey(dto.Id)) { Error(source, dto.Id, "Duplicate part id."); return; }
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
            };
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
                slots.Add(new EngineSlotDefinition
                {
                    Id = s.Id,
                    Category = s.Category,
                    DisplayName = s.DisplayName ?? "",
                    Required = s.Required,
                    InstallAfter = s.InstallAfter?.ToArray() ?? Array.Empty<string>(),
                    AccessibleInVehicle = s.AccessibleInVehicle,
                });
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in slots)
                if (!ids.Add(s.Id)) Error(source, dto.Id, $"Duplicate slot id '{s.Id}'.");
            foreach (var s in slots)
                foreach (var dep in s.InstallAfter)
                    if (!ids.Contains(dep)) Error(source, dto.Id, $"Slot '{s.Id}' install_after references unknown slot '{dep}'.");

            var engine = new EngineDefinition
            {
                Id = dto.Id,
                Name = dto.Name ?? dto.Id,
                Cylinders = dto.Cylinders,
                Layout = dto.Layout ?? "inline",
                Description = dto.Description ?? "",
                Slots = slots,
                StockParts = dto.StockParts ?? new Dictionary<string, string>(),
                StockTune = dto.StockTune ?? "",
                Source = source,
            };
            try { engine.AssemblyOrder(); }
            catch (InvalidOperationException ex) { Error(source, dto.Id, ex.Message); }

            if (_engines.ContainsKey(dto.Id)) { Error(source, dto.Id, "Duplicate engine id."); return; }
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
            if (_fuels.ContainsKey(fuel.Id)) { Error(source, fuel.Id, "Duplicate fuel id."); return; }
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
            if (_tunes.ContainsKey(tune.Id)) { Error(source, tune.Id, "Duplicate tune id."); return; }
            _tunes[tune.Id] = new TuneDocument
            {
                Id = tune.Id, Name = tune.Name, Description = tune.Description,
                RpmAxis = tune.RpmAxis, LoadAxisKpa = tune.LoadAxisKpa,
                TargetLambda = tune.TargetLambda, IgnitionAdvanceDeg = tune.IgnitionAdvanceDeg,
                BoostTargetKpa = tune.BoostTargetKpa, RevLimitRpm = tune.RevLimitRpm,
                IdleRpm = tune.IdleRpm, KnockControlEnabled = tune.KnockControlEnabled, Source = source,
            };
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
            var db = new ContentDatabase(_parts, _engines, _fuels, _tunes);
            return new ContentLoadResult(db, _errors);
        }
    }
}
