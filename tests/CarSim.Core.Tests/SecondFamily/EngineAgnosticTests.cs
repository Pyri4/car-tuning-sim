using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CarSim.Core.Common;
using CarSim.Core.Content;
using CarSim.Core.Damage;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;
using CarSim.Core.Vehicles;
using CarSim.Gameplay;

namespace CarSim.Core.Tests.SecondFamily;

/// <summary>
/// The architecture test of the second-engine milestone: both families go through the same generic systems, the
/// simulation reacts to engine data and never to an engine's identity, and the first family did not change.
/// </summary>
public class EngineAgnosticTests
{
    public static IEnumerable<object[]> Families => new[]
    {
        new object[] { TestContent.K20, "project_car" },
        new object[] { TestContent.M54, "isar_c30_six" },
    };

    // ---- Validation matrix: build → validate → start → idle → dyno → full-load pull → ECU tuning → save/load →
    //      damage/failure → drive, through the same garage, ECU, dyno, damage, save and vehicle code ----------------

    [Theory]
    [MemberData(nameof(Families))]
    public void EveryFamilyRunsThroughTheSameGameplayPipeline(string engineId, string scenario)
    {
        var g = Garage.NewGame(TestContent.Database, scenario);
        Assert.Equal(engineId, g.Engine.Definition.Id);
        Assert.Equal(scenario, g.ScenarioId);

        // Validate.
        var report = g.Validate();
        Assert.True(report.CanRun, string.Join("\n", report.Errors));

        // Start and idle.
        var (sim, _) = g.CreateSimulation();
        var start = new EngineInputs { Throttle = 0, SpeedMode = SpeedMode.Free, Starter = true };
        EngineTelemetry t = null!;
        for (int i = 0; i < 2000; i++) { start.Starter = !sim!.State.Running && i < 600; t = sim.Step(0.005, start); }
        Assert.True(t.Running);
        Assert.InRange(t.Rpm, g.Tune.IdleRpm - 100, g.Tune.IdleRpm + 100);

        // Dyno sweep to the rev limiter.
        (sim, _) = g.CreateSimulation();
        var run = new DynoRunner(sim!, new DynoSettings { StartRpm = 2000, EndRpm = 9000 }).RunToCompletion();
        Assert.True(run.Completed, run.AbortReason);
        Assert.InRange(run.Samples.Max(s => s.Rpm), g.Tune.RevLimitRpm - 300, g.Tune.RevLimitRpm + 50);
        Assert.True(run.PeakPower!.PowerKw > 80);

        // Steady full-load pull.
        (sim, _) = g.CreateSimulation();
        var pull = SteadyStateSweep.Run(sim!, 2000, g.Tune.RevLimitRpm - 500, 1000, settleSeconds: 1.0);
        Assert.All(pull, p => Assert.True(p.Firing && p.Torque > 0));

        // ECU tuning: retarded full-load timing costs torque; a lower rev limit ends the pull earlier.
        g.Tune.OffsetIgnition(-8, 90);
        g.Tune.RevLimitRpm -= 1000;
        (sim, _) = g.CreateSimulation();
        Assert.True(SimFactory.At(sim!, 4000).Torque < pull.Single(p => Math.Abs(p.Rpm - 4000) < 0.01).Torque * 0.98);
        (sim, _) = g.CreateSimulation();
        var shortRun = new DynoRunner(sim!, new DynoSettings { StartRpm = 2000, EndRpm = 9000 }).RunToCompletion();
        Assert.InRange(shortRun.Samples.Max(s => s.Rpm), g.Tune.RevLimitRpm - 300, g.Tune.RevLimitRpm + 50);
        g.Tune.OffsetIgnition(8, 90);
        g.Tune.RevLimitRpm += 1000;

        // Save and load: the round trip is exact.
        string json = SaveSystem.Serialize(g);
        var loaded = SaveSystem.Deserialize(json, TestContent.Database);
        Assert.Equal(json, SaveSystem.Serialize(loaded));
        Assert.Equal(engineId, loaded.Engine.Definition.Id);

        // Drive: the car completes an autopilot lap of the test facility. Fresh rear tyres first: on the project car's
        // half-worn ones the autopilot spins at the chicane and voids its laps (a known autopilot limit, same on main).
        loaded.Chassis!.PartIn("tires_rear")!.Wear = 0.0;
        var (car, problem) = loaded.CreateVehicleSimulation();
        Assert.True(car != null, problem);
        var session = new DrivingSession(car!, TrackLayout.TestFacility()) { AutopilotEnabled = true };
        while (session.Timer.Laps < 1 && car!.State.Time < 200) session.Advance(0.1, default);
        Assert.True(session.Timer.Laps == 1, $"{car!.State.Time:F0} s, {car.Last?.SpeedKmh:F0} km/h, lap {session.Timer.CurrentLapTime(car.State.Time):F0} s, " +
            $"failures: {string.Join("; ", session.Failures.Select(f => f.Mode.ToString()))}");
        Assert.InRange(session.Timer.LastLap ?? 0, 45, 65);
        Assert.Empty(session.Failures);

        // Damage and failure: over-revved by the drivetrain past valve float, the valves meet the pistons; the report
        // explains it, the part is failed in the garage, and the car cannot be driven until it is repaired.
        (sim, _) = g.CreateSimulation();
        var overRev = new EngineInputs { Throttle = 0, SpeedMode = SpeedMode.Held, HeldRpm = sim!.ValveFloatRpm() * 1.08, CoolantTemperatureOverride = 363.15 };
        for (int i = 0; i < 6000 && sim.Damage.Failures.Count == 0; i++) sim.Step(0.005, overRev);
        var failure = Assert.Single(sim.Damage.Failures);
        Assert.Equal(FailureMode.ValvePistonContact, failure.Mode);
        Assert.NotEmpty(failure.Recommendations);
        Assert.Contains(g.FailedParts, p => p.Category == PartCategory.CylinderHead);
        Assert.NotEqual("", g.CreateVehicleSimulation().Problem);
    }

