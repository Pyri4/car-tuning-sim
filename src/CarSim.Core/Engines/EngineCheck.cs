using System.Globalization;
using System.Text;
using CarSim.Core.Common;
using CarSim.Core.Content;
using CarSim.Core.Ecu;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Engines;

/// <summary>The sections of an engine check, in report order.</summary>
public enum CheckSection
{
    /// <summary>Loading the content the engine depends on (its definition, parents, stock parts, stock tune).</summary>
    Content,
    Identity,
    Architecture,

    /// <summary>The stock build: every required slot has a part, every part installs in assembly order.</summary>
    Parts,

    /// <summary>Banks, slots and their scopes (EngineTopology).</summary>
    Topology,

    /// <summary>Mounting interfaces, valvetrain types, ECU outputs for controllable hardware, the car it fits.</summary>
    Interfaces,

    /// <summary>Fits and clearances, compression, derived geometry and its plausibility.</summary>
    Geometry,

    /// <summary>Speed ratings against the rev limit, boost against sensors and springs.</summary>
    Limits,
    Features,
    Provenance,

    /// <summary>Defaulted values and the stock tune's agreement with the stock build.</summary>
    Completeness,
}

/// <summary>One check result. Severity follows <see cref="IssueSeverity"/>: errors block, warnings inform the author.</summary>
public sealed record CheckFinding(CheckSection Section, IssueSeverity Severity, string Code, string Message)
{
    public override string ToString() => $"[{Severity.ToString().ToLowerInvariant()}] {Code}: {Message}";
}

/// <summary>Provenance coverage of the authored values of an engine's stock parts.</summary>
public sealed record ProvenanceCoverage(IReadOnlyDictionary<string, int> ByType, int Unrecorded, int Defaulted, int Authored)
{
    public int Recorded => ByType.Values.Sum();
}

/// <summary>
/// The result of <see cref="EngineCheck.Run"/>: whether an engine definition forms a complete, coherent, compatible,
/// sourced and supported engine. Deterministic: the same content gives the same report, byte for byte.
/// </summary>
public sealed class EngineCheckReport
{
    public required string EngineId { get; init; }
    public string EngineName { get; init; } = "";

    /// <summary>Findings in section order, then severity (errors first), code and message.</summary>
    public required IReadOnlyList<CheckFinding> Findings { get; init; }

    /// <summary>Descriptive lines per section (what was resolved), in report order.</summary>
    public required IReadOnlyList<(CheckSection Section, string Text)> Facts { get; init; }

    public ProvenanceCoverage? Provenance { get; init; }
    public IReadOnlyList<FeatureReportEntry> Features { get; init; } = Array.Empty<FeatureReportEntry>();

    /// <summary>Unrecorded provenance and defaulted fields, part by part (shown with --verbose).</summary>
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

    public IEnumerable<CheckFinding> Errors => Findings.Where(f => f.Severity == IssueSeverity.Error);
    public IEnumerable<CheckFinding> Warnings => Findings.Where(f => f.Severity == IssueSeverity.Warning);
    public bool Passed => !Errors.Any();
    public bool Has(string code) => Findings.Any(f => f.Code == code);

    /// <summary>CLI exit code: 0 no errors (and, with <paramref name="strict"/>, no warnings); 2 errors; 3 warnings under strict.</summary>
    public int ExitCode(bool strict = false) => !Passed ? 2 : strict && Warnings.Any() ? 3 : 0;

    public string Verdict(bool strict = false) => ExitCode(strict) switch
    {
        0 => Warnings.Any() ? $"PASS with {Count(IssueSeverity.Warning)} warning(s)" : "PASS",
        3 => $"BLOCKED under --strict: {Count(IssueSeverity.Warning)} warning(s)",
        _ => $"BLOCKED: {Count(IssueSeverity.Error)} error(s), {Count(IssueSeverity.Warning)} warning(s)",
    };

    private int Count(IssueSeverity s) => Findings.Count(f => f.Severity == s);

