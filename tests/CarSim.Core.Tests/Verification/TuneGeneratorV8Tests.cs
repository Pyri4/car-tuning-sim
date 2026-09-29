using CarSim.Core.Content;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;
using CarSim.Verification.Calibration;
using Keys = CarSim.Core.Engines.EngineDerivedValues.Keys;

namespace CarSim.Core.Tests.Verification;

/// <summary>
/// The synthetic pushrod V8's baseline tune, generated once for the classes below (≈ 1.5 CPU-minutes: the recipe
/// ve → spark → ve until it settles). The same generation the CLI runs as
/// <c>carsim generate-tune syn_v8_ohv --fuel gasoline_95 --mods content/test</c>.
/// </summary>
public static class V8Generation
{
    public const string Engine = "syn_v8_ohv", Fuel = "gasoline_95";

    public static TuneGeneration Generate() =>
        TuneGenerator.Generate(GeneratorFixtures.Load(), new GenerationRequest(Engine, Fuel), GeneratorFixtures.Manifest);

    public static readonly Lazy<TuneGeneration> First = new(Generate);
}

/// <summary>
/// The Phase 3 demonstration: a multi-bank engine through the whole pipeline — V8 assembly and bank resolution, derived
/// values, check-engine, the explicit fuel, the manifest's recipe on the existing calibrators, a settled and validated tune
/// in the existing format — with nothing in the generator knowing the engine.
/// </summary>
public class TuneGeneratorV8Tests
{
    private static TuneGeneration G => V8Generation.First.Value;

    [Fact]
    public void TheSyntheticV8GetsAValidBaselineTune()
    {
        var g = G;
        Assert.False(g.Refused, g.ToText());
        var p = g.Plan!;
        var tune = g.Tune!;

        // V8 assembly and banks: two banks of four, a head and gasket per bank, one pushrod camshaft for both.
        Assert.Equal(("v", 8, 2), (p.Capabilities.Layout, p.Capabilities.Cylinders, p.Capabilities.Banks));
        Assert.Equal(new[] { 1, 3, 5, 7 }, p.Engine.Banks[0].Cylinders);
        Assert.NotEqual(p.Assembly.SlotFor(PartCategory.CylinderHead, 0)!.Id, p.Assembly.SlotFor(PartCategory.CylinderHead, 1)!.Id);
        Assert.Equal(p.Assembly.SlotFor(PartCategory.Camshafts, 0)!.Id, p.Assembly.SlotFor(PartCategory.Camshafts, 1)!.Id);

        // Derived values per bank, and the displacement belief set from them.
        foreach (int bank in new[] { 0, 1 })
        {
            Assert.Equal(DerivedValueStatus.Derived, p.Derived.Find(Keys.CompressionRatio, bank)!.Status);
            Assert.Equal(DerivedValueStatus.Derived, p.Derived.Find(Keys.RunnerTunedSpeed, bank, 0)!.Status);
        }
        Assert.Equal(p.Derived.DisplacementCc, tune.DisplacementCc);
        Assert.Equal(0.748, tune.FuelDensityKgL);

        // The manifest's recipe on the requested fuel, settled; the hand-authored policy unchanged.
        Assert.Equal(new[] { "ve", "spark", "ve" }, p.Recipe.Steps.Select(s => s.Calibrator));
        Assert.Equal(V8Generation.Fuel, p.Recipe.Fuel);
        Assert.InRange(g.Iterations, 1, TuneGenerator.MaxIterations);
        Assert.True(g.Settled!.Reproduced);
        Assert.Equal(p.Policy.TargetLambda, tune.TargetLambda);
        Assert.Equal((p.Policy.RevLimitRpm, p.Policy.IdleRpm, p.Policy.RpmAxis, p.Policy.LoadAxisKpa),
            (tune.RevLimitRpm, tune.IdleRpm, tune.RpmAxis, tune.LoadAxisKpa));

        // Valid: the tune format, no validation error, the file loads back to exactly this tune.
        Assert.Empty(tune.Validate());
        Assert.DoesNotContain(g.Validation, f => f.Severity == IssueSeverity.Error);
        var back = ContentLoader.LoadFromStrings(new[] { ("generated.json", g.Text!) });
        Assert.Empty(TuneGenerator.DifferingFields(tune, back.Database.GetTune(tune.Id)));
        Assert.Contains("RESULT  GENERATED", g.ToText());
    }

