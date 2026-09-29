using CarSim.Core.Content;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Verification.Calibration;

namespace CarSim.Core.Tests.Verification;

/// <summary>
/// <c>carsim generate-tune</c> (Engine Authoring Factory 1.0, Phase 3) up to the calibration: the check gate, the explicit
/// fuel, the resolved build, the derived beliefs, the recipe, validation, the writer and its overwrite policy. These run no
/// calibrator (fast); <see cref="TuneGeneratorV8Tests"/> runs the whole pipeline on the synthetic V8.
/// </summary>
public class TuneGeneratorTests
{
    private static TuneGeneration Plan(string engineId, string? fuel, string? policy = null, bool strict = false, params string[] layers) =>
        TuneGenerator.Plan(GeneratorFixtures.Load(layers), new GenerationRequest(engineId, fuel, policy, Strict: strict), GeneratorFixtures.Manifest);

    private static TunePlan Planned(string engineId, string fuel, params string[] layers)
    {
        var g = Plan(engineId, fuel, layers: layers);
        Assert.False(g.Refused, g.ToText());
        return g.Plan!;
    }

    // ---- The pipeline's gates ----------------------------------------------------------------------------------------

    [Fact]
    public void AnEngineThatFailsCheckEngineIsRefused()
    {
        var g = Plan("fixture_v8_bad_pistons", "gasoline_95", layers: GeneratorFixtures.Variants);
        Assert.Equal("check_failed", Assert.Single(g.Refusals).Code);
        Assert.Contains(g.Check!.Errors, e => e.Code == "piston_bore_mismatch");
        Assert.Null(g.Plan);
        Assert.Contains("piston_bore_mismatch", g.ToText());
        Assert.Contains("RESULT  REFUSED", g.ToText());
    }

    [Fact]
    public void AnEngineThatDoesNotResolveIsRefused()
    {
        var g = Plan("no_such_engine", "gasoline_95");
        Assert.True(g.Has("check_failed"));
        Assert.True(g.Check!.Has("unknown_engine"));
        var orphan = Plan("fixture_orphan", "gasoline_95", layers: """
            { "engines": [ { "id": "fixture_orphan", "extends": "no_such_family", "name": "Orphan variant" } ] }
            """);
        Assert.True(orphan.Has("check_failed"));
        Assert.True(orphan.Check!.Has("content_error"));
    }

    [Fact]
    public void StockPartsThatDoNotResolveAreRefused()
    {
        var g = Plan("fixture_v8_ghost", "gasoline_95", layers: """
            { "engines": [ { "id": "fixture_v8_ghost", "extends": "syn_v8_ohv", "name": "Ghost", "stock_parts": { "crankshaft": "no.such.crank" } } ] }
            """);
        Assert.True(g.Has("check_failed"));
        Assert.True(g.Check!.Has("content_error"));
    }

    [Fact]
    public void AnAbstractFamilyBaseIsRefused()
    {
        var g = Plan("fixture_v8_family", "gasoline_95", layers: """
            { "engines": [ { "id": "fixture_v8_family", "abstract": true, "extends": "syn_v8_ohv", "name": "Family base" } ] }
            """);
        Assert.True(g.Has("check_failed"));
        Assert.True(g.Check!.Has("abstract_engine"));
    }

    [Fact]
    public void StrictRefusesAnEngineWithWarnings()
    {
        Assert.False(Plan("fixture_i4t_b58_like", "gasoline_98", layers: GeneratorFixtures.Variants).Refused);
        var strict = Plan("fixture_i4t_b58_like", "gasoline_98", strict: true, layers: GeneratorFixtures.Variants);
        Assert.Equal("check_warnings", Assert.Single(strict.Refusals).Code);
    }

    [Fact]
    public void TheFuelMustBeExplicitAndKnown()
    {
        Assert.Equal("fuel_required", Assert.Single(Plan("syn_v8_ohv", null).Refusals).Code);
        Assert.Equal("fuel_required", Assert.Single(Plan("syn_v8_ohv", " ").Refusals).Code);
        var unknown = Plan("syn_v8_ohv", "diesel");
        Assert.Equal("unknown_fuel", Assert.Single(unknown.Refusals).Code);
        Assert.Contains("gasoline_95", unknown.Refusals[0].Message); // says what is loaded
    }

