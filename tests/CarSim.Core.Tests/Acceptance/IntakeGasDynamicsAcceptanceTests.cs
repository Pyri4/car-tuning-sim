using CarSim.Core.Content;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;
using CarSim.Verification;
using CarSim.Verification.Calibration;
using Xunit.Abstractions;

namespace CarSim.Core.Tests.Acceptance;

/// <summary>
/// System-level acceptance tests of Intake Gas Dynamics 2.0, written against today's public API before the model exists
/// (docs/milestones/INTAKE_GAS_DYNAMICS_2.md, "Acceptance tests"; the model they will hold is locked in
/// docs/milestones/INTAKE_GAS_DYNAMICS_2_PHASE1_PROPOSAL.md). Each one fails on today's model, where valve timing and
/// runner gas dynamics are one filling hump that a cam phaser moves whole; the failure of each is recorded in the
/// milestone document. Every criterion is a physical or model relationship (a scaling law, a sign, an invariance, a
/// consistency between two measurements) with a tolerance derived in the proposal. None requires a torque gain of a
/// given size or a torque-curve shape, so none depends on the unsourced amplitude and damping parameters, and none can
/// be met by tuning them. Tests 1, 4 and 5 were revised by the design-resolution gate (U5); tests 2 and 3 are unchanged.
/// Geometry marked "test-only" is a generic probe, not data for the engine it is fitted to.
/// </summary>
[Trait("Milestone", "IntakeGasDynamics2")]
public class IntakeGasDynamicsAcceptanceTests(ITestOutputHelper output)
{
    private const string M54 = TestContent.M54, K20 = TestContent.K20;

    /// <summary>
    /// Lower bound of the stage-crossover ratio when the intake closes earlier. With the runner gain independent of cam
    /// timing (Phase 1) the ratio is exactly 1. The deferred intake-closing coupling (U6) moves a runner's best speed down
    /// as the intake closes earlier: an exploratory lumped model gives 11–22 % for 40° of closing. 0.70 admits that; the
    /// valve-event optimum itself moves by ≈ 85 % over the same 40° (the correlation's tuned piston speed 14.1 → 2.1 m/s).
    /// </summary>
    private const double EarlierClosingRatioMin = 0.70;

    /// <summary>Upper bound: physics predicts no upward shift for earlier closing; 5 % covers the 100 rpm sweep grid.</summary>
    private const double EarlierClosingRatioMax = 1.05;

    /// <summary>
    /// Matrix item 5: the runner's response belongs to the runner, not to the cam phaser. Two runner lengths on the same
    /// cams at the same cam phase cancel the valve-event part of filling, so the speed above which the shorter runner
    /// out-fills the longer one is the runners' own. Moving the intake cam by 40° (earlier closing) may move it only as
    /// much as the physics of intake closing allows, in its direction (ratio 0.70–1.05; see
    /// <see cref="EarlierClosingRatioMin"/>), never with the valve-event optimum. Today the phaser carries the whole hump,
    /// and at 50° there is no crossover at all.
    /// </summary>
    [PendingAcceptanceFact]
    public void TheRunnerResponseIsNotCarriedByTheCamPhaser()
    {
        // Test-only geometry: two fixed runners on the M54 (the stock part carries DISA's two stages since Phase 1).
        var db = IntakeRig.Content(
            ("m54.intake.disa", "test.intake.m54_long", s => IntakeRig.SingleStage(s, 380)),
            ("m54.intake.disa", "test.intake.m54_short", s => IntakeRig.SingleStage(s, 250)));
        var tune = db.GetTune(db.GetEngine(M54).StockTune);
        double? At(double advance)
        {
            var phased = IntakeRig.WithCamAt(tune, advance);
            var longRunner = IntakeRig.Sweep(db, IntakeRig.Build(db, M54, ("intake_manifold", "test.intake.m54_long")), "gasoline_98", phased, 1000, 6400, 100);
            var shortRunner = IntakeRig.Sweep(db, IntakeRig.Build(db, M54, ("intake_manifold", "test.intake.m54_short")), "gasoline_98", phased, 1000, 6400, 100);
            return IntakeRig.Crossover(shortRunner, longRunner, p => p.VeDynamic);
        }
        double? early = At(50), late = At(10);
        output.WriteLine($"runner crossover 250/380 mm: {early:F0} rpm with the intake cam at 50°, {late:F0} rpm at 10°; ratio {early / late:F4}");
        Assert.True(early is >= 1500 and <= 6400, $"runner crossover with the intake cam at 50°: {early?.ToString("F0") ?? "none"} rpm");
        Assert.True(late is >= 1500 and <= 6400, $"runner crossover with the intake cam at 10°: {late?.ToString("F0") ?? "none"} rpm");
        Assert.InRange(early!.Value / late!.Value, EarlierClosingRatioMin, EarlierClosingRatioMax);
    }