    /// <summary>The report as text. Information findings and details are shown only when <paramref name="verbose"/>.</summary>
    public string ToText(bool verbose = false, bool strict = false)
    {
        var sb = new StringBuilder();
        sb.Append("check-engine ").Append(EngineId);
        if (EngineName.Length > 0) sb.Append(" — ").Append(EngineName);
        sb.Append('\n');
        foreach (var section in Enum.GetValues<CheckSection>())
        {
            var facts = Facts.Where(f => f.Section == section).Select(f => f.Text).ToList();
            var findings = Findings.Where(f => f.Section == section).ToList();
            var shown = findings.Where(f => verbose || f.Severity != IssueSeverity.Info).ToList();
            if (facts.Count == 0 && findings.Count == 0) continue;
            string status = findings.Any(f => f.Severity == IssueSeverity.Error) ? "error"
                : findings.Any(f => f.Severity == IssueSeverity.Warning) ? "warning" : "ok";
            sb.Append('\n').Append(section.ToString().ToUpperInvariant()).Append("  ").Append(status);
            int hidden = findings.Count - shown.Count;
            if (hidden > 0) sb.Append(" (").Append(hidden).Append(" note(s); --verbose)");
            sb.Append('\n');
            foreach (var fact in facts) sb.Append("  ").Append(fact).Append('\n');
            foreach (var f in shown) sb.Append("  ").Append(f).Append('\n');
        }
        if (verbose && Details.Count > 0)
        {
            sb.Append("\nDETAILS\n");
            foreach (var d in Details) sb.Append("  ").Append(d).Append('\n');
        }
        sb.Append("\nRESULT  ").Append(Verdict(strict)).Append('\n');
        return sb.ToString();
    }
}

/// <summary>
/// Assembly-aware validation of one engine definition (Engine Authoring Factory 1.0, Phase 2; `carsim check-engine`).
/// Content loading answers "can this JSON be loaded?"; this answers "can these parts form this engine?". It builds the
/// stock assembly slot by slot and reuses the generic rules — <see cref="EngineTopology"/>, <see cref="AssemblyValidator"/>,
/// <see cref="EngineGeometry"/>, <see cref="EngineCapabilities"/>, <see cref="FeatureReport"/> — adding only what is
/// about authoring: identity, provenance coverage, defaults, the stock tune's agreement with the build, plausibility
/// heuristics and the cars the engine fits. It reads data only; no rule knows an engine, part or car by id.
/// </summary>
public static class EngineCheck
{
    // Plausibility heuristics (class D authoring aids, warnings only): outside these an author should double-check the
    // data, not the model. They never gate content.
    public const double MinBoreStrokeRatio = 0.6, MaxBoreStrokeRatio = 1.5;
    public const double MinRodRatio = 1.4, MaxRodRatio = 2.3;
    public const double MaxMeanPistonSpeedMs = 25.0;

    /// <summary>Stock tune beliefs that should agree with the stock build (relative tolerance).</summary>
    public const double TuneAgreementTolerance = 0.01;
    public const double DeadTimeToleranceMs = 0.05;