    [Fact]
    public void TheShopSaysWhichPartsFitWhichFamily()
    {
        var m54 = Garage.NewGame(TestContent.Database, "isar_c30_six");
        var k20 = Garage.NewGame(TestContent.Database, "project_car");
        var db = TestContent.Database;
        Assert.Empty(m54.FitProblems(db.GetPart("m54.cams.oem"), "camshafts"));
        Assert.Contains(m54.FitProblems(db.GetPart("k20.cams.race"), "camshafts"), p => p.Contains("k20.cam_carrier"));
        Assert.Contains(k20.FitProblems(db.GetPart("m54.cams.oem"), "camshafts"), p => p.Contains("m54.cam_carrier"));
        Assert.Contains(m54.FitProblems(db.GetPart("injectors.310cc"), "injectors"), p => p.Contains("set of 4"));
        Assert.Empty(m54.FitProblems(db.GetPart("injectors.6x230cc"), "injectors"));
        Assert.Empty(k20.FitProblems(db.GetPart("throttle.68mm"), "throttle_body")); // universal parts fit both
        Assert.Empty(m54.FitProblems(db.GetPart("ecu.standalone"), "ecu"));
        // The same rule the validator applies once the wrong part is in.
        var wrong = TestContent.StockM54();
        TestContent.Swap(wrong, "camshafts", "k20.cams.race");
        Assert.False(AssemblyValidator.Validate(wrong).CanRun);
    }

    // ---- Identity: the same data under other ids behaves identically -----------------------------------------