    [Fact]
    public void AnEngineWithoutAPolicyTuneIsNotGeneratable()
    {
        var g = Plan("fixture_v8_untuned", "gasoline_95", layers: GeneratorFixtures.Variants);
        var refusal = Assert.Single(g.Refusals);
        Assert.Equal("no_policy_tune", refusal.Code);
        Assert.StartsWith("NOT GENERATABLE", refusal.Message);
        Assert.Contains("target_lambda", refusal.Message);
        // Naming a policy makes it generatable: the policy is a tune, not a guess.
        Assert.False(Plan("fixture_v8_untuned", "gasoline_95", "syn.v8.stock", layers: GeneratorFixtures.Variants).Refused);
        Assert.Equal("unknown_policy_tune", Assert.Single(Plan("syn_v8_ohv", "gasoline_95", "no.such.tune").Refusals).Code);
    }

    [Fact]
    public void APolicyTheCalibratorsCannotRunIsRefusedByValidation()
    {
        // A rev limit above the ECU's ceiling: the ECU would cut fuel below it, so the columns in between would be measured
        // on the limiter. Refused before any calibrator runs.
        string layer = GeneratorFixtures.TuneCopy("content/test/engine-matrix/syn_v8_ohv.json", "syn.v8.stock", "fixture.v8.high_limit",
            t => t["rev_limit_rpm"] = 8500);
        var g = Plan("syn_v8_ohv", "gasoline_95", "fixture.v8.high_limit", layers: layer);
        Assert.Equal("calibration_rev_limit_above_ecu", Assert.Single(g.Refusals).Code);
        Assert.NotNull(g.Plan); // planned, then refused by validation
        Assert.Contains("8000", g.Refusals[0].Message);
    }

    // ---- The resolved build and the beliefs --------------------------------------------------------------------------

    [Fact]
    public void GenerationUsesTheResolvedStockAssembly()
    {
        var db = GeneratorFixtures.Load().Database;
        var p = Planned("syn_v8_ohv", "gasoline_95");
        var engine = db.GetEngine("syn_v8_ohv");
        Assert.Equal(engine.StockParts.OrderBy(kv => kv.Key, StringComparer.Ordinal),
            p.Assembly.Installed.Select(kv => KeyValuePair.Create(kv.Key, kv.Value.Definition.Id)).OrderBy(kv => kv.Key, StringComparer.Ordinal));
        Assert.Equal(2, p.Capabilities.Banks);
        Assert.NotEqual(p.Assembly.SlotFor(PartCategory.CylinderHead, 0)!.Id, p.Assembly.SlotFor(PartCategory.CylinderHead, 1)!.Id);
        // The calibrators build the same engine from the recipe.
        var calibration = TuneRegenerator.BuildAssembly(db, p.Recipe);
        Assert.Equal(p.Assembly.Installed.ToDictionary(kv => kv.Key, kv => kv.Value.Definition.Id),
            calibration.Installed.ToDictionary(kv => kv.Key, kv => kv.Value.Definition.Id));
    }

    [Fact]
    public void TheBeliefsComeFromTheBuildAndTheFuel()
    {
        var p = Planned("syn_v8_ohv", "gasoline_95");
        var injectors = p.Assembly.SpecOf<InjectorSpec>(PartCategory.Injectors)!;
        Assert.Equal(p.Derived.DisplacementCc, p.Skeleton.DisplacementCc);
        var independent = EngineAssembly.CreateStock(TestContent.Matrix.GetEngine("syn_v8_ohv"), TestContent.Matrix, new PartInstanceFactory());
        Assert.Equal(EngineDerivedValues.Of(independent).DisplacementCc, p.Skeleton.DisplacementCc);
        Assert.Equal(injectors.FlowCcMin, p.Skeleton.InjectorFlowCcMin);
        Assert.Equal(injectors.DeadTimeMs, p.Skeleton.InjectorDeadTimeMs);
        Assert.Equal(0.748, p.Skeleton.FuelDensityKgL);   // gasoline_95's, not the policy tune's hand-entered 0.745
        Assert.Equal(0.745, p.Policy.FuelDensityKgL);
        Assert.Equal(14.7, p.Skeleton.FuelStoichAfr);
        Assert.All(p.Fields.Where(f => f.Field is "displacement_cc" or "injector_flow_cc_min" or "injector_dead_time_ms" or "fuel_stoich_afr" or "fuel_density_kg_l"),
            f => Assert.Equal(FieldOrigins.Derived, f.Origin));
    }