    /// <summary>
    /// Matrix items 1–2: tuned speed falls with runner length between the Helmholtz (∝ L^−½) and quarter-wave (∝ L^−1)
    /// limits. Fixed cams (K20), three runners (230 mm short-runner part, 340 mm OEM, 460 mm test-only clone): the
    /// crossover of each adjacent pair scales with the pair's geometric-mean length by an exponent in [0.45, 1.1]
    /// (a small allowance for end corrections, which shorten the effective ratio). Today the tuned speed goes as L^−0.25.
    /// </summary>
    [PendingAcceptanceFact]
    public void TunedSpeedScalesWithRunnerLengthAsTheWaveModelsPredict()
    {
        var db = IntakeRig.Content(("k20.intake.oem", "test.intake.k20_long", s => s["runner_length_mm"] = 460)); // test-only geometry
        var tune = db.GetTune(db.GetEngine(K20).StockTune);
        IReadOnlyList<EngineTelemetry> Runner(string part) =>
            IntakeRig.Sweep(db, IntakeRig.Build(db, K20, ("intake_manifold", part)), "gasoline_95", tune, 1000, 8000, 100);
        var r230 = Runner("k20.intake.short_runner");
        var r340 = Runner("k20.intake.oem");
        var r460 = Runner("test.intake.k20_long");
        double? upper = IntakeRig.Crossover(r230, r340, p => p.VeDynamic), lower = IntakeRig.Crossover(r340, r460, p => p.VeDynamic);
        Assert.True(upper is >= 1000 and <= 8000 && lower is >= 1000 and <= 8000, $"crossovers 230/340 mm {upper:F0}, 340/460 mm {lower:F0} rpm");
        double exponent = Math.Log(upper!.Value / lower!.Value) / Math.Log(Math.Sqrt(340.0 * 460) / Math.Sqrt(230.0 * 340));
        output.WriteLine($"crossovers: 230/340 mm {upper:F0} rpm, 340/460 mm {lower:F0} rpm; length exponent {exponent:F3}");
        Assert.InRange(exponent, 0.45, 1.1);
    }

    /// <summary>
    /// Matrix item 7: the charge's speed of sound sets the tuned speed, a = √(γRT). Heating the charge (ambient 263 K →
    /// 323 K, fixed cams, K20 230/340 mm runners) must move the runner crossover up by between half and 1.5 times
    /// √(T_hot/T_cold) − 1, with T the charge temperature the model reports there. Today the tuned speed ignores temperature.
    /// </summary>
    [PendingAcceptanceFact]
    public void AHotterChargeRaisesTheTunedSpeedWithTheSpeedOfSound()
    {
        var db = TestContent.Database;
        var tune = db.GetTune(db.GetEngine(K20).StockTune);
        (double Rpm, double Charge) At(double ambientK)
        {
            var shortRunner = IntakeRig.Sweep(db, IntakeRig.Build(db, K20, ("intake_manifold", "k20.intake.short_runner")), "gasoline_95", tune, 1000, 8000, 100, ambientK);
            var longRunner = IntakeRig.Sweep(db, IntakeRig.Build(db, K20), "gasoline_95", tune, 1000, 8000, 100, ambientK);
            double? x = IntakeRig.Crossover(shortRunner, longRunner, p => p.VeDynamic);
            Assert.True(x is >= 1000 and <= 8000, $"runner crossover at {ambientK} K: {x?.ToString("F0") ?? "none"} rpm");
            return (x!.Value, IntakeRig.Near(longRunner, x.Value, p => p.ChargeTemperature));
        }
        var cold = At(263.15);
        var hot = At(323.15);
        double predicted = Math.Sqrt(hot.Charge / cold.Charge) - 1.0;
        double measured = hot.Rpm / cold.Rpm - 1.0;
        output.WriteLine($"crossover {cold.Rpm:F0} rpm at 263 K ambient (charge {cold.Charge:F1} K), {hot.Rpm:F0} rpm at 323 K (charge {hot.Charge:F1} K): " +
                         $"shift {measured:P2}, charge-temperature prediction {predicted:P2}, band {0.5 * predicted:P2}–{1.5 * predicted:P2}");
        Assert.True(predicted > 0.05, $"the charge should heat up: {cold.Charge:F0} → {hot.Charge:F0} K");
        Assert.InRange(measured, 0.5 * predicted, 1.5 * predicted);
    }

