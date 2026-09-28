using System.Text.Json;
using System.Text.Json.Nodes;
using CarSim.Core.Content;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.SecondFamily;

/// <summary>
/// The M54 torque-curve investigation (SIMULATION_SPEC.md, "M54 torque-curve investigation") found the shape set by
/// generic physics the model lacked: one filling hump for valve timing and intake gas dynamics together, which a cam
/// phaser moved whole. Intake Gas Dynamics 2.0 (Phase 1) split them, and these tests changed with it, deliberately
/// (docs/milestones/intake-gas-dynamics-2/PHASE1_GATE_REPORT.md):
/// <list type="bullet">
/// <item>inverted: the phaser keeps the <i>valve-event</i> filling at its ceiling, but not the runner's gain; runner length
/// matters under the phaser; a phased engine is no longer pinned flat;</item>
/// <item>retired: the M54's "plateau then monotonic fall" shape (the curve's shape is now held-out validation, reported by
/// <c>IntakeGasDynamicsAcceptanceTests.TheM54DisaStagesFollowTheirProvenance</c>, never asserted);</item>
/// <item>kept: the fixed-cam valve-event hump, the calibration methodology (cam envelope, knock-limited spark) and the
/// top end's sensitivity to the estimated flows.</item>
/// </list>
/// </summary>
public class TorqueCurveDiagnosisTests
{
    private const string M54Fuel = "gasoline_98";

    // ---- Content variants and fixed cam phases ------------------------------------------------------------------

