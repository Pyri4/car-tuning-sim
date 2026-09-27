using CarSim.Core.Content;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

/// <summary>
/// Cam timing as data: installed lobe centrelines and an ECU-driven intake phaser (SIMULATION_SPEC.md, "Cam timing").
/// Added for the second engine family (double VANOS); a cam without centrelines must behave exactly as before.
/// </summary>
public class CamTimingTests
{
    private const string M54Fuel = "gasoline_98";

    private static EngineSimulation M54(EcuTune? tune = null, params (string slot, string part)[] swaps) =>
        SimFactory.Create(SimFactory.AssemblyOf(TestContent.M54, swaps), M54Fuel, tune);

    /// <summary>The base content plus extra parts (JSON objects) in one more file.</summary>
    private static ContentDatabase WithParts(params string[] parts)
    {
        var docs = Directory.EnumerateFiles(TestContent.BaseContentPath, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetRelativePath(TestContent.BaseContentPath, f), File.ReadAllText(f)))
            .Append(("extra.json", $$"""{ "parts": [ {{string.Join(",", parts)}} ] }"""));
        return ContentLoader.LoadFromStrings(docs).GetOrThrow();
    }

    private static string Cams(string id, string extraSpec) => $$"""
        { "id": "{{id}}", "name": "{{id}}", "category": "camshafts", "mass_kg": 4.2, "requires": ["k20.cam_carrier"],
          "spec": { "intake_duration_deg": 224, "intake_lift_mm": 9.8, "exhaust_duration_deg": 220, "exhaust_lift_mm": 9.2,
                    "lobe_separation_deg": 112{{extraSpec}} } }
        """;

    private static EngineSimulation K20With(ContentDatabase db, string camsId)
    {
        var a = TestContent.StockK20();
        var removed = new Stack<(string, PartInstance)>();
        foreach (var s in a.RemovalSequenceFor("camshafts")) { Assert.True(a.Remove(s, out var p).Ok); removed.Push((s, p!)); }
        Assert.True(a.Remove("camshafts", out _).Ok);
        Assert.True(a.Install("camshafts", new PartInstanceFactory(70_000_000).Create(db.GetPart(camsId))).Ok);
        while (removed.Count > 0) { var (s, p) = removed.Pop(); Assert.True(a.Install(s, p).Ok); }
        var tune = SimFactory.StockTune();
        var config = EngineConfiguration.Build(a, db.GetFuel("gasoline_95"), new ValidationContext(tune.RevLimitRpm, null)).GetOrThrow();
        return new EngineSimulation(config, tune, EngineState.Warm());
    }

    [Fact]
    public void StraightUpCamsAreUntouchedByCamTiming()
    {
        foreach (var cams in TestContent.Database.Parts.Values.Where(p => p.Category == PartCategory.Camshafts && p.Id.StartsWith("k20.")))
        {
            var spec = cams.GetSpec<CamshaftSpec>();
            Assert.False(spec.HasCenterlines);
            var sim = SimFactory.Create(SimFactory.Assembly(("valve_springs", "k20.valve_springs.performance"), ("camshafts", cams.Id)));
            Assert.Equal(0.0, sim.Config.Banks[0].IntakePhaserRange);
            Assert.Equal(0.0, sim.Config.Banks[0].IntakeClosingShiftDeg(0.0));
            Assert.Equal(0.0, sim.Config.Banks[0].IntakeClosingShiftDeg(25.0)); // no centreline, nothing to shift
            Assert.Equal(sim.Config.Banks[0].VePeakRpm, sim.Config.Banks[0].VePeakRpmAt(25.0));
        }
    }

    [Fact]
    public void CamsDegreedInAtTheReferenceCentrelineMatchStraightUpExactly()
    {
        var db = WithParts(Cams("test.cams.degreed112", ", \"intake_centerline_deg\": 112, \"exhaust_centerline_deg\": 112"),
                           Cams("test.cams.retarded8", ", \"intake_centerline_deg\": 120, \"exhaust_centerline_deg\": 104"));
        var stock = SteadyStateSweep.Run(SimFactory.Create(), 1500, 7500, 1500, settleSeconds: 0.5);
        var degreed = SteadyStateSweep.Run(K20With(db, "test.cams.degreed112"), 1500, 7500, 1500, settleSeconds: 0.5);
        Assert.Equal(stock, degreed);

        // 8° of intake retard (same overlap): the intake closes later, so filling moves up the rev range.
        var retarded = SteadyStateSweep.Run(K20With(db, "test.cams.retarded8"), 1500, 7500, 1500, settleSeconds: 0.5);
        Assert.True(retarded[0].Torque < stock[0].Torque, "less low-speed torque");
        Assert.True(retarded[^1].Torque > stock[^1].Torque, "more top-end torque");
    }

    [Fact]
    public void CamSpecsValidateTheirTiming()
    {
        string Load(string extra) => string.Join("\n", ContentLoader.LoadFromStrings(new[]
        {
            ("c.json", $$"""{ "parts": [ {{Cams("c", extra)}} ] }"""),
        }).Errors);
        Assert.Equal("", Load(", \"intake_centerline_deg\": 110, \"exhaust_centerline_deg\": 110, \"intake_phaser_range_deg\": 40"));
        Assert.Contains("go together", Load(", \"intake_centerline_deg\": 110"));
        Assert.Contains("needs the installed", Load(", \"intake_phaser_range_deg\": 40"));
        Assert.Contains("intake_centerline_deg", Load(", \"intake_centerline_deg\": 200, \"exhaust_centerline_deg\": 110"));
        Assert.Contains("intake_phaser_range_deg", Load(", \"intake_centerline_deg\": 110, \"exhaust_centerline_deg\": 110, \"intake_phaser_range_deg\": 120"));
    }

    [Fact]
    public void OverlapGrowsDegreeForDegreeWithIntakeAdvance()
    {
        var cams = new CamshaftSpec
        {
            IntakeDurationDeg = 240, ExhaustDurationDeg = 236, IntakeLiftMm = 10, ExhaustLiftMm = 10, LobeSeparationDeg = 112,
            IntakeCenterlineDeg = 118, ExhaustCenterlineDeg = 108, IntakePhaserRangeDeg = 40,
        };
        Assert.Equal(12.0, cams.OverlapAt(0.0), 9);
        Assert.Equal(32.0, cams.OverlapAt(20.0), 9);
        Assert.Equal(0.0, new CamshaftSpec
        {
            IntakeDurationDeg = 190, ExhaustDurationDeg = 179, IntakeLiftMm = 9.7, ExhaustLiftMm = 9, LobeSeparationDeg = 112,
            IntakeCenterlineDeg = 134, ExhaustCenterlineDeg = 136,
        }.OverlapAt(60.0)); // the M54's short cams never overlap at 1 mm within the phaser's range
    }

    [Fact]
    public void ThePhaserFollowsTheEcuMapWithAnActuatorLag()
    {
        var tune = SimFactory.StockTuneOf(TestContent.M54);
        var sim = M54(tune);
        var at6000 = SimFactory.At(sim, 6000);
        Assert.Equal(0.0, at6000.IntakeCamAdvance, 3);
        // Drop to 2,000 rpm: the map asks for 35° of advance; the phaser gets there with a 0.15 s time constant.
        double target = tune.IntakeCamAdvanceAt(2000, 100);
        Assert.Equal(35.0, target);
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 2000, CoolantTemperatureOverride = 363.15 };
        EngineTelemetry t = null!;
        for (int i = 0; i < 30; i++) t = sim.Step(0.005, input); // 0.15 s
        Assert.InRange(t.IntakeCamAdvance / target, 0.55, 0.72);
        for (int i = 0; i < 200; i++) t = sim.Step(0.005, input);
        Assert.Equal(target, t.IntakeCamAdvance, 1);
    }

    [Fact]
    public void AnEcuThatCannotDriveThePhaserLeavesItParked()
    {
        var withoutControl = M54(swaps: ("ecu", "ecu.k20_oem"));
        Assert.Equal(0.0, withoutControl.Config.Banks[0].IntakePhaserRange);
        var tune = SimFactory.StockTuneOf(TestContent.M54);
        Assert.True(AssemblyValidator.Validate(withoutControl.Config.Assembly, new ValidationContext(tune.RevLimitRpm, null)).Has("cam_phaser_uncontrolled"));
        var parked = SimFactory.At(withoutControl, 2000);
        Assert.Equal(0.0, parked.IntakeCamAdvance);
        Assert.True(parked.Torque < 0.9 * SimFactory.At(M54(), 2000).Torque);
        // A standalone ECU with phaser outputs drives it from the same map.
        Assert.Equal(35.0, SimFactory.At(M54(swaps: ("ecu", "ecu.standalone")), 2000).IntakeCamAdvance, 1);
    }

    [Fact]
    public void EarlyIntakeClosingFillsBetterLowAndWorseHigh()
    {
        EcuTune Fixed(double advance)
        {
            var d = SimFactory.StockTuneOf(TestContent.M54).ToDocument();
            d.IntakeCamAdvanceDeg = d.LoadAxisKpa.Select(_ => d.RpmAxis.Select(_ => advance).ToArray()).ToArray();
            return EcuTune.FromDocument(d);
        }
        var advanced = M54(Fixed(40));
        var parked = M54(Fixed(0));
        Assert.True(SimFactory.At(advanced, 1500).AirPerCycle > 1.05 * SimFactory.At(parked, 1500).AirPerCycle);
        Assert.True(SimFactory.At(advanced, 6000).AirPerCycle < 0.95 * SimFactory.At(parked, 6000).AirPerCycle);
    }

    [Fact]
    public void ShippedCamMapTrapsTheMostAirWhereItWasCalibrated()
    {
        foreach (double rpm in new[] { 2000.0, 4000 })
        {
            var stock = SimFactory.StockTuneOf(TestContent.M54);
            double advance = stock.IntakeCamAdvanceAt(rpm, 100);
            double Air(double a)
            {
                var d = stock.ToDocument();
                d.IntakeCamAdvanceDeg = d.LoadAxisKpa.Select(_ => d.RpmAxis.Select(_ => a).ToArray()).ToArray();
                return SimFactory.At(M54(EcuTune.FromDocument(d)), rpm).AirPerCycle;
            }
            double best = Air(advance);
            Assert.True(best >= Air(advance + 5) && best >= Air(advance - 5), $"{rpm} rpm: {advance}° is not the filling optimum");
        }
    }

    [Fact]
    public void ShippedSparkMapKeepsItsMarginsOnTheRatingFuel()
    {
        // calibrate-spark wrote min(MBT − 1°, knock limit − 1.5°), rounded to 0.5°: the shipped map must still say so.
        foreach (double rpm in new[] { 1500.0, 3000, 5000 })
        {
            var t = SimFactory.At(M54(), rpm);
            Assert.True(t.IgnitionAdvance <= Math.Min(t.MbtAdvance - 1.0, t.KnockLimitAdvance - 1.5) + 0.3, $"{rpm} rpm: {t.IgnitionAdvance:F1}°");
            Assert.True(t.IgnitionAdvance >= Math.Min(t.MbtAdvance - 1.0, t.KnockLimitAdvance - 1.5) - 0.8, $"{rpm} rpm: {t.IgnitionAdvance:F1}° is stale");
            Assert.Equal(0.0, t.KnockIntensity);
        }
    }

    [Fact]
    public void CamTuningGuardNeverActsOnShippedCalibrations()
    {
        foreach (var engineId in TestContent.Families)
        {
            var tune = SimFactory.StockTuneOf(engineId);
            foreach (double rpm in new[] { 800.0, 2000, 4000, tune.RevLimitRpm - 300 })
                foreach (double throttle in new[] { 0.0, 0.2, 1.0 })
                {
                    var t = SimFactory.At(SimFactory.Create(TestContent.Stock(engineId), engineId == TestContent.M54 ? M54Fuel : "gasoline_95"), rpm, throttle, 0.5);
                    Assert.False(t.CamTuningFloorActive, $"{engineId} {rpm} rpm {throttle}");
                    Assert.False(t.VeFloorActive, $"{engineId} {rpm} rpm {throttle}");
                }
        }
        // It is reachable only far from any calibration: the M54 intake fully advanced at 6,000 rpm.
        var d = SimFactory.StockTuneOf(TestContent.M54).ToDocument();
        d.IntakeCamAdvanceDeg = d.LoadAxisKpa.Select(_ => d.RpmAxis.Select(_ => 60.0).ToArray()).ToArray();
        Assert.True(SimFactory.At(M54(EcuTune.FromDocument(d)), 6000).CamTuningFloorActive);
    }
}