    /// <summary>
    /// Matrix items 3–5, generic part: a two-stage intake acts on an engine whose cams follow a calibrated phaser map, and
    /// the ECU's switch speed selects the better stage. Test-only stage geometry (380/250 mm) on the M54 with an ECU that
    /// can drive it; the shipped VANOS map. Relationships only:
    /// <list type="number">
    /// <item>the stages are ordered (long below, short above) with a crossover in the rev range, each stage ahead
    /// somewhere by more than 0.2 % VE_dyn (a non-vacuity floor far below any physical effect, not a required gain);</item>
    /// <item>the phaser does not absorb the stage effect. The stage ratio ρ(N) = VE_dyn,short / VE_dyn,long cancels the
    /// valve-event part of filling, so the phaser map may change it only through the intake-closing coupling (deferred,
    /// U6), which shifts a runner's response in speed by at most ≈ 22 % per 40° of closing but does not cancel it. So at
    /// speeds below 0.7 × the parked crossover (out of reach of such a shift) where the parked stage effect |ρ − 1| is at
    /// least a quarter of its largest value there, the effect under the map has the same sign and at least half the size.
    /// Every threshold is relative, so the test holds for any amplitude and damping in their bands;</item>
    /// <item>where the stages differ by at least 0.5 % in VE_dyn, full-load torque differs in the same direction (the
    /// stage acts through filling, not through fuelling or spark);</item>
    /// <item>with the switch speed at the crossover (rounded to 100 rpm, as the tune driver sets it), the switched curve
    /// is the upper envelope of the two stages within 0.5 %, except within 150 rpm (the hysteresis) of the switch.</item>
    /// </list>
    /// Today the phaser re-centres the single filling hump for either runner, so below the park speed the stage effect
    /// under the map almost vanishes while it is large with the cams parked.
    /// </summary>
    [PendingAcceptanceFact]
    public void ASwitchedRunnerActsOnAPhasedEngine()
    {
        var db = IntakeRig.Content(
            ("m54.intake.disa", "test.intake.m54_two_stage", s => IntakeRig.TwoStage(s, 380, 250)), // test-only geometry
            ("ecu.m54_oem", "test.ecu.m54_runner_control", s => s["intake_runner_control"] = true));
        var tune = db.GetTune(db.GetEngine(M54).StockTune);
        IReadOnlyList<EngineTelemetry> Run(TuneDocument t) => IntakeRig.Sweep(db,
            IntakeRig.Build(db, M54, ("intake_manifold", "test.intake.m54_two_stage"), ("ecu", "test.ecu.m54_runner_control")), "gasoline_98", t, 1000, 6400, 100);

        var longStage = Run(tune with { IntakeRunnerSwitchRpm = null });
        var shortStage = Run(tune with { IntakeRunnerSwitchRpm = 500 });
        Assert.All(shortStage, p => Assert.True(p.SwitchedRunner));
        double? crossover = IntakeRig.Crossover(shortStage, longStage, p => p.VeDynamic);
        Assert.True(crossover is >= 1000 and <= 6400, $"stage crossover under the phaser map: {crossover?.ToString("F0") ?? "none"} rpm");
        double longAhead = longStage.Zip(shortStage).Max(p => p.First.VeDynamic / p.Second.VeDynamic - 1);
        double shortAhead = longStage.Zip(shortStage).Max(p => p.Second.VeDynamic / p.First.VeDynamic - 1);
        Assert.True(longAhead > 0.002 && shortAhead > 0.002, $"stage advantages: long {longAhead:P2}, short {shortAhead:P2}");

        var parked = IntakeRig.WithCamAt(tune, 0);
        var longParked = Run(parked with { IntakeRunnerSwitchRpm = null });
        var shortParked = Run(parked with { IntakeRunnerSwitchRpm = 500 });
        double? parkedCrossover = IntakeRig.Crossover(shortParked, longParked, p => p.VeDynamic);
        Assert.True(parkedCrossover is >= 1000 and <= 6400, $"stage crossover with the cams parked: {parkedCrossover?.ToString("F0") ?? "none"} rpm");
        var band = Enumerable.Range(0, longStage.Count).Where(i => longStage[i].Rpm >= 1500 && longStage[i].Rpm <= 0.7 * parkedCrossover!.Value).ToList();
        Assert.True(band.Count > 0, $"no speed between 1,500 rpm and 0.7 × the parked crossover ({parkedCrossover:F0} rpm) to compare the stage effect at");
        double Effect(IReadOnlyList<EngineTelemetry> s, IReadOnlyList<EngineTelemetry> l, int i) => s[i].VeDynamic / l[i].VeDynamic - 1;
        double largest = band.Max(i => Math.Abs(Effect(shortParked, longParked, i)));
        output.WriteLine($"stage crossover {crossover:F0} rpm under the map, {parkedCrossover:F0} rpm parked; advantages under the map: long {longAhead:P2}, short {shortAhead:P2}");
        foreach (int i in band.Where(i => Math.Abs(Effect(shortParked, longParked, i)) >= 0.25 * largest))
        {
            double withMap = Effect(shortStage, longStage, i), withParked = Effect(shortParked, longParked, i);
            output.WriteLine($"  {longStage[i].Rpm,5:F0} rpm: stage effect {withMap,8:P2} under the map, {withParked,8:P2} parked (cam {longStage[i].IntakeCamAdvance:F1}°)");
            Assert.True(Math.Sign(withMap) == Math.Sign(withParked) && Math.Abs(withMap) >= 0.5 * Math.Abs(withParked),
                $"{longStage[i].Rpm:F0} rpm: the phaser map absorbs the stage effect ({withMap:P2} against {withParked:P2} with the cams parked)");
        }

        IntakeRig.AssertStagesActThroughFilling(longStage, shortStage);
        double switchRpm = Math.Round(crossover!.Value / 100.0) * 100.0;
        IntakeRig.AssertSwitchFollowsTheUpperEnvelope(longStage, shortStage, Run(tune with { IntakeRunnerSwitchRpm = switchRpm }), switchRpm);
    }