    private static IEnumerable<(string Source, string Json)> BaseDocuments() =>
        Directory.EnumerateFiles(TestContent.BaseContentPath, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetRelativePath(TestContent.BaseContentPath, f), File.ReadAllText(f)));

    /// <summary>The M54's own files (engine, engine parts, tune) under unrelated ids and names.</summary>
    private static List<(string Source, string Json)> RenamedM54() => BaseDocuments()
        .Where(d => d.Source.Contains("m54", StringComparison.Ordinal))
        .Select(d => ("clone/" + d.Source.Replace("m54", "zz", StringComparison.Ordinal),
            d.Json.Replace("isar_m54", "zz_family", StringComparison.Ordinal)
                  .Replace("\"m54.", "\"zz.", StringComparison.Ordinal)
                  .Replace("M54", "ZZ9", StringComparison.Ordinal)
                  .Replace("Isar", "Nord", StringComparison.Ordinal)))
        .ToList();

    private static EngineSimulation Build(ContentDatabase db, string engineId, string fuel = "gasoline_98")
    {
        var engine = db.GetEngine(engineId);
        var tune = EcuTune.FromDocument(db.GetTune(engine.StockTune));
        var a = EngineAssembly.CreateStock(engine, db, new PartInstanceFactory());
        var config = EngineConfiguration.Build(a, db.GetFuel(fuel), new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa));
        return new EngineSimulation(config.GetOrThrow(), tune, EngineState.Warm());
    }

    [Fact]
    public void TheSimulationCannotTellWhichEngineItIsRunning()
    {
        var clone = RenamedM54();
        Assert.Equal(5, clone.Count); // engine, three part files, tune
        // Only the shared universal parts it bolts on (ECU, radiator, fuel pump) keep their ids.
        Assert.DoesNotContain(clone, d => Regex.IsMatch(d.Json, @"isar_m54|""m54\.|M54"));
        var db = ContentLoader.LoadFromStrings(BaseDocuments().Concat(clone)).GetOrThrow();

        var original = SteadyStateSweep.Run(Build(db, TestContent.M54), 1000, 6250, 750, settleSeconds: 0.5);
        var renamed = SteadyStateSweep.Run(Build(db, "zz_family"), 1000, 6250, 750, settleSeconds: 0.5);
        Assert.Equal(original, renamed); // every telemetry channel, bit for bit

        var dynoA = new DynoRunner(Build(db, TestContent.M54), new DynoSettings()).RunToCompletion();
        var dynoB = new DynoRunner(Build(db, "zz_family"), new DynoSettings()).RunToCompletion();
        Assert.Equal(dynoA.Samples, dynoB.Samples);
    }

    // ---- Content-only variant: change the data, the physics follows ---------------------------------------------

    /// <summary>
    /// The renamed M54 turned by data alone into an 80 × 76 mm straight eight of ≈ 11:1 with a 7,500 rpm limit and a
    /// longer intake cam: nothing in the code knows it exists.
    /// </summary>
    private static ContentDatabase EightCylinderVariant()
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var docs = RenamedM54().Select(d => (d.Source, Node: JsonNode.Parse(d.Json, documentOptions: options)!)).ToList();
        JsonObject Item(string array, string id) => docs.SelectMany(d => d.Node[array]?.AsArray() ?? new JsonArray())
            .Single(n => (string?)n!["id"] == id)!.AsObject();
        JsonObject Spec(string id) => Item("parts", id)["spec"]!.AsObject();

        var engine = Item("engines", "zz_family");
        engine["cylinders"] = 8;
        engine["stock_parts"]!["injectors"] = "zz.injectors8";
        engine["stock_parts"]!["ecu"] = "ecu.standalone"; // the OEM ECU's hardware limit is 7,200 rpm
        Spec("zz.block.oem")["cylinders"] = 8;
        Spec("zz.block.oem")["bore_mm"] = 80.0;
        Spec("zz.block.oem")["deck_height_mm"] = 38.0 + 135.0 + 30.5 + 0.7;
        Spec("zz.crankshaft.oem")["stroke_mm"] = 76.0;
        Spec("zz.pistons.oem")["count"] = 8;
        Spec("zz.pistons.oem")["bore_mm"] = 80.0;
        Spec("zz.rods.oem")["count"] = 8;
        Spec("zz.head_gasket.oem")["bore_mm"] = 81.0;
        Spec("zz.head.oem")["chamber_volume_cc"] = 19.0;
        Spec("zz.cams.oem")["intake_duration_deg"] = 205.0;
        var tune = Item("tunes", "zz.stock");
        tune["rev_limit_rpm"] = 7500.0;
        tune["displacement_cc"] = 3057.0;
        var extra = """
            { "parts": [ { "id": "zz.injectors8", "name": "230 cc/min injectors (set of 8)", "category": "injectors", "mass_kg": 0.6,
              "spec": { "count": 8, "flow_cc_min": 230, "rated_pressure_kpa": 350, "max_duty": 0.85, "dead_time_ms": 0.85 } } ] }
            """;
        var clone = docs.Select(d => (d.Source, d.Node.ToJsonString())).Append(("clone/extra.json", extra));
        return ContentLoader.LoadFromStrings(BaseDocuments().Concat(clone)).GetOrThrow();
    }

    [Fact]
    public void AContentOnlyVariantIsSimulatedFromItsDataAlone()
    {
        var db = EightCylinderVariant();
        var variant = Build(db, "zz_family");
        var m54 = Build(db, TestContent.M54);

        // Geometry from the data: 8 × π/4 × 80² × 76 mm³, and the compression ratio from its four volumes.
        var g = variant.Config.Geometry;
        Assert.Equal(8, g.Cylinders);
        double swept = Math.PI / 4 * 8.0 * 8.0 * 7.6;
        Assert.Equal(8 * swept, Units.M3ToCc(g.Displacement), 6);
        double clearance = 19.0 + Math.PI / 4 * 8.1 * 8.1 * 0.07 + Math.PI / 4 * 8.0 * 8.0 * 0.07 + 12.1;
        Assert.Equal((swept + clearance) / clearance, g.CompressionRatio, 6);
        Assert.InRange(g.CompressionRatio, 10.8, 11.2);

        // Eight firings per two revolutions.
        var at3000 = SimFactory.At(variant, 3000);
        Assert.Equal(at3000.AirPerCycle * 8 * 3000 / 120.0, at3000.AirMassFlow, 1e-9);

        // Its own rev limit: it pulls at 7,300 rpm where the M54 is on its 6,500 rpm limiter, and cuts at 7,500.
        Assert.True(SimFactory.At(variant, 7300).Firing);
        Assert.False(SimFactory.At(Build(db, "zz_family"), 7600).Firing);
        Assert.False(SimFactory.At(m54, 7000).Firing);

        // Shorter stroke: lower mean piston speed, so less friction at the same speed.
        Assert.True(SimFactory.At(variant, 5000).Fmep < SimFactory.At(m54, 5000).Fmep);

        // Shorter stroke and longer intake cam move the breathing up the rev range: its peak power comes later.
        var variantCurve = SteadyStateSweep.Run(Build(db, "zz_family"), 2000, 7250, 250, settleSeconds: 0.5);
        var m54Curve = SteadyStateSweep.Run(Build(db, TestContent.M54), 2000, 6250, 250, settleSeconds: 0.5);
        Assert.True(SimFactory.PeakPower(variantCurve).Rpm > SimFactory.PeakPower(m54Curve).Rpm);
        // More displacement and speed: more power, from the same generic model.
        Assert.True(SimFactory.PeakPower(variantCurve).Power > SimFactory.PeakPower(m54Curve).Power);
        Assert.InRange(Units.PaToBar(variantCurve.Max(p => p.Bmep)), 10, 15);
    }

    // ---- Code audit ------------------------------------------------------------------------------------------

    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return string.Join("\n", code.Split('\n').Select(line =>
        {
            // Remove a // comment that is not inside a string literal.
            bool inString = false;
            for (int i = 0; i < line.Length - 1; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) inString = !inString;
                if (!inString && line[i] == '/' && line[i + 1] == '/') return line[..i];
            }
            return line;
        }));
    }

    [Fact]
    public void NoEngineIdentityOrEngineSpecificBranchInSimulationCode()
    {
        // Every content id of the base game and of the synthetic engine matrix (ENGINE_AUTHORING_GUIDE.md).
        var db = TestContent.Matrix;
        var identities = db.Engines.Keys.Concat(db.Parts.Keys).Concat(db.Tunes.Keys).Concat(db.Vehicles.Keys).Concat(db.Scenarios.Keys)
            .Concat(db.Parts.Values.SelectMany(p => p.Provides.Concat(p.Requires)))
            .Distinct().ToList();
        var familyTokens = new Regex(@"(?<![A-Za-z0-9])(k20|m54|kestrel_k20|isar_m54|syn_[a-z0-9_]+)(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
        // A comparison of an engine's size against a specific number (not a > 0 sanity check) would be a hidden special case.
        var countBranch = new Regex(@"\b(Cylinders|Displacement|DisplacementCc|BoreMm|StrokeMm)\s*(==|!=|<=|>=|<|>)\s*(0\.0*[1-9]|[2-9]|[1-9][0-9])");
        var files = new[] { "src/CarSim.Core", "src/CarSim.Gameplay" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(TestContent.RepoRoot, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(files.Count > 50);
        var problems = new List<string>();
        foreach (var file in files)
        {
            string code = StripComments(File.ReadAllText(file));
            string name = Path.GetRelativePath(TestContent.RepoRoot, file);
            foreach (Match m in familyTokens.Matches(code)) problems.Add($"{name}: engine family token '{m.Value}'");
            foreach (Match m in countBranch.Matches(code)) problems.Add($"{name}: branch on '{m.Value}'");
            foreach (Match lit in Regex.Matches(code, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                foreach (var id in identities.Where(id => lit.Groups[1].Value.Contains(id, StringComparison.Ordinal)))
                    problems.Add($"{name}: content id '{id}' in a string literal");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ---- The first family did not change ---------------------------------------------------------------------

    [Fact]
    public void K20ReferenceOutputIsUnchangedByTheSecondFamily()
    {
        // Full-precision values from the pre-milestone code (main at the merge of PR #2), stock K20 on RON 95, default
        // dyno sweep. The generic cam-timing terms are exactly zero for cams without authored centrelines, so nothing
        // moved. Update deliberately if K20 physics is ever changed on purpose.
        // Re-pinned 2026-09-27 for a content change, not physics: the pre-physics tune regeneration (Intake Gas Dynamics
        // 2.0 design resolution, Q5) moved 11 cells of k20.stock's VE table by 0.001, so peak power moved by 2e-8 and peak
        // torque by 8e-9 relative (was 110488.59485226133 W, 189.1821087807875 N·m; the regression fingerprint has the diff).
        // Re-pinned 2026-09-27 for a documented generic physics correction, Intake Gas Dynamics 2.0 Phase 1: the runner's
        // wave gain split from valve-event filling, v₀ and the ceiling re-anchored on this engine's air per cycle (within the
        // anchor's ±3 %, K20AnchorGuardTests) and the tables regenerated. Peak power −2.44 %, peak torque +2.20 % (was
        // 110488.5926433362 W, 189.18210731949648 N·m; docs/milestones/intake-gas-dynamics-2/PHASE1_GATE_REPORT.md).
        var run = new DynoRunner(SimFactory.Create(), new DynoSettings()).RunToCompletion();
        Assert.Equal(221, run.Samples.Count);
        Assert.Equal(107789.92353732626, run.PeakPower!.Power, 1e-6);
        Assert.Equal(193.3525607444823, run.PeakTorque!.Torque, 1e-9);
        var at2000 = SimFactory.At(SimFactory.Create(), 2000);
        Assert.Equal(0.0, at2000.IntakeCamAdvance);
        Assert.False(at2000.CamTuningFloorActive);
    }
}