    /// <summary>The base content with JSON edits applied (items looked up by array name and id).</summary>
    private static ContentDatabase Variant(Action<Func<string, string, JsonObject>> edit)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var docs = Directory.EnumerateFiles(TestContent.BaseContentPath, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Source: Path.GetRelativePath(TestContent.BaseContentPath, f),
                          Node: JsonNode.Parse(File.ReadAllText(f), documentOptions: options)!))
            .ToList();
        JsonObject Item(string array, string id) => docs.SelectMany(d => d.Node[array]?.AsArray() ?? new JsonArray())
            .Single(n => (string?)n!["id"] == id)!.AsObject();
        edit(Item);
        return ContentLoader.LoadFromStrings(docs.Select(d => (d.Source, d.Node.ToJsonString()))).GetOrThrow();
    }

    private static JsonObject Spec(Func<string, string, JsonObject> item, string partId) => item("parts", partId)["spec"]!.AsObject();

    private static EngineSimulation Build(ContentDatabase db, string engineId, string fuel, EcuTune? tune = null)
    {
        var engine = db.GetEngine(engineId);
        tune ??= EcuTune.FromDocument(db.GetTune(engine.StockTune));
        var a = EngineAssembly.CreateStock(engine, db, new PartInstanceFactory());
        var config = EngineConfiguration.Build(a, db.GetFuel(fuel), new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa));
        return new EngineSimulation(config.GetOrThrow(), tune, EngineState.Warm());
    }

    /// <summary>The tune with the intake cam held at one advance everywhere (null: no cam map, the phaser stays parked).</summary>
    private static EcuTune AtPhase(EcuTune tune, double? advance)
    {
        var d = tune.ToDocument();
        d.IntakeCamAdvanceDeg = advance is double a ? d.LoadAxisKpa.Select(_ => d.RpmAxis.Select(_ => a).ToArray()).ToArray() : null;
        return EcuTune.FromDocument(d);
    }

    /// <summary>The intake advance that puts the filling peak at <paramref name="rpm"/>, within the phaser's range.</summary>
    private static double PhaseTunedTo(EngineConfiguration config, double rpm)
    {
        var c = config.Banks[0]; // these engines have one bank
        double lo = 0, hi = c.IntakePhaserRange;
        if (c.VePeakRpmAt(lo) <= rpm) return lo;
        if (c.VePeakRpmAt(hi) >= rpm) return hi;
        for (int i = 0; i < 50; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (c.VePeakRpmAt(mid) > rpm) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>Full-load air per cycle at <paramref name="rpm"/> with the intake cam held at <paramref name="advance"/>.</summary>
    private static double AirAt(ContentDatabase db, string engineId, string fuel, double rpm, double? advance)
    {
        var stock = EcuTune.FromDocument(db.GetTune(db.GetEngine(engineId).StockTune));
        return SimFactory.At(Build(db, engineId, fuel, AtPhase(stock, advance)), rpm).AirPerCycle;
    }

    private static double ShapeAt(EngineSimulation sim, EngineTelemetry t) => sim.AirPathOf(0).VeShape(t.Rpm, t.IntakeCamAdvance);

    // ---- The generic filling model and the phaser ---------------------------------------------------------------

    [Fact]
    public void FixedCamsFillAlongTheGenericHump()
    {
        // The VE model is not flat by itself: with the cams fixed, filling rises towards the tuned speed and falls past
        // it, for the K20 (tuned ≈ 5,270 rpm) and for the M54 with its phaser parked (tuned ≈ 5,400 rpm).
        var k20 = SimFactory.Create(TestContent.StockK20());
        var parkedM54 = SimFactory.Create(TestContent.StockM54(), M54Fuel, AtPhase(SimFactory.StockTuneOf(TestContent.M54), null));
        foreach (var sim in new[] { k20, parkedM54 })
        {
            double peak = sim.Config.Banks[0].VePeakRpm;
            Assert.True(ShapeAt(sim, SimFactory.At(sim, 1500)) < 0.8, $"{sim.Config.Geometry.Cylinders} cyl at 1,500 rpm");
            Assert.True(ShapeAt(sim, SimFactory.At(sim, 3000)) < 0.95);
            Assert.True(ShapeAt(sim, SimFactory.At(sim, peak)) > 0.999);
        }
    }

    [Fact]
    public void APhasedEngineHoldsItsValveEventFillingButNotItsRunnerGain()
    {
        // Inverted by Intake Gas Dynamics 2.0 (was "a phased engine sits on its filling ceiling at every speed", the root
        // cause of the flat low end). The calibrated VANOS still keeps the valve-event filling at its ceiling — that is
        // what a phaser does — but the runner's wave gain has no cam input: it rises toward the runner's tuned speed and
        // falls past it whatever the phaser does, so the phased engine no longer fills as if tuned at every speed.
        var sim = SimFactory.Create(TestContent.StockM54(), M54Fuel);
        var sweep = SteadyStateSweep.Run(sim, 1000, 6250, 750, settleSeconds: 1.0);
        foreach (var t in sweep)
        {
            Assert.InRange(ShapeAt(sim, t), 0.99, 1.0);
            // The valve-event peak follows the speed wherever the phaser can reach (below the parked peak).
            if (t.Rpm is >= 1500 and <= 5000) Assert.Equal(t.Rpm, sim.Config.Banks[0].VePeakRpmAt(t.IntakeCamAdvance), t.Rpm * 0.06);
        }
        // The gain is lowest at the bottom of the range and peaks near the tuned speed of the stage the engine runs.
        var best = sweep.MaxBy(t => t.IntakeWaveGain)!;
        Assert.True(sweep[0].IntakeWaveGain < best.IntakeWaveGain, "the runner gain varies with speed under the phaser");
        Assert.InRange(best.Rpm, 0.8 * best.RunnerTunedRpm, 1.25 * best.RunnerTunedRpm);
    }

    [Fact]
    public void APhaserFillsAnyFamilyBetterLowNotJustTheM54()
    {
        // The same phaser given to the K20 by content alone (park centreline 20° late, 50° of intake phaser, an ECU that
        // drives it) lifts its low-speed filling the same way: the effect is the generic model's, not the M54's data.
        // Inverted by Intake Gas Dynamics 2.0: the phased curve is no longer asserted flat (it was pinned within 2 % from
        // 2,000 to 4,000 rpm, the old limitation); the runner's gain, which the phaser cannot move, now shapes it.
        var phased = Variant(item =>
        {
            var cams = Spec(item, "k20.cams.oem");
            cams["intake_centerline_deg"] = 132.0;
            cams["exhaust_centerline_deg"] = 112.0;
            cams["intake_phaser_range_deg"] = 50.0;
            item("engines", TestContent.K20)["stock_parts"]!["ecu"] = "ecu.standalone";
        });
        var config = Build(phased, TestContent.K20, "gasoline_95").Config;
        double Phased(double rpm) => AirAt(phased, TestContent.K20, "gasoline_95", rpm, PhaseTunedTo(config, rpm));
        double Fixed(double rpm) => SimFactory.At(SimFactory.Create(TestContent.StockK20()), rpm).AirPerCycle;

        Assert.True(Fixed(2000) < 0.9 * Fixed(4000)); // fixed cams: a torque curve that climbs to its peak
        Assert.True(Phased(2000) > 1.15 * Fixed(2000)); // phased: the valve event tuned to the speed fills far better low
        Assert.True(Phased(4000) >= 0.995 * Fixed(4000)); // and costs nothing where the fixed cams are tuned
    }

    [Fact]
    public void RunnerLengthMattersUnderThePhaserToo()
    {
        // Inverted by Intake Gas Dynamics 2.0 (was "runner length only matters while the phaser is parked": the runner
        // shifted the one filling hump and the phaser shifted it back). The runner's wave gain now has no cam input, so
        // with the phase that tunes each engine's valve event to the speed the runner still decides the filling: the
        // 550 mm runner (tuned ≈ 3,200 rpm) out-fills the 250 mm one (≈ 5,700 rpm) at 2,000 and 3,500 rpm, parked or
        // phased, in the same direction. Test-only single-stage runners; the size is the unsourced amplitude's, so only
        // the sign and a floor far below it (0.5 %) are asserted.
        ContentDatabase WithRunner(double mm) => Variant(item =>
        {
            var spec = Spec(item, "m54.intake.disa");
            spec["runner_length_mm"] = mm;
            spec.Remove("switched_runner_length_mm");
        });
        var shortRunner = WithRunner(250);
        var longRunner = WithRunner(550);

        // Parked, a longer runner tunes lower and fills better at 2,000 rpm.
        Assert.True(AirAt(longRunner, TestContent.M54, M54Fuel, 2000, null) > 1.005 * AirAt(shortRunner, TestContent.M54, M54Fuel, 2000, null));

        // With the phase that tunes each engine's valve event to the speed, the runner still matters, the same way.
        foreach (double rpm in new[] { 2000.0, 3500 })
        {
            double Tuned(ContentDatabase db) => AirAt(db, TestContent.M54, M54Fuel, rpm, PhaseTunedTo(Build(db, TestContent.M54, M54Fuel).Config, rpm));
            Assert.True(Tuned(longRunner) > 1.005 * Tuned(shortRunner), $"{rpm} rpm: {Tuned(longRunner) / Tuned(shortRunner) - 1:P2}");
        }
    }

    // ---- Calibration methodology ---------------------------------------------------------------------------------

    [Fact]
    public void TheCamCalibratorAddsNothingBeyondTheFixedPhaseEnvelope()
    {
        // calibrate-cams chose, at each speed, the phase that traps the most air. The shipped schedule must match the
        // best fixed phase (the envelope) — not exceed it and not fall short of it: the flat low end is what the physics
        // offers at the best phase, not an artefact of the calibration.
        var db = TestContent.Database;
        var shipped = SimFactory.Create(TestContent.StockM54(), M54Fuel);
        foreach (double rpm in new[] { 1500.0, 3000, 5000 })
        {
            double envelope = Enumerable.Range(0, 13).Select(i => 5.0 * i).Append(PhaseTunedTo(shipped.Config, rpm))
                .Max(a => AirAt(db, TestContent.M54, M54Fuel, rpm, a));
            double air = SimFactory.At(shipped, rpm).AirPerCycle;
            Assert.InRange(air / envelope, 0.995, 1.002);
        }
    }

    [Fact]
    public void LowSpeedSparkIsKnockLimitedNotOverAdvanced()
    {
        // calibrate-spark wrote min(MBT − 1°, knock limit − 1.5°). Below ≈ 2,500 rpm knock binds, far from best torque;
        // above it the map runs at MBT − 1°. So the low end already pays a knock penalty on RON 98; its height does not
        // come from an optimistic spark map.
        var sim = SimFactory.Create(TestContent.StockM54(), M54Fuel);
        var low = SimFactory.At(sim, 1500);
        Assert.True(low.KnockLimitAdvance - 1.5 < low.MbtAdvance - 1.0 - 5.0, "knock binds at 1,500 rpm");
        Assert.True(low.IgnitionAdvance < low.MbtAdvance - 6.0);
        var mid = SimFactory.At(sim, 3500);
        Assert.True(mid.KnockLimitAdvance - 1.5 > mid.MbtAdvance - 1.0, "MBT binds at 3,500 rpm");
        Assert.InRange(mid.IgnitionAdvance, mid.MbtAdvance - 1.8, mid.MbtAdvance - 0.2);
    }

    // ---- Content: the estimated flows set the top end, not the shape ---------------------------------------------

    [Fact]
    public void TheTopEndIsSensitiveToTheEstimatedFlowContent()
    {
        // The head's port flows are estimates (PARTS_DATABASE.md). 20 % more flow is worth ≈ 3 % of air at 6,000 rpm and
        // under 0.5 % at 2,000: part of the top-end deficit may be content, the low-end shape is not.
        var better = Variant(item =>
        {
            var head = Spec(item, "m54.head.oem");
            foreach (var key in new[] { "intake_port_flow_cfm", "exhaust_port_flow_cfm" })
                foreach (var point in head[key]!.AsArray()) point![1] = (double)point[1]! * 1.2;
        });
        var db = TestContent.Database;
        var tune = SimFactory.StockTuneOf(TestContent.M54);
        double Air(ContentDatabase d, double rpm) => SimFactory.At(Build(d, TestContent.M54, M54Fuel, tune), rpm).AirPerCycle;
        Assert.True(Air(better, 6000) > 1.02 * Air(db, 6000));
        Assert.True(Air(better, 2000) < 1.01 * Air(db, 2000));
    }

    // ---- The resulting shape ---------------------------------------------------------------------------------------
    // Retired by Intake Gas Dynamics 2.0: TheModelledCurveIsAPlateauThenAMonotonicFall pinned the old limitation's shape
    // (peak on a 1,500–2,750 rpm plateau, then a monotonic fall). The curve's shape is held-out validation now: reported by
    // IntakeGasDynamicsAcceptanceTests.TheM54DisaStagesFollowTheirProvenance against the reference, never asserted.
}