    /// <summary>Where each <see cref="AssemblyValidator"/> code is reported. A code missing here is reported under Parts.</summary>
    public static readonly IReadOnlyDictionary<string, CheckSection> ValidatorSections = new Dictionary<string, CheckSection>(StringComparer.Ordinal)
    {
        ["unsupported_topology"] = CheckSection.Topology,
        ["missing_part"] = CheckSection.Parts,
        ["missing_category"] = CheckSection.Parts,
        ["cylinder_count"] = CheckSection.Parts,
        ["set_count"] = CheckSection.Parts,
        ["missing_interface"] = CheckSection.Interfaces,
        ["valvetrain_mismatch"] = CheckSection.Interfaces,
        ["cam_phaser_uncontrolled"] = CheckSection.Interfaces,
        ["valve_lift_uncontrolled"] = CheckSection.Interfaces,
        ["intake_runner_uncontrolled"] = CheckSection.Interfaces,
        ["main_journal_mismatch"] = CheckSection.Geometry,
        ["main_bearing_mismatch"] = CheckSection.Geometry,
        ["rod_journal_mismatch"] = CheckSection.Geometry,
        ["rod_bearing_mismatch"] = CheckSection.Geometry,
        ["piston_pin_mismatch"] = CheckSection.Geometry,
        ["piston_bore_mismatch"] = CheckSection.Geometry,
        ["gasket_bore_small"] = CheckSection.Geometry,
        ["coil_bind"] = CheckSection.Geometry,
        ["piston_head_contact"] = CheckSection.Geometry,
        ["tight_quench"] = CheckSection.Geometry,
        ["poor_quench"] = CheckSection.Geometry,
        ["no_clearance_volume"] = CheckSection.Geometry,
        ["compression_ratio"] = CheckSection.Geometry,
        ["high_compression"] = CheckSection.Geometry,
        ["low_compression"] = CheckSection.Geometry,
        ["default_intake_geometry"] = CheckSection.Completeness,
        ["valve_float"] = CheckSection.Limits,
        ["crank_overspeed"] = CheckSection.Limits,
        ["flywheel_overspeed"] = CheckSection.Limits,
        ["map_sensor_range"] = CheckSection.Limits,
        ["boost_by_spring"] = CheckSection.Limits,
        ["boost_target_above_map_sensor"] = CheckSection.Limits,
        ["boost_target_below_spring"] = CheckSection.Limits,
    };

    private sealed class Collector
    {
        public readonly List<CheckFinding> Findings = new();
        public readonly List<(CheckSection, string)> Facts = new();
        public void Add(CheckSection s, IssueSeverity sev, string code, string message) => Findings.Add(new CheckFinding(s, sev, code, message));
        public void Fact(CheckSection s, string text) => Facts.Add((s, text));
    }