    [Fact]
    public void TheSelectedFuelSetsTheFuelBeliefsAndTheCalibrationFuel()
    {
        var e85 = Planned("syn_v8_ohv", "e85");
        Assert.Equal(("e85", 9.8, 0.781), (e85.Recipe.Fuel, e85.Skeleton.FuelStoichAfr, e85.Skeleton.FuelDensityKgL!.Value));
        // The manifest calibrates the M54 on RON 98; asked for RON 95, the generator calibrates on RON 95.
        var m54 = Planned("isar_m54", "gasoline_95");
        Assert.Equal("gasoline_98", GeneratorFixtures.Manifest.Tunes.Single(t => t.Tune == "m54.stock").Fuel);
        Assert.Equal(("gasoline_95", 0.748), (m54.Recipe.Fuel, m54.Skeleton.FuelDensityKgL!.Value));
        Assert.Contains("gasoline_95", m54.Recipe.Provenance);
    }

    [Fact]
    public void AnEngineVariantOnPartVariantsIsGeneratedFromItsResolvedParts()
    {
        var p = Planned("fixture_v8_stroker", "gasoline_95", GeneratorFixtures.Variants);
        Assert.Equal("fixture.v8.crankshaft.stroker", p.Assembly.PartIn("crankshaft")!.Definition.Id);
        Assert.Equal("fixture.v8.injectors.320cc", p.Assembly.PartIn("injectors")!.Definition.Id);
        // The stroke of the crank variant, not its parent's: π/4 · 94² · 75 mm × 8.
        Assert.Equal(Math.PI / 4 * 9.4 * 9.4 * 7.5 * 8, p.Skeleton.DisplacementCc!.Value, 9);
        Assert.Equal((320.0, 0.8), (p.Skeleton.InjectorFlowCcMin, p.Skeleton.InjectorDeadTimeMs!.Value));
        // The variant inherits the parent's stock tune as its policy, not the parent's recipe: that one calibrates the parent.
        Assert.Equal("syn.v8.stock", p.Policy.Id);
        Assert.StartsWith("derived from the hardware", p.RecipeSource);
        Assert.Equal(new[] { "ve", "spark", "ve" }, p.Recipe.Steps.Select(s => s.Calibrator));
        Assert.Equal("fixture_v8_stroker", p.Recipe.Engine);
        // Validation found the beliefs to be the build's (the parent's 3941.8 cc would be refused).
        Assert.DoesNotContain(TuneGenerator.Validate(p, p.Skeleton), f => f.Severity == IssueSeverity.Error);
        Assert.Contains(TuneGenerator.Validate(p, p.Skeleton with { DisplacementCc = 3941.8 }), f => f.Code == "belief_not_derived");
    }

    [Theory]
    [InlineData("displacement_cc")]
    [InlineData("injector_flow_cc_min")]
    [InlineData("injector_dead_time_ms")]
    [InlineData("fuel_stoich_afr")]
    [InlineData("fuel_density_kg_l")]
    public void ValidationRefusesABeliefThatIsNotTheBuildsOrTheFuels(string field)
    {
        var p = Planned("syn_v8_ohv", "gasoline_95");
        var s = p.Skeleton;
        var wrong = field switch
        {
            "displacement_cc" => s with { DisplacementCc = s.DisplacementCc + 1 },
            "injector_flow_cc_min" => s with { InjectorFlowCcMin = s.InjectorFlowCcMin + 10 },
            "injector_dead_time_ms" => s with { InjectorDeadTimeMs = s.InjectorDeadTimeMs + 0.1 },
            "fuel_stoich_afr" => s with { FuelStoichAfr = 14.6 },
            _ => s with { FuelDensityKgL = 0.745 },
        };
        var errors = TuneGenerator.Validate(p, wrong).Where(f => f.Severity == IssueSeverity.Error).ToList();
        Assert.Equal("belief_not_derived", Assert.Single(errors).Code);
        Assert.StartsWith(field, errors[0].Message);
    }