    /// <summary>
    /// Acceptance criterion 1 (M54 DISA), as relationships with the M54's own data. Phase 1 authors DISA by the frozen
    /// procedure of the proposal (U4): open stage = the runner estimate, closed stage derived so that the model's stage
    /// crossover is the centre of the sourced switch band (3,750–4,100 rpm, assumption A-D1), computed from the gain
    /// functions alone before any M54 output is looked at. Asserted:
    /// <list type="number">
    /// <item>DISA is fitted and the ECU drives it (capability summary, independent of the intake schema);</item>
    /// <item>the full-load stage crossover of the whole engine (stages held) lies in the sourced band: the derivation,
    /// done on the gain functions, carries through the air path, fuelling and cam map unchanged;</item>
    /// <item>the tune's switch speed, set by the tune driver from that crossover, lies in the sourced band;</item>
    /// <item>the closed stage fills better below the switch and the open stage above it, and torque follows filling;</item>
    /// <item>the switched curve is the upper envelope of the two stages;</item>
    /// <item>peak torque and power stay in the published bands (300 N·m ± 10 %, 170 kW ± 15 %).</item>
    /// </list>
    /// Not asserted, reported: the curve's shape (where it peaks, how far it rises from 1,500 rpm) is held-out validation
    /// against the reference shape. A miss is classified and documented, never closed by changing content or parameters.
    /// Today DISA is not authored.
    /// </summary>
    [PendingAcceptanceFact]
    public void TheM54DisaStagesFollowTheirProvenance()
    {
        const double switchBandLow = 3750, switchBandHigh = 4100; // sourced: PARTS_DATABASE.md, "Isar M54 reference engine"
        var db = TestContent.Database;
        var caps = EngineCapabilities.Resolve(TestContent.StockM54());
        Assert.True(caps.SwitchedRunners && caps.VariableIntakeRunner, "The M54's DISA is not authored as two stages driven by its ECU (Phase 1 authors it, with provenance).");
        var tune = db.GetTune(db.GetEngine(M54).StockTune);
        IReadOnlyList<EngineTelemetry> Run(TuneDocument t) => IntakeRig.Sweep(db, TestContent.StockM54(), "gasoline_98", t, 1000, 6500, 100);

        var closed = Run(tune with { IntakeRunnerSwitchRpm = null });
        var open = Run(tune with { IntakeRunnerSwitchRpm = 500 });
        double? crossover = IntakeRig.Crossover(open, closed, p => p.VeDynamic);
        Assert.True(crossover is >= switchBandLow and <= switchBandHigh, $"DISA stage crossover {crossover?.ToString("F0") ?? "none"} rpm, sourced band {switchBandLow}–{switchBandHigh}");
        Assert.NotNull(tune.IntakeRunnerSwitchRpm);
        Assert.InRange(tune.IntakeRunnerSwitchRpm!.Value, switchBandLow, switchBandHigh);

        IntakeRig.AssertStagesActThroughFilling(closed, open);
        var shipped = Run(tune);
        IntakeRig.AssertSwitchFollowsTheUpperEnvelope(closed, open, shipped, tune.IntakeRunnerSwitchRpm.Value);
        var peakTorque = shipped.MaxBy(p => p.Torque)!;
        Assert.InRange(peakTorque.Torque, 270, 330);
        Assert.InRange(shipped.Max(p => p.PowerKw), 144.5, 195.5);

        double at1500 = shipped.Single(p => p.Rpm == 1500).Torque;
        output.WriteLine($"Held-out validation (reported, not asserted): peak {peakTorque.Torque:F1} N·m at {peakTorque.Rpm:F0} rpm " +
                         $"(reference 300 N·m at 3,500), {peakTorque.Torque / at1500 - 1:P1} above 1,500 rpm; peak power {shipped.Max(p => p.PowerKw):F1} kW (reference 170).");
        foreach (var p in shipped.Where(p => p.Rpm % 500 == 0))
            output.WriteLine($"  {p.Rpm,5:F0} rpm  {p.Torque,6:F1} N·m  {p.PowerKw,6:F1} kW  stage {(p.SwitchedRunner ? "open" : "closed")}");
    }
}