    /// <summary>Checks <paramref name="engineId"/> against the content as loaded (errors and all).</summary>
    public static EngineCheckReport Run(ContentLoadResult load, string engineId)
    {
        var c = new Collector();
        var db = load.Database;
        var engine = db.Engines.GetValueOrDefault(engineId);
        var abstractBase = db.AbstractEngines.GetValueOrDefault(engineId);
        var definition = engine ?? abstractBase;

        // ---- Content: load errors of this engine, its family chain, its stock parts and its stock tune ----------------
        var related = new HashSet<string>(StringComparer.Ordinal) { engineId };
        for (var d = definition; d?.Extends is { } parent; d = db.Engines.GetValueOrDefault(parent) ?? db.AbstractEngines.GetValueOrDefault(parent))
            if (!related.Add(parent)) break;
        if (definition != null)
        {
            foreach (var partId in definition.StockParts.Values) related.Add(partId);
            if (definition.StockTune.Length > 0) related.Add(definition.StockTune);
        }
        var relatedErrors = load.Errors.Where(e => related.Contains(e.ItemId)).ToList();
        foreach (var e in relatedErrors) c.Add(CheckSection.Content, IssueSeverity.Error, "content_error", e.ToString());
        int otherErrors = load.Errors.Count - relatedErrors.Count;
        if (otherErrors > 0)
            c.Add(CheckSection.Content, IssueSeverity.Info, "unrelated_content_errors",
                $"{otherErrors} load error(s) in other content (run carsim validate).");

        // ---- Identity ------------------------------------------------------------------------------------------------
        if (definition == null)
        {
            c.Add(CheckSection.Identity, IssueSeverity.Error, "unknown_engine", relatedErrors.Count > 0
                ? $"Engine '{engineId}' did not load (see CONTENT)."
                : $"No engine '{engineId}'. Loaded: {string.Join(", ", db.Engines.Keys.OrderBy(k => k, StringComparer.Ordinal))}.");
            return Finish(engineId, "", c, null, Array.Empty<FeatureReportEntry>(), Array.Empty<string>());
        }
        CheckIdentity(c, db, definition, abstractBase != null);
        if (abstractBase != null)
            return Finish(engineId, definition.Name, c, null, Array.Empty<FeatureReportEntry>(), Array.Empty<string>());

        // ---- Architecture and topology (the family's own rules) --------------------------------------------------------
        var topology = EngineTopology.CheckFamily(definition);
        IReadOnlyList<EngineSlotDefinition>? order = null;
        try { order = definition.AssemblyOrder(); }
        catch (InvalidOperationException ex) { c.Add(CheckSection.Topology, IssueSeverity.Error, "slot_graph_cycle", ex.Message); }
        c.Fact(CheckSection.Topology, $"{definition.Slots.Count} slots ({definition.Slots.Count(s => s.Required)} required), " +
                                     $"{definition.Slots.Count(s => s.Banks.Count > 0)} bank-scoped");

        // ---- Parts: build the stock assembly slot by slot, reporting every failure instead of stopping -----------------
        var assembly = new EngineAssembly(definition);
        var factory = new PartInstanceFactory();
        foreach (var slot in order ?? definition.Slots)
        {
            if (!definition.StockParts.TryGetValue(slot.Id, out var partId))
            {
                if (slot.Required)
                    c.Add(CheckSection.Parts, IssueSeverity.Error, "stock_part_missing", $"Required slot '{slot.Id}' ({slot.Label}) has no stock part.");
                continue;
            }
            if (!db.Parts.TryGetValue(partId, out var part)) continue; // a content error, reported above
            var result = assembly.Install(slot.Id, factory.Create(part));
            if (!result.Ok)
                c.Add(CheckSection.Parts, IssueSeverity.Error, "stock_install_failed", $"'{partId}' does not install in '{slot.Id}': {result.Message}");
        }
        c.Fact(CheckSection.Parts, $"stock build: {assembly.Installed.Count} of {definition.Slots.Count} slots filled, " +
                                   $"{assembly.Installed.Values.Select(p => p.Definition.Id).Distinct().Count()} distinct parts");

        // ---- The generic assembly rules, with the stock tune's operating intent ---------------------------------------
        var tuneDoc = definition.StockTune.Length > 0 ? db.Tunes.GetValueOrDefault(definition.StockTune) : null;
        EcuTune? tune = null;
        if (tuneDoc != null)
        {
            try { tune = EcuTune.FromDocument(tuneDoc); }
            catch (InvalidDataException ex) { c.Add(CheckSection.Completeness, IssueSeverity.Error, "stock_tune_invalid", ex.Message); }
        }
        var validation = AssemblyValidator.Validate(assembly, new ValidationContext(tune?.RevLimitRpm, tune?.MaxBoostTargetKpa));
        foreach (var issue in validation.Issues)
            c.Add(ValidatorSections.GetValueOrDefault(issue.Code, CheckSection.Parts), issue.Severity, issue.Code, issue.Message);
        if (topology.Count == 0) c.Fact(CheckSection.Topology, "banks, slot scopes and assembly order are consistent");

        var capabilities = EngineCapabilities.Resolve(assembly);
        CheckArchitecture(c, definition, capabilities);
        CheckVehicles(c, db, definition);
        CheckGeometry(c, assembly, tune);
        var features = FeatureReport.For(definition.Identity, capabilities);
        CheckFeatures(c, definition, features);
        var (coverage, details) = CheckProvenance(c, db, definition, assembly);
        CheckCompleteness(c, assembly, tune, tuneDoc, capabilities);
        return Finish(engineId, definition.Name, c, coverage, features, details);
    }

    private static EngineCheckReport Finish(string id, string name, Collector c, ProvenanceCoverage? coverage,
        IReadOnlyList<FeatureReportEntry> features, IReadOnlyList<string> details) => new()
    {
        EngineId = id,
        EngineName = name,
        Findings = c.Findings.Distinct()
            .OrderBy(f => f.Section).ThenByDescending(f => f.Severity)
            .ThenBy(f => f.Code, StringComparer.Ordinal).ThenBy(f => f.Message, StringComparer.Ordinal).ToList(),
        Facts = c.Facts,
        Provenance = coverage,
        Features = features,
        Details = details,
    };

