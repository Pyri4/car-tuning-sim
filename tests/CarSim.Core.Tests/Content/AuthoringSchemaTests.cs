using System.Text.Json;
using System.Text.RegularExpressions;
using CarSim.Core.Content;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Content;

/// <summary>
/// The authoring schema of Engine Authoring Factory 1.0, Phase 1: provenance and sources, part and engine variants
/// (<c>extends</c>, abstract family bases), engine identity with declared features, and the checks the loader makes on
/// them. None of it reaches the physics: the regression fingerprint pins that, and the audit at the end of this file
/// forbids the simulation from reading it.
/// </summary>
public class AuthoringSchemaTests
{
    private static ContentLoadResult Layers(params (string source, string json)[][] layers) => ContentLoader.LoadFromLayers(layers);

    private static (string, string)[] BaseLayer() =>
        Directory.EnumerateFiles(TestContent.BaseContentPath, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetRelativePath(TestContent.BaseContentPath, f), File.ReadAllText(f))).ToArray();

    private static ContentDatabase WithLayer(string json)
    {
        var r = Layers(BaseLayer(), new[] { ("test/layer.json", json) });
        Assert.True(r.Success, string.Join("\n", r.Errors));
        return r.Database;
    }

    private static IReadOnlyList<ContentError> ErrorsWithLayer(string json) =>
        Layers(BaseLayer(), new[] { ("test/layer.json", json) }).Errors;

    private const string Sources = """
        "sources": [ { "id": "spec_sheet", "type": "manufacturer", "title": "A maker's spec sheet" } ]
        """;

    private static string Pump(string provenance, string id = "oil_pump.x", string extra = "") => $$"""
        { "id": "{{id}}", "name": "Pump", "category": "oil_pump", "price": 1, "mass_kg": 2{{extra}},
          "spec": { "displacement_cc_per_rev": 12, "relief_pressure_kpa": 450 },
          "provenance": {{provenance}} }
        """;

    // ---- Provenance ----------------------------------------------------------------------------------------------

    [Fact]
    public void ProvenanceIsExpandedOverTheAuthoredFieldsOnly()
    {
        var db = WithLayer($$"""
            { {{Sources}}, "parts": [ {{Pump("""
                { "*": { "type": "published", "source": "spec_sheet" },
                  "relief_pressure_kpa": { "type": "estimated", "confidence": "low", "notes": "no source" } }
                """)}} ] }
            """);
        var part = db.GetPart("oil_pump.x");
        Assert.Equal(new[] { "displacement_cc_per_rev", "relief_pressure_kpa" }, part.AuthoredSpecFields);
        Assert.Equal(new[] { "displacement_cc_per_rev", "mass_kg", "relief_pressure_kpa" }, part.Provenance.Keys);
        Assert.Equal(ProvenanceTypes.Published, part.Provenance["displacement_cc_per_rev"].Type);
        Assert.Equal("spec_sheet", part.Provenance["mass_kg"].Source);
        var relief = part.Provenance["relief_pressure_kpa"];
        Assert.Equal((ProvenanceTypes.Estimated, "low", "no source"), (relief.Type, relief.Confidence, relief.Notes));
        Assert.Equal("A maker's spec sheet", db.Sources["spec_sheet"].Title);
    }

    [Fact]
    public void AFieldLeftToItsDefaultHasNoProvenance()
    {
        // The injector set states no dead time: the schema default is nobody's claim, so "*" does not cover it.
        var db = WithLayer($$"""
            { {{Sources}}, "parts": [ { "id": "inj.x", "name": "Injectors", "category": "injectors", "price": 1,
              "spec": { "count": 4, "flow_cc_min": 300 },
              "provenance": { "*": { "type": "published", "source": "spec_sheet" } } } ] }
            """);
        var part = db.GetPart("inj.x");
        Assert.Equal(new[] { "count", "flow_cc_min" }, part.Provenance.Keys);
        Assert.DoesNotContain("dead_time_ms", part.AuthoredSpecFields);
    }

    [Theory]
    [InlineData("""{ "bore_mm": { "type": "estimated" } }""", "'bore_mm' is not a spec field")]
    [InlineData("""{ "relief_pressure_kpa": { "type": "guessed" } }""", "type must be one of")]
    [InlineData("""{ "relief_pressure_kpa": { "type": "published" } }""", "needs a 'source'")]
    [InlineData("""{ "relief_pressure_kpa": { "type": "secondary", "source": "nowhere" } }""", "unknown source 'nowhere'")]
    [InlineData("""{ "relief_pressure_kpa": { "type": "derived" } }""", "needs a 'method'")]
    [InlineData("""{ "relief_pressure_kpa": { "type": "converted", "source": "spec_sheet" } }""", "needs a 'method'")]
    [InlineData("""{ "relief_pressure_kpa": { "type": "fitted" } }""", "needs a 'target'")]
    [InlineData("""{ "relief_pressure_kpa": { "type": "estimated", "target": "300 N·m" } }""", "only a fitted value has a 'target'")]
    [InlineData("""{ "relief_pressure_kpa": { "type": "estimated", "confidence": "certain" } }""", "confidence must be one of")]
    [InlineData("""{ "relief_pressure_kpa": { "type": "estimated", "source_url": "x" } }""", "source_url")]
    [InlineData("""{ "relief_pressure_kpa": { "notes": "no type" } }""", "type")]
    public void MalformedProvenanceIsRejectedWithItsReason(string provenance, string expected)
    {
        var errors = ErrorsWithLayer($$"""{ {{Sources}}, "parts": [ {{Pump(provenance)}} ] }""");
        Assert.Contains(errors, e => e.ItemId == "oil_pump.x" && e.Message.Contains(expected));
    }

    [Theory]
    [InlineData("""{ "id": "s", "type": "blog", "title": "t" }""", "type must be one of")]
    [InlineData("""{ "id": "s", "type": "press", "title": "" }""", "missing 'title'")]
    public void MalformedSourcesAreRejected(string source, string expected) =>
        Assert.Contains(ErrorsWithLayer($$"""{ "sources": [ {{source}} ] }"""), e => e.Message.Contains(expected));

    [Fact]
    public void ProvenanceAndSourcesRoundTripDeterministically()
    {
        var record = new ValueProvenance
        {
            Type = ProvenanceTypes.Derived, Source = "spec_sheet", Method = "A-D1", Confidence = "medium", Notes = "frozen first",
        };
        string json = JsonSerializer.Serialize(record, ContentJson.Options);
        Assert.Equal(record, JsonSerializer.Deserialize<ValueProvenance>(json, ContentJson.Options));
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<ValueProvenance>(json, ContentJson.Options), ContentJson.Options));
        Assert.Contains("\"type\": \"derived\"", json);

        var source = TestContent.Database.Sources["bmw_m54_published_figures"];
        string sourceJson = JsonSerializer.Serialize(source, ContentJson.Options);
        Assert.Equal(source with { LoadedFrom = "" }, JsonSerializer.Deserialize<SourceDefinition>(sourceJson, ContentJson.Options));

        // Loading twice gives the same records in the same order.
        string Dump(ContentDatabase db) => JsonSerializer.Serialize(
            db.Parts.Values.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => new { p.Id, p.Provenance }), ContentJson.Options);
        Assert.Equal(Dump(ContentLoader.LoadDirectory(TestContent.BaseContentPath).GetOrThrow()),
                     Dump(ContentLoader.LoadDirectory(TestContent.BaseContentPath).GetOrThrow()));
    }

    [Fact]
    public void TheM54sProvenanceSaysWhatTheRepositoryKnowsAndNoMore()
    {
        var db = TestContent.Database;
        Assert.Equal(ProvenanceTypes.Published, db.GetPart("m54.block.oem").Provenance["bore_mm"].Type);
        Assert.Equal(ProvenanceTypes.Derived, db.GetPart("m54.pistons.oem").Provenance["dish_volume_cc"].Type);
        Assert.Equal(ProvenanceTypes.Derived, db.GetPart("m54.intake.disa").Provenance["runner_length_mm"].Type);
        Assert.Equal(ProvenanceTypes.Estimated, db.GetPart("m54.head.oem").Provenance["intake_port_flow_cfm"].Type);
        Assert.Equal(ProvenanceTypes.Converted, db.GetPart("m54.cams.oem").Provenance["intake_duration_deg"].Type);
        // Nothing the repository cannot support is claimed: no fitted values, and values without a recorded origin
        // (the lobe separation, the head gasket's bore) have no record rather than an invented one.
        Assert.DoesNotContain(db.Parts.Values.SelectMany(p => p.Provenance.Values), v => v.Type == ProvenanceTypes.Fitted);
        Assert.False(db.GetPart("m54.cams.oem").Provenance.ContainsKey("lobe_separation_deg"));
        Assert.False(db.GetPart("m54.head_gasket.oem").Provenance.ContainsKey("bore_mm"));
        // The fictional K20 and the aftermarket parts carry none.
        Assert.Empty(db.GetPart("k20.block.oem").Provenance);
        Assert.Empty(db.GetPart("m54.pistons.forged_hc").Provenance);
    }

    // ---- Identity and declared features ----------------------------------------------------------------------------

    [Fact]
    public void TheM54DeclaresItsUnmodelledHardwareWithWhatStandsIn()
    {
        var m54 = TestContent.Database.GetEngine(TestContent.M54);
        Assert.Equal(("real", "M54B30", "bmw_m54_published_figures"), (m54.Identity!.Kind, m54.Identity.Variant, m54.Identity.Reference));
        var report = FeatureReport.For(m54.Identity, EngineCapabilities.Resolve(TestContent.StockM54()));
        Assert.Equal(FeatureStatus.Supported, report.Single(f => f.Feature == "intake_cam_phasing").Status);
        Assert.Equal(FeatureStatus.Supported, report.Single(f => f.Feature == "variable_intake").Status);
        var exhaust = report.Single(f => f.Feature == "exhaust_cam_phasing");
        Assert.Equal(FeatureStatus.NotModelled, exhaust.Status);
        Assert.Contains("park", exhaust.Approximation);
        Assert.DoesNotContain(report, f => f.Status == FeatureStatus.MissingData);
        Assert.Equal("fictional", TestContent.Database.GetEngine(TestContent.K20).Identity!.Kind);
        Assert.All(TestContent.MatrixFamilies, f => Assert.Equal("synthetic", TestContent.Matrix.GetEngine(f).Identity!.Kind));
    }

    [Fact]
    public void AModelledFeatureWithoutItsHardwareIsMissingDataAndUndeclaredHardwareIsReported()
    {
        var caps = EngineCapabilities.Resolve(TestContent.StockK20()); // naturally aspirated, no phaser
        var identity = new EngineIdentity { Kind = "fictional", Features = new[] { new DeclaredFeature { Feature = "turbocharger" } } };
        Assert.Equal(FeatureStatus.MissingData, Assert.Single(FeatureReport.For(identity, caps)).Status);
        var twinTurbo = EngineAssembly.CreateStock(TestContent.Matrix.GetEngine("syn_v6_tt"), TestContent.Matrix, new PartInstanceFactory());
        var undeclared = FeatureReport.For(null, EngineCapabilities.Resolve(twinTurbo));
        Assert.All(undeclared, f => Assert.Equal(FeatureStatus.Undeclared, f.Status));
        Assert.Contains(undeclared, f => f.Feature == "twin_turbo_parallel");
    }

    [Theory]
    [InlineData("""{ "kind": "real", "features": [ { "feature": "direct_injection" } ] }""", "not modelled; say what stands in")]
    [InlineData("""{ "kind": "real", "features": [ { "feature": "warp_drive", "approximation": "none" } ] }""", "unknown feature 'warp_drive'")]
    [InlineData("""{ "kind": "real", "features": [ { "feature": "turbocharger" }, { "feature": "turbocharger" } ] }""", "declared twice")]
    [InlineData("""{ "kind": "imaginary" }""", "kind must be one of")]
    [InlineData("""{ "kind": "real", "reference": "nowhere" }""", "unknown reference source 'nowhere'")]
    [InlineData("""{ "kind": "real", "features": [ { "feature": "dry_sump", "approximation": "wet sump", "status": "x" } ] }""", "status")]
    public void InvalidIdentitiesAreRejected(string identity, string expected)
    {
        var errors = ErrorsWithLayer($$"""{ "engines": [ { "id": "v", "extends": "isar_m54", "name": "V", "identity": {{identity}} } ] }""");
        Assert.Contains(errors, e => e.ItemId == "v" && e.Message.Contains(expected));
    }

    [Fact]
    public void AnUnmodelledFeatureIsDeclarableButNeverSimulated()
    {
        // A B58-like declaration on a copy of the M54: accepted with its approximations, reported, and the engine runs
        // exactly as the M54 does. Declaring hardware changes nothing the physics reads.
        var db = WithLayer("""
            { "engines": [ { "id": "b58_like", "extends": "isar_m54", "name": "Declared, not modelled",
                "identity": { "kind": "real", "variant": "demo", "features": [
                  { "feature": "direct_injection", "approximation": "port injection" },
                  { "feature": "variable_valve_lift_continuous", "approximation": "omitted" },
                  { "feature": "twin_scroll_turbine", "approximation": "omitted" } ] } } ] }
            """);
        var copy = db.GetEngine("b58_like");
        var report = FeatureReport.For(copy.Identity, EngineCapabilities.Resolve(EngineAssembly.CreateStock(copy, db, new PartInstanceFactory())));
        Assert.Equal(3, report.Count(f => f.Status == FeatureStatus.NotModelled));
        Assert.Equal(PeakTorque(db, TestContent.M54), PeakTorque(db, "b58_like"));
    }

    // ---- Engine families and variants ------------------------------------------------------------------------------

    private static double PeakTorque(ContentDatabase db, string engineId)
    {
        var engine = db.GetEngine(engineId);
        var tune = EcuTune.FromDocument(db.GetTune(engine.StockTune));
        var config = EngineConfiguration.Build(EngineAssembly.CreateStock(engine, db, new PartInstanceFactory()),
            db.GetFuel("gasoline_98"), new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa)).GetOrThrow();
        return SteadyStateSweep.Run(new EngineSimulation(config, tune, EngineState.Warm()), 2000, 6000, 1000, settleSeconds: 0.5)
            .Max(p => p.Torque);
    }

    [Fact]
    public void AVariantReusesItsFamilyAndOverridesOnlyWhatItStates()
    {
        var db = WithLayer("""
            { "engines": [ { "id": "isar_m54_hc", "extends": "isar_m54", "name": "Isar M54 high compression",
                "identity": { "variant": "M54B30 (forged pistons)" },
                "stock_parts": { "pistons": "m54.pistons.forged_hc" } } ] }
            """);
        var m54 = db.GetEngine(TestContent.M54);
        var variant = db.GetEngine("isar_m54_hc");
        Assert.Equal(TestContent.M54, variant.Extends);
        Assert.Equal(m54.Slots.Select(s => (s.Id, s.Category, string.Join(",", s.InstallAfter))),
                     variant.Slots.Select(s => (s.Id, s.Category, string.Join(",", s.InstallAfter))));
        Assert.Equal("m54.pistons.forged_hc", variant.StockParts["pistons"]);
        Assert.Equal(m54.StockParts.Where(kv => kv.Key != "pistons"), variant.StockParts.Where(kv => kv.Key != "pistons"));
        Assert.Equal((m54.Cylinders, m54.Layout, m54.StockTune), (variant.Cylinders, variant.Layout, variant.StockTune));
        // identity merges field by field: the variant keeps the family's kind, reference and features.
        Assert.Equal(("real", "M54B30 (forged pistons)", "bmw_m54_published_figures", m54.Identity!.Features.Count),
                     (variant.Identity!.Kind, variant.Identity.Variant, variant.Identity.Reference, variant.Identity.Features.Count));
        Assert.True(PeakTorque(db, "isar_m54_hc") > PeakTorque(db, TestContent.M54)); // it is the physics, through its parts
    }

    [Fact]
    public void AVariantThatRestatesNothingIsTheSameEngine()
    {
        var db = WithLayer("""{ "engines": [ { "id": "isar_m54_copy", "extends": "isar_m54", "name": "Copy" } ] }""");
        Assert.Equal(PeakTorque(db, TestContent.M54), PeakTorque(db, "isar_m54_copy"));
        // Architecture provenance is inherited because the variant restates no architecture field.
        Assert.Equal(db.GetEngine(TestContent.M54).Provenance, db.GetEngine("isar_m54_copy").Provenance);
    }

    [Fact]
    public void AnAbstractFamilyIsExtendedButNeverOffered()
    {
        var db = WithLayer("""
            { "engines": [
              { "id": "m54_family", "abstract": true, "extends": "isar_m54", "name": "M54 family",
                "stock_parts": {}, "identity": { "variant": null } },
              { "id": "m54_variant", "extends": "m54_family", "name": "M54 variant" } ] }
            """);
        Assert.False(db.Engines.ContainsKey("m54_family"));
        Assert.True(db.AbstractEngines.ContainsKey("m54_family"));
        Assert.True(db.Engines.ContainsKey("m54_variant")); // abstract is never inherited
        Assert.Equal(db.GetEngine(TestContent.M54).StockParts, db.GetEngine("m54_variant").StockParts);

        var bare = Layers(new[] { ("f.json", """
            { "engines": [ { "id": "family", "abstract": true, "cylinders": 4, "layout": "inline" } ] }
            """) });
        Assert.True(bare.Success, string.Join("\n", bare.Errors)); // a base needs no slots or build
        Assert.Empty(bare.Database.Engines);
    }

    [Theory]
    [InlineData("""{ "id": "v", "extends": "no_such_engine", "name": "V" }""", "unknown engine 'no_such_engine'")]
    [InlineData("""{ "id": "a", "extends": "b", "name": "A" }, { "id": "b", "extends": "a", "name": "B" }""", "cycle")]
    [InlineData("""{ "id": "v", "extends": "isar_m54", "name": "V", "stock_parts": { "pistons": "no.such.part" } }""", "stock_parts['pistons'] references unknown part")]
    [InlineData("""{ "id": "v", "extends": "isar_m54", "name": "V", "banks": [ { "id": "a", "cylinders": [1, 2, 3] } ] }""", "are in no bank")]
    [InlineData("""{ "id": "v", "extends": "isar_m54", "name": "V", "provenance": { "bore_mm": { "type": "estimated" } } }""", "not an engine architecture field")]
    public void ImpossibleVariantsAreRejected(string engines, string expected) =>
        Assert.Contains(ErrorsWithLayer($$"""{ "engines": [ {{engines}} ] }"""), e => e.Message.Contains(expected));

    [Fact]
    public void AModRedefiningAFamilyReachesItsVariants()
    {
        var variant = ("base/v.json", """{ "engines": [ { "id": "v", "extends": "isar_m54", "name": "V" } ] }""");
        var simpleMod = ("mods/m/e.json", File.ReadAllText(Path.Combine(TestContent.BaseContentPath, "engines", "isar_m54.json"))
            .Replace("\"stock_tune\": \"m54.stock\"", "\"stock_tune\": \"k20.stock\""));
        var r = Layers(BaseLayer().Append(variant).ToArray(), new[] { simpleMod });
        Assert.True(r.Success, string.Join("\n", r.Errors));
        Assert.Equal("k20.stock", r.Database.GetEngine("v").StockTune);
        Assert.Single(r.Overrides);
        Assert.Equal(TestContent.Database.Engines.Keys, r.Database.Engines.Keys.Take(2)); // the override keeps the load order
    }

    // ---- Part variants ---------------------------------------------------------------------------------------------

    [Fact]
    public void APartVariantIsItsParentUnderItsOwnIdAndName()
    {
        var db = TestContent.Matrix; // syn_v6_tt's right manifold extends the left one
        var left = db.GetPart("syn.v6tt.exhaust_manifold.left");
        var right = db.GetPart("syn.v6tt.exhaust_manifold.right");
        Assert.Equal(left.Id, right.Extends);
        Assert.Equal("V6TT turbo manifold (right)", right.Name);
        Assert.Equal(JsonSerializer.Serialize((object)left.Spec, ContentJson.Options), JsonSerializer.Serialize((object)right.Spec, ContentJson.Options));
        Assert.Equal((left.Category, left.Price, left.MassKg, left.Manufacturer), (right.Category, right.Price, right.MassKg, right.Manufacturer));
        Assert.Equal(left.Provides, right.Provides);
        Assert.Equal(left.Requires, right.Requires);
        Assert.Equal(left.AuthoredSpecFields, right.AuthoredSpecFields);
    }

    [Fact]
    public void APartVariantInheritsProvenanceOnlyForTheValuesItDoesNotChange()
    {
        var db = WithLayer($$"""
            { {{Sources}}, "parts": [
              {{Pump("""{ "*": { "type": "published", "source": "spec_sheet" } }""")}},
              { "id": "oil_pump.hv", "extends": "oil_pump.x", "name": "High-volume pump", "spec": { "displacement_cc_per_rev": 16 } },
              { "id": "oil_pump.hv2", "extends": "oil_pump.x", "name": "Measured pump", "mass_kg": 2.2,
                "spec": { "relief_pressure_kpa": 500 },
                "provenance": { "relief_pressure_kpa": { "type": "measured", "source": "spec_sheet" } } } ] }
            """);
        var hv = db.GetPart("oil_pump.hv");
        Assert.Equal(16, ((OilPumpSpec)hv.Spec).DisplacementCcPerRev);
        Assert.Equal(450, ((OilPumpSpec)hv.Spec).ReliefPressureKpa);
        Assert.False(hv.Provenance.ContainsKey("displacement_cc_per_rev")); // changed without a source: no record
        Assert.Equal(ProvenanceTypes.Published, hv.Provenance["relief_pressure_kpa"].Type);
        var hv2 = db.GetPart("oil_pump.hv2");
        Assert.Equal(ProvenanceTypes.Measured, hv2.Provenance["relief_pressure_kpa"].Type);
        Assert.False(hv2.Provenance.ContainsKey("mass_kg"));
        Assert.Equal(ProvenanceTypes.Published, hv2.Provenance["displacement_cc_per_rev"].Type);
    }

    [Theory]
    [InlineData("""{ "id": "p", "extends": "no.such.part", "name": "P" }""", "unknown part 'no.such.part'")]
    [InlineData("""{ "id": "p", "extends": "m54.oil_pump.oem" }""", "needs its own 'name'")]
    [InlineData("""{ "id": "p", "extends": "m54.oil_pump.oem", "name": "P", "category": "radiator" }""", "keeps its parent's category")]
    [InlineData("""{ "id": "p", "extends": "q", "name": "P" }, { "id": "q", "extends": "p", "name": "Q" }""", "cycle")]
    [InlineData("""{ "id": "p", "extends": "m54.oil_pump.oem", "name": "P", "spec": { "relief_pressure_kpa": -5 } }""", "relief_pressure_kpa")]
    [InlineData("""{ "id": "p", "extends": "m54.oil_pump.oem", "name": "P", "spec": { "bore_mm": 80 } }""", "bore_mm")]
    public void ImpossiblePartVariantsAreRejected(string parts, string expected) =>
        Assert.Contains(ErrorsWithLayer($$"""{ "parts": [ {{parts}} ] }"""), e => e.Message.Contains(expected));

    // ---- Backward compatibility and the physics boundary -------------------------------------------------------------

    [Fact]
    public void ContentWithoutTheNewFieldsLoadsAsBefore()
    {
        var r = Layers(new[] { ("old.json", $$"""{ "parts": [ { "id": "oil_pump.old", "name": "Pump", "category": "oil_pump", "spec": { "displacement_cc_per_rev": 12, "relief_pressure_kpa": 450 } } ] }""") });
        Assert.True(r.Success, string.Join("\n", r.Errors));
        var part = r.Database.GetPart("oil_pump.old");
        Assert.Empty(part.Provenance);
        Assert.Null(part.Extends);
        Assert.Empty(r.Database.Sources);
    }

    [Fact]
    public void TheSimulationNeverReadsIdentityProvenanceOrDeclaredFeatures()
    {
        // Metadata may be read by content loading, the capability summary's report, tools and the UI — never by the
        // physics, the ECU, damage or gameplay. (EngineAgnosticTests forbids identities; this forbids the metadata.)
        var forbidden = new Regex(@"\.(Identity|Provenance|AuthoredSpecFields|Extends)\b|\bFeatureReport\b|\bEngineFeatures\b|\bSourceDefinition\b");
        var dirs = new[] { "src/CarSim.Core/Simulation", "src/CarSim.Core/Ecu", "src/CarSim.Core/Damage", "src/CarSim.Core/Dyno",
                           "src/CarSim.Core/Vehicles", "src/CarSim.Gameplay" };
        var hits = dirs.SelectMany(d => Directory.EnumerateFiles(Path.Combine(TestContent.RepoRoot, d), "*.cs", SearchOption.AllDirectories))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => forbidden.IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(TestContent.RepoRoot, x.f)}:{x.i + 1}: {x.line.Trim()}").ToList();
        Assert.True(hits.Count == 0, string.Join("\n", hits));
    }
}