/// <summary>
/// Guards for Intake Gas Dynamics 2.0 that hold today and must keep holding after it (docs/milestones/
/// INTAKE_GAS_DYNAMICS_2.md, "Acceptance tests"): bounded filling and fuelling on target for every family on its
/// calibration fuel, stage switching with hysteresis, the phaser still moving the valve-event optimum, and the
/// per-step allocation of the engine model. The regression fingerprint and the source audit are guards too.
/// </summary>
[Trait("Milestone", "IntakeGasDynamics2")]
public class IntakeGasDynamicsGuardTests
{
    /// <summary>Every family on its tune's calibration fuel (tools/CarSim.Verification/tune-manifest.json).</summary>
    public static TheoryData<string, string> Families()
    {
        var data = new TheoryData<string, string>();
        foreach (var recipe in TuneManifest.Load(RepoPaths.TuneManifest).Tunes)
            if (TestContent.Matrix.GetEngine(recipe.Engine).StockTune == recipe.Tune) data.Add(recipe.Engine, recipe.Fuel);
        return data;
    }

    /// <summary>
    /// Matrix items 8–9: finite, bounded filling and λ on target at full load across the rev range, for NA, turbo,
    /// multi-bank, phased, VVL and variable-intake families. Bounds: 0 &lt; VE_dyn ≤ 1.35 (ram and resonance gains of
    /// production intakes stay well under +35 %); λ within 4 % of the tune's target (fuel maps on target — the
    /// recalibration driver regenerates them after the physics change).
    /// </summary>
    [Theory, MemberData(nameof(Families))]
    public void FullLoadFillingIsBoundedAndFuelledOnTarget(string family, string fuel)
    {
        var db = TestContent.Matrix;
        var tune = db.GetTune(db.GetEngine(family).StockTune);
        var engine = IntakeRig.Build(db, family);
        var points = IntakeRig.Sweep(db, engine, fuel, tune, 1500, tune.RevLimitRpm - 500, 500);
        Assert.All(points, p =>
        {
            Assert.True(double.IsFinite(p.VeDynamic) && p.VeDynamic > 0 && p.VeDynamic <= 1.35, $"{p.Rpm:F0} rpm: VE_dyn {p.VeDynamic:R}");
            Assert.True(double.IsFinite(p.VolumetricEfficiency) && p.VolumetricEfficiency > 0, $"{p.Rpm:F0} rpm: VE {p.VolumetricEfficiency:R}");
            Assert.True(Math.Abs(p.Lambda / p.TargetLambda - 1.0) <= 0.04, $"{family} {p.Rpm:F0} rpm: λ {p.Lambda:F3} against target {p.TargetLambda:F3}");
        });
    }