    // ---- Recipes -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheManifestsRecipeForTheEnginesStockBuildIsUsedUnchanged()
    {
        var p = Planned("isar_m54", "gasoline_98");
        var manifest = GeneratorFixtures.Manifest.Tunes.Single(t => t.Tune == "m54.stock");
        Assert.Equal(manifest.Steps, p.Recipe.Steps);          // including the 2.5° cam step
        Assert.Equal(manifest.HandAuthored, p.Recipe.HandAuthored);
        Assert.StartsWith("the tune manifest's recipe for m54.stock", p.RecipeSource);
        // A hand-authored table stays the policy's: the K20's factory spark map is never recalibrated.
        var k20 = Planned("kestrel_k20", "gasoline_95");
        Assert.Equal(new[] { "ve" }, k20.Recipe.Steps.Select(s => s.Calibrator));
        Assert.Equal(FieldOrigins.HandAuthored, k20.Fields.Single(f => f.Field == "ignition_advance_deg").Origin);
        Assert.Contains(k20.Notes, n => n.Subject == "ignition_advance_deg" && n.Status == "NOT GENERATABLE");
    }

    [Fact]
    public void TheRecipeDerivedFromTheHardwareIsTheRuleEveryManifestRecipeFollows()
    {
        // For every recipe of a stock build, the rule gives the same steps from the build's capabilities and the recipe's
        // hand-authored tables (the manifest's step parameters, like the M54's 2.5° cam step, are its own).
        var db = GeneratorFixtures.Load().Database;
        foreach (var recipe in GeneratorFixtures.Manifest.Tunes.Where(r => (r.Build?.Swap?.Count ?? 0) + (r.Build?.Add?.Count ?? 0) == 0))
        {
            var caps = EngineCapabilities.Resolve(EngineAssembly.CreateStock(db.GetEngine(recipe.Engine), db, new PartInstanceFactory()));
            Assert.True(recipe.Steps.Select(s => s.Calibrator).SequenceEqual(TuneGenerator.DeriveSteps(caps, recipe.HandAuthored.ToList()).Select(s => s.Calibrator)),
                $"{recipe.Tune}: manifest {string.Join(" → ", recipe.Steps.Select(s => s.Calibrator))}, derived " +
                string.Join(" → ", TuneGenerator.DeriveSteps(caps, recipe.HandAuthored.ToList()).Select(s => s.Calibrator)));
        }
    }

    [Fact]
    public void TheDerivedRecipePutsTheHardwareScheduleBeforeTheMaps()
    {
        EngineCapabilities Caps(bool phasing = false, bool runner = false, bool lift = false) => new()
        {
            Layout = "inline", Cylinders = 4, Banks = 1, Valvetrains = new[] { "dohc" }, ValvesPerCylinder = new[] { 4 },
            IntakeCamPhasers = phasing, IntakeCamPhasing = phasing, SwitchedRunners = runner, VariableIntakeRunner = runner,
            VariableLiftCams = lift, VariableValveLift = lift,
        };
        string Steps(EngineCapabilities c, params string[] hand) => string.Join(" ", TuneGenerator.DeriveSteps(c, hand).Select(s => s.Calibrator));
        Assert.Equal("ve spark ve", Steps(Caps(), "target_lambda"));
        Assert.Equal("cams runner_switch lift_switch ve spark ve", Steps(Caps(true, true, true), "target_lambda"));
        Assert.Equal("cams ve", Steps(Caps(phasing: true), "target_lambda", "ignition_advance_deg"));
        Assert.Equal("lift_switch spark", Steps(Caps(lift: true), "target_lambda", "volumetric_efficiency"));
        Assert.Equal("ve spark ve", Steps(Caps(phasing: true), "target_lambda", "intake_cam_advance_deg"));
    }

    [Fact]
    public void HardwareTheEcuCannotDriveIsNotGeneratable()
    {
        var p = Planned("fixture_v8d_basic_ecu", "gasoline_98", GeneratorFixtures.Variants);
        Assert.True(p.Capabilities.IntakeCamPhasers);
        Assert.False(p.Capabilities.IntakeCamPhasing);
        Assert.DoesNotContain(p.Recipe.Steps, s => s.Calibrator == "cams");
        Assert.Contains(p.Notes, n => n.Subject == "intake_cam_advance_deg" && n.Status == "NOT GENERATABLE" && n.Reason.Contains("cam_phaser_uncontrolled"));
    }

