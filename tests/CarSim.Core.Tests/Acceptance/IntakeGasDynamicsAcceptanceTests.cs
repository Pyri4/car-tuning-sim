using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;
using CarSim.Verification;
using CarSim.Verification.Calibration;

namespace CarSim.Core.Tests.Acceptance;

/// <summary>
/// System-level acceptance tests of Intake Gas Dynamics 2.0, written in Phase 0 against today's public API
/// (docs/milestones/INTAKE_GAS_DYNAMICS_2.md, "Acceptance tests"; the model they will hold is SIMULATION_SPEC.md,
/// "Intake gas dynamics 2.0 — proposed model"). Each one fails on today's model, where valve timing and runner
/// gas dynamics are one filling hump that a cam phaser moves whole; the failure of each was recorded when it was written.
/// Tolerances are physical (wave speed ∝ √T, tuned speed between L^−½ and L^−1), not fitted to any engine.
/// Geometry marked "test-only" is a generic probe, not data for the engine it is fitted to.
/// </summary>
[Trait("Milestone", "IntakeGasDynamics2")]
public class IntakeGasDynamicsAcceptanceTests
{
    private const string M54 = TestContent.M54, K20 = TestContent.K20;

    /// <summary>
    /// Matrix item 5: the runner's response belongs to the runner. With two runner lengths on the same cams at the same
    /// cam phase, the valve-event part of filling cancels; the speed above which the shorter runner out-fills the longer
    /// one must stay put (±10 %) when the phaser moves the valve event by 40°. Today the phaser carries the runner's
    /// effect with it (the crossover moves with the tuned speed of the whole hump).
    /// </summary>
    [PendingAcceptanceFact]
    public void TheRunnerCrossoverDoesNotMoveWithTheCamPhaser()
    {
        var db = IntakeRig.Content(("m54.intake.disa", "test.intake.m54_short", s => s["runner_length_mm"] = 250)); // test-only geometry
        var tune = db.GetTune(db.GetEngine(M54).StockTune);
        double? At(double advance)
        {
            var phased = IntakeRig.WithCamAt(tune, advance);
            var longRunner = IntakeRig.Sweep(db, IntakeRig.Build(db, M54), "gasoline_98", phased, 1000, 6400, 100);
            var shortRunner = IntakeRig.Sweep(db, IntakeRig.Build(db, M54, ("intake_manifold", "test.intake.m54_short")), "gasoline_98", phased, 1000, 6400, 100);
            return IntakeRig.Crossover(shortRunner, longRunner, p => p.VeDynamic);
        }
        double? early = At(50), late = At(10);
        Assert.True(early is >= 1500 and <= 6400, $"runner crossover with the intake cam at 50°: {early?.ToString("F0") ?? "none"} rpm");
        Assert.True(late is >= 1500 and <= 6400, $"runner crossover with the intake cam at 10°: {late?.ToString("F0") ?? "none"} rpm");
        Assert.InRange(early!.Value / late!.Value, 0.90, 1.10);
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
        Assert.True(predicted > 0.05, $"the charge should heat up: {cold.Charge:F0} → {hot.Charge:F0} K");
        Assert.InRange(measured, 0.5 * predicted, 1.5 * predicted);
    }

    /// <summary>
    /// Matrix items 3–4 and acceptance criterion 1 (generic part): a two-stage intake must act on an engine whose cams
    /// are phased by its calibrated map — each stage winning by ≥ 2 % full-load torque in its own speed range — as a
    /// DISA does. Test-only stage geometry (380/250 mm) on the M54 with an ECU that can drive it; the shipped VANOS map.
    /// Today the runner is inert under a phaser (M54 investigation E4: within 1 N·m).
    /// </summary>
    [PendingAcceptanceFact]
    public void ASwitchedRunnerActsOnAPhasedEngine()
    {
        var db = IntakeRig.Content(
            ("m54.intake.disa", "test.intake.m54_two_stage", s => s["switched_runner_length_mm"] = 250), // test-only geometry
            ("ecu.m54_oem", "test.ecu.m54_runner_control", s => s["intake_runner_control"] = true));
        var engine = IntakeRig.Build(db, M54, ("intake_manifold", "test.intake.m54_two_stage"), ("ecu", "test.ecu.m54_runner_control"));
        var tune = db.GetTune(db.GetEngine(M54).StockTune);
        var primary = IntakeRig.Sweep(db, engine, "gasoline_98", tune with { IntakeRunnerSwitchRpm = null }, 1000, 6400, 100);
        engine = IntakeRig.Build(db, M54, ("intake_manifold", "test.intake.m54_two_stage"), ("ecu", "test.ecu.m54_runner_control"));
        var switched = IntakeRig.Sweep(db, engine, "gasoline_98", tune with { IntakeRunnerSwitchRpm = 500 }, 1000, 6400, 100);
        Assert.All(switched.Where(p => p.Rpm >= 1000), p => Assert.True(p.SwitchedRunner));
        var primaryWins = primary.Zip(switched).Where(p => p.First.Torque >= 1.02 * p.Second.Torque).Select(p => p.First.Rpm).ToList();
        var switchedWins = primary.Zip(switched).Where(p => p.Second.Torque >= 1.02 * p.First.Torque).Select(p => p.First.Rpm).ToList();
        Assert.True(primaryWins.Count > 0 && switchedWins.Count > 0,
            $"long stage ≥ 2 % better at [{string.Join(", ", primaryWins)}] rpm, short stage at [{string.Join(", ", switchedWins)}] rpm");
        Assert.True(primaryWins.Min() < switchedWins.Max(), "the long stage must win below the short one");
    }

    /// <summary>
    /// Acceptance criterion 1, with the M54's own data: DISA authored with its effective geometry and provenance (Phase 1),
    /// generic code only. The full-load curve rises from 1,500 rpm into a mid-range peak (2,750–4,750 rpm, ≥ 3 % above
    /// the 1,500 rpm torque) instead of today's plateau, and peak torque and power stay in their bands (±10 % / ±15 %).
    /// The historical curve need not be matched. Today DISA is not authored and the curve plateaus from 1,500 rpm.
    /// </summary>
    [PendingAcceptanceFact]
    public void TheM54RisesIntoAMidRangePeakWithItsDisaAuthored()
    {
        var db = TestContent.Database;
        var engine = db.GetEngine(M54);
        var intake = TestContent.StockM54().PartIn("intake_manifold")!.Spec<IntakeManifoldSpec>();
        Assert.True(intake.SwitchedRunnerLengthMm != null, "The M54's DISA stage is not authored (Phase 1 authors it, with provenance).");
        var tune = db.GetTune(engine.StockTune);
        Assert.NotNull(tune.IntakeRunnerSwitchRpm);
        var curve = IntakeRig.Sweep(db, TestContent.StockM54(), "gasoline_98", tune, 1000, 6500, 250);
        var peak = curve.MaxBy(p => p.Torque)!;
        double at1500 = curve.Single(p => p.Rpm == 1500).Torque;
        Assert.InRange(peak.Rpm, 2750, 4750);
        Assert.True(peak.Torque >= 1.03 * at1500, $"peak {peak.Torque:F1} N·m at {peak.Rpm:F0} rpm against {at1500:F1} N·m at 1,500 rpm");
        Assert.InRange(peak.Torque, 270, 330);
        Assert.InRange(curve.Max(p => p.PowerKw), 144.5, 195.5);
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