    private static void CheckIdentity(Collector c, ContentDatabase db, EngineDefinition e, bool isAbstract)
    {
        var chain = new List<string>();
        for (var d = e; d?.Extends is { } parent && !chain.Contains(parent); d = db.Engines.GetValueOrDefault(parent) ?? db.AbstractEngines.GetValueOrDefault(parent))
            chain.Add(parent);
        var id = e.Identity;
        string who = id == null ? "" : string.Join(" ", new[] { id.Manufacturer, id.Variant ?? id.Family }.Where(s => !string.IsNullOrEmpty(s)));
        c.Fact(CheckSection.Identity, id == null ? "identity not recorded" : $"{id.Kind}{(who.Length > 0 ? ", " + who : "")}" +
                                                                          (id.Family != null ? $"; family {id.Family}" : ""));
        if (chain.Count > 0) c.Fact(CheckSection.Identity, $"variant of {string.Join(" → ", chain)}");
        if (isAbstract)
        {
            var variants = db.Engines.Values.Where(v => v.Extends == e.Id).Select(v => v.Id).OrderBy(v => v, StringComparer.Ordinal).ToList();
            c.Add(CheckSection.Identity, IssueSeverity.Error, "abstract_engine",
                $"'{e.Id}' is an abstract family base: it cannot be built or played. " +
                (variants.Count > 0 ? $"Check one of its variants: {string.Join(", ", variants)}." : "No variant extends it yet."));
            return;
        }
        if (id == null)
        {
            c.Add(CheckSection.Identity, IssueSeverity.Warning, "identity_missing",
                "No identity: say whether the engine is real, fictional or synthetic (PARTS_DATABASE.md, \"Engine families\").");
            return;
        }
        if (id.Kind == "real")
        {
            if (string.IsNullOrWhiteSpace(id.Family))
                c.Add(CheckSection.Identity, IssueSeverity.Warning, "identity_family_missing", "A real engine should name its family.");
            if (string.IsNullOrWhiteSpace(id.Variant))
                c.Add(CheckSection.Identity, IssueSeverity.Info, "identity_variant_missing", "The variant code is not recorded.");
            if (string.IsNullOrWhiteSpace(id.Reference))
                c.Add(CheckSection.Identity, IssueSeverity.Warning, "identity_reference_missing",
                    "A real engine should cite its reference (identity.reference, a source id).");
        }
    }

    private static void CheckArchitecture(Collector c, EngineDefinition e, EngineCapabilities caps)
    {
        c.Fact(CheckSection.Architecture, string.Join(", ", caps.Describe()));
        if (e.Banks.Count > 1)
            foreach (var bank in e.Banks)
                c.Fact(CheckSection.Architecture, $"bank '{bank.Id}': cylinders {string.Join(", ", bank.Cylinders)}");
        c.Fact(CheckSection.Architecture, $"air paths: {caps.IntakePaths} intake, {caps.ExhaustPaths} exhaust; " +
                                          $"{caps.Turbochargers} turbocharger(s){(caps.Intercooled ? ", intercooled" : "")}");
        if (e.FiringOrder.Count == 0 && e.Cylinders > 1)
            c.Add(CheckSection.Architecture, IssueSeverity.Info, "firing_order_missing",
                "No firing order (validated data only; the mean-value model does not use it).");
        else if (e.FiringOrder.Count > 0)
            c.Fact(CheckSection.Architecture, $"firing order {string.Join("-", e.FiringOrder)}");
    }