    /// <summary>
    /// Matrix item 4: a switched stage drops back only below its switch speed less the hysteresis — approached from above,
    /// 50 rpm under the switch speed it stays switched; 200 rpm under, it is back on the primary stage.
    /// </summary>
    [Fact]
    public void AStageSwitchHasHysteresis()
    {
        var db = TestContent.Matrix;
        var tune = db.GetTune(db.GetEngine("syn_i6_vis").StockTune);
        double on = tune.IntakeRunnerSwitchRpm!.Value;
        var ecu = EcuTune.FromDocument(tune);
        var config = EngineConfiguration.Build(IntakeRig.Build(db, "syn_i6_vis"), db.GetFuel("gasoline_95"),
            new ValidationContext(ecu.RevLimitRpm, ecu.MaxBoostTargetKpa)).GetOrThrow();
        var sim = new EngineSimulation(config, ecu, EngineState.Warm()) { DamageEnabled = false };
        bool StageAt(double rpm) => SteadyStateSweep.Settle(sim, rpm, 1.0, 0.5).SwitchedRunner;
        Assert.False(StageAt(on - 300));
        Assert.True(StageAt(on + 100));
        Assert.True(StageAt(on - 50));
        Assert.False(StageAt(on - 200));
        Assert.False(StageAt(on - 50));
    }

    /// <summary>
    /// Matrix item 5 (first half): the phaser still moves the valve-event optimum — with the runner fixed, the fixed cam
    /// advance that traps the most air is at least 20° earlier at 1,500 rpm than at 5,500 rpm (M54: 60° VANOS).
    /// </summary>
    [Fact]
    public void ThePhaserStillMovesTheValveEventOptimum()
    {
        var db = TestContent.Database;
        var tune = db.GetTune(db.GetEngine(TestContent.M54).StockTune);
        double Best(double rpm) => Enumerable.Range(0, 7).Select(i => i * 10.0).MaxBy(advance =>
            IntakeRig.Sweep(db, TestContent.StockM54(), "gasoline_98", IntakeRig.WithCamAt(tune, advance), rpm, rpm, 100)[0].AirPerCycle);
        Assert.True(Best(1500) >= Best(5500) + 20, $"best advance {Best(1500)}° at 1,500 rpm, {Best(5500)}° at 5,500 rpm");
    }

    /// <summary>
    /// Acceptance criterion 6 (allocation half): no additional per-step allocation. Budgets are the Phase 0 measurement
    /// (full load, 5,000 rpm held, 2 ms steps: K20 and M54 10,360 B, twin-turbo V6 28,032 B) plus 2 % for runtime patch
    /// differences. Step time (+10 %) is measured with `carsim bench` against the Phase 0 numbers, not in a unit test.
    /// </summary>
    [Theory]
    [InlineData(TestContent.K20, "gasoline_95", 10_568)]
    [InlineData(TestContent.M54, "gasoline_98", 10_568)]
    [InlineData("syn_v6_tt", "gasoline_98", 28_593)]
    public void AnEngineStepAllocatesNoMoreThanBeforeTheMilestone(string family, string fuel, long budgetBytes)
    {
        var db = TestContent.Matrix;
        var tune = EcuTune.FromDocument(db.GetTune(db.GetEngine(family).StockTune));
        var config = EngineConfiguration.Build(IntakeRig.Build(db, family), db.GetFuel(fuel),
            new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa)).GetOrThrow();
        var sim = new EngineSimulation(config, tune, EngineState.Warm());
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 5000, CoolantTemperatureOverride = 363.15 };
        for (int i = 0; i < 2000; i++) sim.Step(0.002, input);
        const int steps = 4000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < steps; i++) sim.Step(0.002, input);
        long perStep = (GC.GetAllocatedBytesForCurrentThread() - before) / steps;
        Assert.True(perStep <= budgetBytes, $"{family}: {perStep} B per engine step (budget {budgetBytes} B)");
    }
}