    [Fact]
    public void DeclaredHardwareTheSimulatorDoesNotModelIsReportedNotModelled()
    {
        var g = Plan("fixture_i4t_b58_like", "gasoline_98", layers: GeneratorFixtures.Variants);
        Assert.False(g.Refused, g.ToText());
        var notModelled = g.Plan!.Notes.Where(n => n.Status == "NOT MODELLED").Select(n => n.Subject).OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(new[] { "direct_injection", "exhaust_cam_phasing" }, notModelled);
        Assert.Contains("stands in: port injection", g.ToText());
        Assert.Contains(g.Plan.Notes, n => n.Subject == "boost_target_kpa" && n.Status == "NOT GENERATABLE");
        Assert.Equal(new[] { "cams", "ve", "spark", "ve" }, g.Plan.Recipe.Steps.Select(s => s.Calibrator));
    }

    // ---- Determinism, the writer and the overwrite policy -------------------------------------------------------------

    [Fact]
    public void PlanningIsDeterministic()
    {
        foreach (var (engine, fuel) in new[] { ("syn_v8_ohv", "gasoline_95"), ("isar_m54", "gasoline_98"), ("syn_i6_vis", "e85") })
        {
            var a = Plan(engine, fuel);
            var b = Plan(engine, fuel);
            Assert.Equal(a.ToText(verbose: true), b.ToText(verbose: true));
            Assert.Equal(TuneGenerator.WriteTuneFile(a.Plan!.Skeleton, a.Plan.CalibratedFields), TuneGenerator.WriteTuneFile(b.Plan!.Skeleton, b.Plan.CalibratedFields));
            Assert.Equal(TuneGenerator.WriteRecord(GeneratorFixtures.Uncalibrated(a), "x.json"), TuneGenerator.WriteRecord(GeneratorFixtures.Uncalibrated(b), "x.json"));
        }
    }

    [Fact]
    public void EveryTuneLoadsBackFromTheWriterUnchanged()
    {
        // The writer states every field a tune has (a field it forgot would load back as its default and fail here).
        foreach (var tune in TestContent.Matrix.Tunes.Values)
        {
            string text = TuneGenerator.WriteTuneFile(tune, new[] { "volumetric_efficiency" });
            var back = ContentLoader.LoadFromStrings(new[] { ("t.json", text) });
            Assert.True(back.Success, string.Join("\n", back.Errors));
            Assert.Empty(TuneGenerator.DifferingFields(tune, back.Database.GetTune(tune.Id)));
        }
    }