    [Fact]
    public void TheGeneratedTunePassesCheckEngineAsTheEnginesStockTune()
    {
        // Dropped into a content layer after the matrix, it replaces the stock tune of the same id.
        var load = GeneratorFixtures.Load(G.Text!);
        Assert.True(load.Success, string.Join("\n", load.Errors));
        Assert.Contains(load.Overrides, o => o.Contains("syn.v8.stock"));
        var report = EngineCheck.Run(load, V8Generation.Engine);
        Assert.True(report.Passed, report.ToText(verbose: true));
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("tune_", StringComparison.Ordinal) && f.Severity != IssueSeverity.Info);
        Assert.Equal(DerivedValueStatus.Validated, report.Derived!.Find("tune_displacement_cc")!.Status);
    }

    [Fact]
    public void TheGeneratedFuelMapHoldsTheTargetLambda()
    {
        // The speed-density fuel map was measured on this build and fuel: at full load the engine runs on its λ target.
        var db = GeneratorFixtures.Load().Database;
        var tune = EcuTune.FromDocument(G.Tune!);
        var assembly = EngineAssembly.CreateStock(db.GetEngine(V8Generation.Engine), db, new PartInstanceFactory());
        var config = EngineConfiguration.Build(assembly, db.GetFuel(V8Generation.Fuel), new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa)).GetOrThrow();
        var sim = new EngineSimulation(config, tune, EngineState.Warm());
        foreach (double rpm in new[] { 2000.0, 4000.0, 5500.0 })
        {
            var t = SteadyStateSweep.Settle(sim, rpm, 1.0, 1.0);
            Assert.True(Math.Abs(t.Lambda / t.TargetLambda - 1) < 0.02, $"{rpm} rpm: λ {t.Lambda:F3} against the target {t.TargetLambda:F3}");
            Assert.True(t.Firing && t.Torque > 0);
        }
    }

    [Fact]
    public void WritingItTwiceChangesNothing()
    {
        string dir = GeneratorFixtures.TempDirectory();
        try
        {
            string path = Path.Combine(dir, "syn_v8_ohv.gasoline_95.json");
            Assert.StartsWith("WRITTEN", TuneGenerator.Write(G, path).Message);
            string tune = File.ReadAllText(path), record = File.ReadAllText(TuneGenerator.RecordPathFor(path));
            Assert.StartsWith("UNCHANGED", TuneGenerator.Write(G, path).Message);
            Assert.Equal(0, TuneGenerator.Write(G, path, check: true).ExitCode);
            Assert.Equal(tune, File.ReadAllText(path));
            Assert.Equal(record, File.ReadAllText(TuneGenerator.RecordPathFor(path)));
            var recipe = Assert.Single(CarSim.Verification.Calibration.TuneManifest.Load(TuneGenerator.RecordPathFor(path)).Tunes);
            Assert.Equal(G.Iterations, recipe.Generated!.Iterations);
            Assert.Equal(TuneGenerator.Sha256(tune), recipe.Generated.OutputSha256);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>
/// The regeneration guarantee, checked independently of the generator's own last pass: the recipe run again on the
/// generated tune (<see cref="TuneRegenerator.Settle"/>, what <c>carsim regenerate-tunes</c> runs) gives it back.
/// </summary>
public class TuneGeneratorV8RegenerationTests
{
    [Fact]
    public void RegeneratingTheGeneratedTuneReproducesIt()
    {
        var g = V8Generation.First.Value;
        var db = GeneratorFixtures.Load(g.Text!).Database;
        var regenerated = TuneRegenerator.Settle(db, g.Plan!.Recipe, db.GetTune(g.Tune!.Id));
        Assert.True(regenerated.Reproduced, string.Join("; ", regenerated.Fields.Select(f => $"{f.Field}: {f.Differing} differ")));
        Assert.Empty(TuneGenerator.DifferingFields(g.Tune, regenerated.Document));
    }
}

/// <summary>Determinism: a second, independent generation gives the same bytes, record and report.</summary>
public class TuneGeneratorV8DeterminismTests
{
    [Fact]
    public void GeneratingTwiceGivesTheSameBytes()
    {
        var second = V8Generation.Generate();
        var first = V8Generation.First.Value;
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(TuneGenerator.WriteRecord(first, "v8.json"), TuneGenerator.WriteRecord(second, "v8.json"));
        Assert.Equal(first.ToText(verbose: true), second.ToText(verbose: true));
        Assert.Equal(first.Iterations, second.Iterations);
    }
}