/// <summary>
/// The K20 anchor of Intake Gas Dynamics 2.0 (U7; docs/milestones/INTAKE_GAS_DYNAMICS_2_PHASE1_PROPOSAL.md, "Regression
/// rules"). The reference is the stock K20 on RON 95 after the pre-physics tune regeneration (commit 91d3ffa), frozen here
/// and never re-pinned by the physics change it guards. Three levels:
/// <list type="number">
/// <item>Numerical: the regression fingerprint, bit-identical wherever neither code nor content changed (not repeated
/// here).</item>
/// <item>Physics: full-load trapped air per cycle within ±3 % at every 250 rpm point from 1,500 to 7,500 rpm. On this
/// configuration (naturally aspirated, fixed cams, no switched hardware) the fuel map cannot move the air: the
/// pre-physics regeneration moved it by less than 0.001 %, so a failure here is physics, never a tune change.</item>
/// <item>Calibration (after the tune driver has regenerated the tables under the new physics): peak torque, peak power
/// and 0–100 km/h within ±3 %, with λ on target (<see cref="IntakeGasDynamicsGuardTests"/>). A change larger than the
/// air explains is a calibration effect and must be attributed in the report (λ, spark and knock, friction share).</item>
/// </list>
/// ±3 %: the new runner term may reshape the anchor's filling by about half its amplitude (A ≈ 0.03–0.10), which the two
/// re-anchored valve-event constants absorb only in part. The bound lets that documented generic correction through and
/// stops anything larger, and it is more than 3,000 times the largest tune effect on the physics metric.
/// </summary>
[Trait("Milestone", "IntakeGasDynamics2")]
public class K20AnchorGuardTests
{
    /// <summary>Full-load air per cycle and cylinder (mg), 1,500–7,500 rpm in 250 rpm steps, 1 s settle, RON 95.</summary>
    private static readonly double[] ReferenceAirPerCycleMg =
    {
        442.4282685119624, 460.79485074917534, 477.47237619492995, 492.45431879143405, 505.6952296281729, 517.2718962114799,
        527.1215656759557, 534.9065574434306, 540.9843262989328, 545.3416581390736, 548.1012750690093, 549.2735759016139,
        549.0018203422125, 547.3173064337817, 544.3649160260858, 540.1867275622138, 535.1209108309196, 529.4854957935461,
        523.3569184564205, 516.7646901845442, 509.77225341431915, 502.40238033727525, 494.7136609669049, 486.723727251404,
        478.48332624422636,
    };

    private const double ReferencePeakTorqueNm = 189.21184907830153, ReferencePeakPowerKw = 110.98506225347931, ReferenceZeroTo100S = 8.8;
    public const double PhysicsTolerance = 0.03, CalibrationTolerance = 0.03;

    private static IReadOnlyList<EngineTelemetry> Sweep()
    {
        var db = TestContent.Database;
        return IntakeRig.Sweep(db, TestContent.StockK20(), "gasoline_95", db.GetTune(db.GetEngine(TestContent.K20).StockTune), 1500, 7500, 250);
    }

    [Fact]
    public void TheStockK20FillsWithinThePhysicsTolerance()
    {
        var sweep = Sweep();
        Assert.Equal(ReferenceAirPerCycleMg.Length, sweep.Count);
        Assert.All(sweep.Zip(ReferenceAirPerCycleMg), p =>
            Assert.True(Math.Abs(p.First.AirPerCycle * 1e6 / p.Second - 1) <= PhysicsTolerance,
                $"{p.First.Rpm:F0} rpm: air per cycle {p.First.AirPerCycle * 1e6:F1} mg against the anchor's {p.Second:F1} mg"));
    }

    [Fact]
    public void TheStockK20OutputStaysWithinTheCalibrationTolerance()
    {
        var sweep = Sweep();
        Assert.InRange(sweep.Max(p => p.Torque) / ReferencePeakTorqueNm - 1, -CalibrationTolerance, CalibrationTolerance);
        Assert.InRange(sweep.Max(p => p.PowerKw) / ReferencePeakPowerKw - 1, -CalibrationTolerance, CalibrationTolerance);
        Assert.InRange(Vehicles.Car.Accelerate(Vehicles.Car.Chassis(), 100) / ReferenceZeroTo100S - 1, -CalibrationTolerance, CalibrationTolerance);
    }
}