    [Fact]
    public void TheRecordSaysHowTheTuneWasMadeAndCarriesNoTimeOrPath()
    {
        var g = GeneratorFixtures.Uncalibrated(Plan("fixture_v8_stroker", "gasoline_95", layers: GeneratorFixtures.Variants));
        string record = TuneGenerator.WriteRecord(g, "stroker.json");
        var recipe = Assert.Single(CarSim.Verification.Calibration.TuneManifest.Load(WriteTemp(record)).Tunes);
        Assert.Equal(("syn.v8.stock", "fixture_v8_stroker", "gasoline_95", "stroker.json"), (recipe.Tune, recipe.Engine, recipe.Fuel, recipe.File));
        var gen = recipe.Generated!;
        Assert.Equal((TuneGenerator.Name, TuneGenerator.Version, "syn.v8.stock"), (gen.Generator, gen.Version, gen.PolicyTune));
        Assert.Contains(gen.Assembly, pair => pair[0] == "crankshaft" && pair[1] == "fixture.v8.crankshaft.stroker");
        Assert.Contains(gen.Fields, f => f.Field == "displacement_cc" && f.Origin == FieldOrigins.Derived);
        Assert.Contains(gen.Fields, f => f.Field == "target_lambda" && f.Origin == FieldOrigins.HandAuthored);
        Assert.Contains(gen.Fields, f => f.Field == "volumetric_efficiency" && f.Origin == FieldOrigins.Calibrated);
        Assert.Equal(TuneGenerator.Sha256(g.Text!), gen.OutputSha256);
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), record);
        Assert.DoesNotMatch(@"20\d\d-\d\d-\d\d", record);
    }

    private static string WriteTemp(string text)
    {
        string path = Path.Combine(GeneratorFixtures.TempDirectory(), "record.recipe.jsonc");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void AGeneratedTuneIsWrittenOnceAndRegeneratedOnlyWhileUntouched()
    {
        string dir = GeneratorFixtures.TempDirectory();
        try
        {
            var g = GeneratorFixtures.Uncalibrated(Plan("syn_v8_ohv", "gasoline_95"));
            string path = Path.Combine(dir, "v8.json"), record = TuneGenerator.RecordPathFor(path);
            Assert.Equal(0, TuneGenerator.Write(g, path).ExitCode);
            Assert.Equal(g.Text, File.ReadAllText(path));
            Assert.True(File.Exists(record));
            Assert.EndsWith("v8.recipe.jsonc", record);

            // The same output again: nothing changes; checking it passes.
            Assert.StartsWith("UNCHANGED", TuneGenerator.Write(g, path).Message);
            Assert.Equal(0, TuneGenerator.Write(g, path, check: true).ExitCode);

            // Another fuel: an untouched generator output may be regenerated; --check reports the difference first.
            var e85 = GeneratorFixtures.Uncalibrated(Plan("syn_v8_ohv", "e85"));
            Assert.Equal(4, TuneGenerator.Write(e85, path, check: true).ExitCode);
            Assert.Equal(g.Text, File.ReadAllText(path));
            Assert.StartsWith("REGENERATED", TuneGenerator.Write(e85, path).Message);
            Assert.Equal(e85.Text, File.ReadAllText(path));

            // Edited by hand after generation: it is hand-authored now, and never overwritten.
            File.WriteAllText(path, e85.Text!.Replace("\"idle_rpm\": 700", "\"idle_rpm\": 750"));
            string edited = File.ReadAllText(path);
            var refused = TuneGenerator.Write(g, path);
            Assert.Equal(2, refused.ExitCode);
            Assert.Contains("edited after it was generated", refused.Message);
            Assert.Equal(edited, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("content/base/tunes/k20_stock.json")]
    [InlineData("content/test/engine-matrix/syn_v8_ohv.json")] // a tune inside an engine's content file
    public void AHandAuthoredTuneIsNeverOverwritten(string file)
    {
        string dir = GeneratorFixtures.TempDirectory();
        try
        {
            // A copy of the hand-authored file (the real one is never put at risk, even by a mutant).
            string path = Path.Combine(dir, Path.GetFileName(file));
            File.Copy(Path.Combine(TestContent.RepoRoot, file), path);
            string before = File.ReadAllText(path);
            var g = GeneratorFixtures.Uncalibrated(Plan("syn_v8_ohv", "gasoline_95"));
            foreach (bool check in new[] { false, true })
            {
                var outcome = TuneGenerator.Write(g, path, check);
                Assert.Equal(2, outcome.ExitCode);
                Assert.Contains("is not a generator output", outcome.Message);
            }
            Assert.Equal(before, File.ReadAllText(path));
            Assert.False(File.Exists(TuneGenerator.RecordPathFor(path)));

            // A record beside a tune it does not describe does not make the tune the generator's either.
            File.WriteAllText(TuneGenerator.RecordPathFor(path), TuneGenerator.WriteRecord(g, "other.json"));
            Assert.Equal(2, TuneGenerator.Write(g, path).ExitCode);
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OnlyARefusedOrMisnamedOutputIsNotWritten()
    {
        string dir = GeneratorFixtures.TempDirectory();
        try
        {
            var refused = Plan("syn_v8_ohv", null);
            Assert.Equal(2, TuneGenerator.Write(refused, Path.Combine(dir, "a.json")).ExitCode);
            var g = GeneratorFixtures.Uncalibrated(Plan("syn_v8_ohv", "gasoline_95"));
            Assert.Equal(1, TuneGenerator.Write(g, Path.Combine(dir, "a.jsonc")).ExitCode);
            Assert.Equal(2, TuneGenerator.Write(g, Path.Combine(dir, "b.json"), check: true).ExitCode); // nothing to check
            Assert.Empty(Directory.EnumerateFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