    /// <summary>Which cars take the engine, by interfaces (the bellhousing today).</summary>
    private static void CheckVehicles(Collector c, ContentDatabase db, EngineDefinition e)
    {
        if (db.Vehicles.Count == 0) return;
        var engineParts = e.StockParts.Where(kv => db.Parts.ContainsKey(kv.Value)).Select(kv => (kv.Key, db.Parts[kv.Value])).ToList();
        var fits = db.Vehicles.Values.OrderBy(v => v.Id, StringComparer.Ordinal)
            .Where(v => Vehicles.VehicleCompatibility.InterfaceProblems(engineParts,
                v.StockParts.Where(kv => db.Parts.ContainsKey(kv.Value)).Select(kv => (kv.Key, db.Parts[kv.Value])), e.Name).Count == 0)
            .Select(v => v.Id).ToList();
        if (fits.Count > 0) c.Fact(CheckSection.Interfaces, $"fits (stock chassis): {string.Join(", ", fits)}");
        else
            c.Add(CheckSection.Interfaces, IssueSeverity.Warning, "no_vehicle_fits",
                "No loaded car's stock chassis takes this engine (bellhousing): it runs on the engine stand only until a gearbox with its pattern exists.");
    }

    private static void CheckGeometry(Collector c, EngineAssembly a, EcuTune? tune)
    {
        var def = a.Definition;
        for (int b = 0; b < def.Banks.Count; b++)
        {
            var g = EngineGeometry.TryCreate(a, b, out var missing);
            string on = def.Banks.Count > 1 ? $"bank '{def.Banks[b].Id}': " : "";
            if (g == null)
            {
                c.Add(CheckSection.Geometry, IssueSeverity.Error, "geometry_incomplete",
                    $"{on}geometry cannot be derived: missing {string.Join(", ", missing)}.");
                continue;
            }
            if (b == 0)
            {
                c.Fact(CheckSection.Geometry, string.Create(CultureInfo.InvariantCulture,
                    $"derived: {Units.M3ToCc(g.Displacement):F1} cc, bore × stroke {Units.MToMm(g.Bore):F1} × {Units.MToMm(g.Stroke):F1} mm, rod ratio {g.RodRatio:F2}"));
                double boreStroke = g.Bore / g.Stroke;
                if (boreStroke < MinBoreStrokeRatio || boreStroke > MaxBoreStrokeRatio)
                    c.Add(CheckSection.Geometry, IssueSeverity.Warning, "implausible_bore_stroke", string.Create(CultureInfo.InvariantCulture,
                        $"Bore/stroke {boreStroke:F2} is outside {MinBoreStrokeRatio}–{MaxBoreStrokeRatio}: check the bore and stroke."));
                if (g.RodRatio < MinRodRatio || g.RodRatio > MaxRodRatio)
                    c.Add(CheckSection.Geometry, IssueSeverity.Warning, "implausible_rod_ratio", string.Create(CultureInfo.InvariantCulture,
                        $"Rod/stroke {g.RodRatio:F2} is outside {MinRodRatio}–{MaxRodRatio}: check the rod length and stroke."));
                if (tune != null && g.MeanPistonSpeed(tune.RevLimitRpm) > MaxMeanPistonSpeedMs)
                    c.Add(CheckSection.Geometry, IssueSeverity.Warning, "implausible_piston_speed", string.Create(CultureInfo.InvariantCulture,
                        $"Mean piston speed {g.MeanPistonSpeed(tune.RevLimitRpm):F1} m/s at the {tune.RevLimitRpm:F0} rpm limit is above {MaxMeanPistonSpeedMs} m/s."));
            }
            if (def.Banks.Count > 1 || b > 0)
                c.Fact(CheckSection.Geometry, string.Create(CultureInfo.InvariantCulture, $"{on}compression {g.CompressionRatio:F2}:1"));
        }
    }

