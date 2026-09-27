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
/// Why the M54's modelled full-load curve differs in shape from the reference (a plateau from 1,500 to 2,750 rpm and
/// a steady fall, against a first peak of 300 N·m at 3,500 rpm, a DISA dip near 4,000 and a second hump). These pin
/// the conclusions of the investigation in SIMULATION_SPEC.md, "M54 torque-curve investigation": the shape is set by
/// generic physics the model lacks, not by the M54's content or its calibration. If that physics is added (ROADMAP),
/// the tests marked "known limitation" are expected to change, along with that section.
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
    private static double PhaseTunedTo(EngineConfiguration c, double rpm)
    {
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

    private static double ShapeAt(EngineSimulation sim, EngineTelemetry t) => new AirPath(sim.Config).VeShape(t.Rpm, t.IntakeCamAdvance);

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
            double peak = sim.Config.VePeakRpm;
            Assert.True(ShapeAt(sim, SimFactory.At(sim, 1500)) < 0.8, $"{sim.Config.Geometry.Cylinders} cyl at 1,500 rpm");
            Assert.True(ShapeAt(sim, SimFactory.At(sim, 3000)) < 0.95);
            Assert.True(ShapeAt(sim, SimFactory.At(sim, peak)) > 0.999);
        }
    }

    [Fact]
    public void APhasedEngineSitsOnItsFillingCeilingAtEverySpeed()
    {
        // Known limitation (the root cause of the flat low end). The model has one filling hump for intake closing and
        // intake gas dynamics together, and the phaser moves all of it: the calibrated VANOS keeps it centred on the
        // engine speed, so the M54 fills as if tuned at every speed from idle to the power peak. Real low-speed filling
        // lacks the runner's ram and resonance, which a cam phase cannot move.
        var sim = SimFactory.Create(TestContent.StockM54(), M54Fuel);
        foreach (var t in SteadyStateSweep.Run(sim, 1000, 6250, 750, settleSeconds: 1.0))
        {
            Assert.InRange(ShapeAt(sim, t), 0.99, 1.0);
            // The filling peak follows the speed wherever the phaser can reach (below the parked peak).
            if (t.Rpm is >= 1500 and <= 5000) Assert.Equal(t.Rpm, sim.Config.VePeakRpmAt(t.IntakeCamAdvance), t.Rpm * 0.06);
        }
    }

    [Fact]
    public void ThePhaserFlattensAnyFamilyNotJustTheM54()
    {
        // The same phaser given to the K20 by content alone (park centreline 20° late, 50° of intake phaser, an ECU that
        // drives it) flattens its curve the same way: the effect is the generic model's, not the M54's data.
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
        Assert.True(Phased(2000) > 0.98 * Phased(4000)); // phased: flat from 2,000 rpm
        Assert.True(Phased(2000) > 1.15 * Fixed(2000));
    }

    [Fact]
    public void RunnerLengthOnlyMattersWhileThePhaserIsParked()
    {
        // Known limitation: the runner shifts the one filling hump, and the phaser shifts it back, so under VANOS the
        // intake runner has no effect on full-load filling. This is also why DISA (a switched runner) has nothing to act
        // on in this model.
        ContentDatabase WithRunner(double mm) => Variant(item => Spec(item, "m54.intake.disa")["runner_length_mm"] = mm);
        var shortRunner = WithRunner(250);
        var longRunner = WithRunner(550);

        // Parked, a longer runner tunes lower and fills better at 2,000 rpm.
        Assert.True(AirAt(longRunner, TestContent.M54, M54Fuel, 2000, null) > 1.04 * AirAt(shortRunner, TestContent.M54, M54Fuel, 2000, null));

        // With the phase that tunes each engine to the speed, the runner no longer matters.
        foreach (double rpm in new[] { 2000.0, 3500 })
        {
            double Tuned(ContentDatabase db) => AirAt(db, TestContent.M54, M54Fuel, rpm, PhaseTunedTo(Build(db, TestContent.M54, M54Fuel).Config, rpm));
            Assert.Equal(1.0, Tuned(longRunner) / Tuned(shortRunner), 0.005);
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

    [Fact]
    public void TheModelledCurveIsAPlateauThenAMonotonicFall()
    {
        // Known limitation, pinned as documented: the peak sits on the low-speed plateau, 3,500 rpm is a few percent
        // below it (the reference peaks there), and above the plateau torque only falls — the model has no intake
        // resonance to make DISA's 3,500 rpm hump, its switch-over dip near 4,000 rpm or its second hump.
        var curve = SteadyStateSweep.Run(SimFactory.Create(TestContent.StockM54(), M54Fuel), 1000, 6250, 250, settleSeconds: 1.0);
        var peak = SimFactory.PeakTorque(curve);
        Assert.InRange(peak.Rpm, 1500, 2750);
        Assert.InRange(curve.Single(p => Math.Abs(p.Rpm - 3500) < 0.01).Torque / peak.Torque, 0.94, 0.98);
        var above = curve.Where(p => p.Rpm >= 2500).ToList();
        for (int i = 1; i < above.Count; i++)
            Assert.True(above[i].Torque < above[i - 1].Torque + 0.2, $"torque rises at {above[i].Rpm} rpm");
    }
}