    private static void CheckFeatures(Collector c, EngineDefinition e, IReadOnlyList<FeatureReportEntry> features)
    {
        bool real = e.Identity?.Kind == "real";
        foreach (var f in features)
        {
            switch (f.Status)
            {
                case FeatureStatus.Supported:
                    c.Fact(CheckSection.Features, $"{f.Feature}: supported");
                    break;
                case FeatureStatus.NotModelled:
                    c.Add(CheckSection.Features, IssueSeverity.Warning, "feature_not_modelled",
                        $"{f.Feature} ({f.Description}) is not modelled; stands in: {f.Approximation}");
                    break;
                case FeatureStatus.MissingData:
                    c.Add(CheckSection.Features, IssueSeverity.Error, "feature_missing_data",
                        $"{f.Feature} is declared and modelled, but no stock part provides it: the content lacks its hardware data.");
                    break;
                case FeatureStatus.Undeclared:
                    c.Add(CheckSection.Features, real ? IssueSeverity.Warning : IssueSeverity.Info, "feature_undeclared",
                        $"The stock parts provide {f.Feature} ({f.Description}), which identity.features does not declare.");
                    break;
            }
        }
    }

    private static (ProvenanceCoverage, IReadOnlyList<string>) CheckProvenance(Collector c, ContentDatabase db, EngineDefinition e, EngineAssembly a)
    {
        var byType = ProvenanceTypes.All.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        int unrecorded = 0, defaulted = 0, authored = 0;
        var details = new List<string>();
        foreach (var part in a.Installed.Values.Select(p => p.Definition).DistinctBy(p => p.Id).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            PartSpecRegistry.TryGetSpecType(part.Category, out var specType);
            var defaults = ProvenanceFields.Of(specType).Except(part.AuthoredSpecFields).ToList();
            defaulted += defaults.Count;
            authored += part.AuthoredSpecFields.Count;
            var missing = new List<string>();
            foreach (var field in part.AuthoredSpecFields)
            {
                if (part.Provenance.TryGetValue(field, out var record)) byType[record.Type]++;
                else { unrecorded++; missing.Add(field); }
            }
            foreach (var (field, record) in part.Provenance.Where(kv => kv.Value.Type == ProvenanceTypes.Fitted))
                c.Add(CheckSection.Provenance, IssueSeverity.Warning, "provenance_fitted",
                    $"{part.Id}.{field} is fitted to reproduce \"{record.Target}\": a reference output, not a physical source.");
            if (missing.Count > 0) details.Add($"{part.Id}: no provenance for {string.Join(", ", missing)}");
            if (defaults.Count > 0) details.Add($"{part.Id}: defaulted {string.Join(", ", defaults)}");
        }
        var coverage = new ProvenanceCoverage(byType, unrecorded, defaulted, authored);
        c.Fact(CheckSection.Provenance, $"{coverage.Recorded} of {authored} authored values recorded: " +
            string.Join(", ", ProvenanceTypes.All.Select(t => $"{t} {byType[t]}")) + $"; unrecorded {unrecorded}; defaulted {defaulted}");
        if (e.Provenance.Count > 0) c.Fact(CheckSection.Provenance, $"architecture: {string.Join(", ", e.Provenance.Select(kv => $"{kv.Key} {kv.Value.Type}"))}");
        if (unrecorded > 0)
            c.Add(CheckSection.Provenance, e.Identity?.Kind == "real" ? IssueSeverity.Warning : IssueSeverity.Info, "provenance_incomplete",
                $"{unrecorded} authored value(s) of the stock parts have no provenance record (--verbose lists them).");
        return (coverage, details);
    }

    private static void CheckCompleteness(Collector c, EngineAssembly a, EcuTune? tune, TuneDocument? doc, EngineCapabilities caps)
    {
        if (doc == null)
        {
            c.Add(CheckSection.Completeness, IssueSeverity.Info, "no_stock_tune",
                "No stock tune yet: the engine cannot be started until one is calibrated (ENGINE_AUTHORING_GUIDE.md §6).");
            return;
        }
        c.Fact(CheckSection.Completeness, $"stock tune: {doc.Id}");
        if (tune == null) return;
        string Pct(double x) => x.ToString("P1", CultureInfo.InvariantCulture);
        var g = EngineGeometry.TryCreate(a, out _);
        if (g != null)
        {
            double actual = Units.M3ToCc(g.Displacement);
            if (Math.Abs(tune.DisplacementCc / actual - 1) > TuneAgreementTolerance)
                c.Add(CheckSection.Completeness, IssueSeverity.Warning, "tune_displacement_mismatch", string.Create(CultureInfo.InvariantCulture,
                    $"The stock tune assumes {tune.DisplacementCc:F0} cc; the stock build displaces {actual:F0} cc ({Pct(tune.DisplacementCc / actual - 1)})."));
        }
        if (a.SpecOf<InjectorSpec>(PartCategory.Injectors) is { } inj)
        {
            if (Math.Abs(tune.InjectorFlowCcMin / inj.FlowCcMin - 1) > TuneAgreementTolerance)
                c.Add(CheckSection.Completeness, IssueSeverity.Warning, "tune_injector_flow_mismatch", string.Create(CultureInfo.InvariantCulture,
                    $"The stock tune assumes {tune.InjectorFlowCcMin:F0} cc/min injectors; the stock injectors flow {inj.FlowCcMin:F0} cc/min."));
            if (Math.Abs(tune.InjectorDeadTimeMs - inj.DeadTimeMs) > DeadTimeToleranceMs)
                c.Add(CheckSection.Completeness, IssueSeverity.Warning, "tune_dead_time_mismatch", string.Create(CultureInfo.InvariantCulture,
                    $"The stock tune assumes a {tune.InjectorDeadTimeMs:F2} ms dead time; the stock injectors have {inj.DeadTimeMs:F2} ms."));
        }
        if (a.SpecOf<EcuSpec>(PartCategory.Ecu) is { } ecu && tune.RevLimitRpm > ecu.MaxRevLimitRpm)
            c.Add(CheckSection.Completeness, IssueSeverity.Warning, "tune_rev_limit_above_ecu", string.Create(CultureInfo.InvariantCulture,
                $"The stock tune's {tune.RevLimitRpm:F0} rpm limit is above the stock ECU's {ecu.MaxRevLimitRpm:F0} rpm ceiling."));
        double axisTop = doc.RpmAxis.Length > 0 ? doc.RpmAxis[^1] : 0;
        if (caps.IntakeCamPhasing && doc.IntakeCamAdvanceDeg == null)
            c.Add(CheckSection.Completeness, IssueSeverity.Info, "tune_no_cam_table",
                "The ECU drives an intake phaser but the stock tune has no intake_cam_advance_deg table: the phaser stays parked.");
        if (caps.VariableValveLift && doc.ValveLiftSwitchRpm == null)
            c.Add(CheckSection.Completeness, IssueSeverity.Warning, "tune_no_switch_speed",
                "The ECU switches two-stage cams but the stock tune has no valve_lift_switch_rpm.");
        if (caps.VariableIntakeRunner && doc.IntakeRunnerSwitchRpm == null)
            c.Add(CheckSection.Completeness, IssueSeverity.Warning, "tune_no_switch_speed",
                "The ECU switches the intake runners but the stock tune has no intake_runner_switch_rpm.");
        foreach (var (name, rpm) in new[] { ("valve_lift_switch_rpm", doc.ValveLiftSwitchRpm), ("intake_runner_switch_rpm", doc.IntakeRunnerSwitchRpm) })
            if (rpm is double r && r > axisTop)
                c.Add(CheckSection.Completeness, IssueSeverity.Warning, "tune_switch_outside_axis", string.Create(CultureInfo.InvariantCulture,
                    $"{name} {r:F0} is above the tune's rpm axis (ends at {axisTop:F0})."));
        if (tune.MaxBoostTargetKpa is double boost && doc.LoadAxisKpa.Length > 0 && boost > doc.LoadAxisKpa[^1])
            c.Add(CheckSection.Completeness, IssueSeverity.Warning, "tune_load_axis_below_boost", string.Create(CultureInfo.InvariantCulture,
                $"The boost target reaches {boost:F0} kPa but the tables stop at {doc.LoadAxisKpa[^1]:F0} kPa."));
    }
}
