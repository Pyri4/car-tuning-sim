using System.Text.Json.Nodes;
using CarSim.Core.Content;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Simulation;
using CarSim.Core.Tests.Acceptance;
using CarSim.Verification;
using CarSim.Verification.Calibration;

namespace CarSim.Core.Tests.Simulation;

/// <summary>
/// Component tests of the intake gas-dynamics model (Intake Gas Dynamics 2.0, Phase 1; the proposal's section 5.3):
/// the runner–cylinder fundamental against its limits, the normalised response, the bounds of the gain, the tuned speed's
/// independence of cam timing and its √T scaling, and a unique crossover between two stages. The system-level
/// relationships are <see cref="IntakeGasDynamicsAcceptanceTests"/>.
/// </summary>
public class IntakeGasDynamicsTests
{
    // ---- The fundamental x·tan x = β -----------------------------------------------------------------------------------

    [Fact]
    public void TheFundamentalSolvesTheDistributedRunnerEquationAndMeetsBothLimits()
    {
        foreach (double beta in new[] { 1e-6, 1e-3, 0.1, 0.5, 1.0, 1.16, 2.0, 10.0, 1e3, 1e6 })
        {
            double x = IntakeGasDynamics.Fundamental(beta);
            Assert.InRange(x, 0.0, Math.PI / 2);
            Assert.Equal(beta, x * Math.Tan(x), beta * 1e-9);
        }
        // Helmholtz (lumped) as the runner's volume vanishes against the cylinder's; the quarter wave as it dominates.
        Assert.Equal(1.0, IntakeGasDynamics.Fundamental(1e-8) / Math.Sqrt(1e-8), 1e-8);
        Assert.Equal(Math.PI / 2, IntakeGasDynamics.Fundamental(1e8), 1e-7);
        // c(β) = x/√β falls monotonically from 1 (Helmholtz) toward the quarter wave; c₁ = 0.8603 is the root at β = 1.
        Assert.Equal(0.8603, IntakeGasDynamics.ReferenceFundamental, 4);
        double previous = 1.0;
        foreach (double beta in new[] { 0.01, 0.3, 1.0, 1.1, 2.0, 5.0 })
        {
            double c = IntakeGasDynamics.Fundamental(beta) / Math.Sqrt(beta);
            Assert.True(c < previous, $"c({beta}) = {c}");
            previous = c;
        }
    }

    [Fact]
    public void TheTunedSpeedIsEngelmansAtTheReferenceVolumeRatio()
    {
        // At β = β_ref the distributed form equals the lumped rule it is normalised to: N_t = 60·f_H/K,
        // f_H = (a/2π)·√(A/(L_eff·V_eff)).
        const double lEff = 0.35, a = 346.0;
        double area = IntakeGasDynamics.ReferenceVolumeRatio * 3.0e-4 / lEff; // β = A·L/V = β_ref with V = 3.0e-4 m³
        double helmholtz = a / (2 * Math.PI) * Math.Sqrt(area / (lEff * 3.0e-4));
        double x = IntakeGasDynamics.Fundamental(IntakeGasDynamics.ReferenceVolumeRatio);
        Assert.Equal(60 * helmholtz / IntakeGasDynamics.TunedFrequencyRatio, IntakeGasDynamics.TunedRpm(x, lEff, a), 1e-9);
    }

    [Fact]
    public void AStageIsBuiltFromItsGeometryAndTheBanksOwnCylinder()
    {
        var bank = SimFactory.Create().Config.Banks[0];
        var stage = bank.RunnerStages[0];
        var g = bank.Geometry;
        Assert.True(stage.DefaultDiameter);
        Assert.Equal(RunnerStageConfiguration.DefaultDiameterPerBore * g.Bore, stage.Diameter, 12);
        Assert.Equal(0.340 + IntakeGasDynamics.EndCorrection * stage.Diameter / 2, stage.EffectiveLength, 12);
        Assert.Equal(g.ClearanceVolume + g.SweptVolumePerCylinder / 2, stage.EffectiveCylinderVolume, 15);
        Assert.Equal(stage.Area * stage.EffectiveLength / stage.EffectiveCylinderVolume, stage.VolumeRatio, 12);
        // The mid-stroke volume is V_d·(CR + 1)/(2·(CR − 1)).
        double cr = g.CompressionRatio;
        Assert.Equal(g.SweptVolumePerCylinder * (cr + 1) / (2 * (cr - 1)), stage.EffectiveCylinderVolume, 12);
    }

    // ---- The response R̃ -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheResponseIsOneAtTheTunedSpeedPositiveAndUnimodal()
    {
        Assert.Equal(1.0, IntakeGasDynamics.Response(1.0), 14);
        Assert.Equal(0.0, IntakeGasDynamics.Response(0.0));
        double previous = 0.0;
        for (double r = 0.01; r <= 1.0 + 1e-12; r += 0.01)
        {
            double v = IntakeGasDynamics.Response(r);
            Assert.True(v > 0 && v <= 1.0 + 1e-15 && v >= previous, $"R̃({r:F2}) = {v}");
            previous = v;
        }
        for (double r = 1.01; r <= 20.0; r += 0.01)
        {
            double v = IntakeGasDynamics.Response(r);
            Assert.True(v > 0 && v < previous, $"R̃({r:F2}) = {v}");
            previous = v;
        }
        // r_p is the quadrature curve's peak: R has zero slope there.
        double rp = IntakeGasDynamics.ResponsePeakRatio, h = 1e-6;
        Assert.Equal(0.0, (IntakeGasDynamics.Quadrature(rp + h, IntakeGasDynamics.Damping) - IntakeGasDynamics.Quadrature(rp - h, IntakeGasDynamics.Damping)) / (2 * h), 6);
    }

    [Fact]
    public void BetweenTwoStagesTunedSpeedsThereIsExactlyOneCrossover()
    {
        // Two stages sharing the runner area share the amplitude, so where one out-fills the other depends only on R̃.
        foreach (double spacing in new[] { 1.05, 1.15, 1.3, 1.6, 2.0, 3.0 })
        {
            const double low = 3000.0;
            double high = low * spacing;
            int changes = 0;
            double last = double.NaN;
            for (double n = 100; n <= 3 * high; n += 5)
            {
                double d = IntakeGasDynamics.Response(n / high) - IntakeGasDynamics.Response(n / low);
                if (!double.IsNaN(last) && Math.Sign(d) != Math.Sign(last))
                {
                    changes++;
                    Assert.InRange(n, low, high + 5);
                }
                last = d;
            }
            Assert.Equal(1, changes);
        }
    }

    // ---- The gain: bounded, no cam input, √T -------------------------------------------------------------------------

    [Fact]
    public void TheGainStaysWithinOneAndOnePlusTheAmplitudeBoundForAnyAcceptedGeometry()
    {
        // Over the validator's whole range of runner length and diameter, any speed, runner gas temperature and event.
        foreach (double lengthMm in new[] { 50.0, 150, 340, 600, 1000 })
        foreach (double diameterMm in new[] { 15.0, 30, 60, 120 })
        foreach (double veffCc in new[] { 20.0, 300, 3000 })
        {
            double area = Math.PI * diameterMm * diameterMm / 4e6, lEff = lengthMm / 1000 + IntakeGasDynamics.EndCorrection * diameterMm / 2000;
            double x = IntakeGasDynamics.Fundamental(area * lEff / (veffCc * 1e-6));
            foreach (double t in new[] { 200.0, 298.15, 500 })
            {
                double a = IntakeGasDynamics.SpeedOfSound(t), tuned = IntakeGasDynamics.TunedRpm(x, lEff, a);
                Assert.True(double.IsFinite(tuned) && tuned > 0);
                foreach (double rpm in new[] { 0.0, 1, 500, 3000, 8000, 20000 })
                foreach (double fraction in new[] { 0.2, 0.4, 1.0 })
                {
                    double g = IntakeGasDynamics.Gain(rpm, tuned, IntakeGasDynamics.RunnerMach(rpm, veffCc * 1e-6, area, fraction, a));
                    Assert.True(g >= 1.0 && g <= 1.0 + IntakeGasDynamics.MaxAmplitude, $"L {lengthMm}, d {diameterMm}, {rpm} rpm, {t} K: G {g}");
                }
            }
        }
    }

    [Fact]
    public void TheTunedSpeedAndGainHaveNoCamInput()
    {
        // The M54's 60° intake phaser, held anywhere: the wave gain and the tuned speed are bit-identical, only the
        // valve-event filling moves.
        var db = TestContent.Database;
        var tune = db.GetTune(db.GetEngine(TestContent.M54).StockTune);
        var sweeps = new[] { 0.0, 20, 40, 60 }.Select(advance =>
            IntakeRig.Sweep(db, TestContent.StockM54(), "gasoline_98", IntakeRig.WithCamAt(tune, advance), 1500, 6000, 1500)).ToList();
        for (int i = 0; i < sweeps[0].Count; i++)
        {
            Assert.All(sweeps, s => Assert.Equal(sweeps[0][i].IntakeWaveGain, s[i].IntakeWaveGain));
            Assert.All(sweeps, s => Assert.Equal(sweeps[0][i].RunnerTunedRpm, s[i].RunnerTunedRpm));
        }
        Assert.True(sweeps[0][0].AirPerCycle != sweeps[3][0].AirPerCycle, "the phaser still moves the valve-event filling");
    }

    [Fact]
    public void TheTunedSpeedScalesExactlyWithTheSpeedOfSound()
    {
        var bank = SimFactory.Create().Config.Banks[0];
        var stage = bank.RunnerStages[0];
        var path = new AirPath(bank);
        foreach (double t in new[] { 230.0, 263.15, 323.15, 420 })
        {
            double s = Math.Sqrt(t / IntakeGasDynamics.ReferenceTemperature);
            Assert.Equal(s, stage.TunedRpm(t) / stage.TunedRpmAtReference, 12);
            // Similarity: speed and speed of sound scaled together leave the whole response (placement and Mach amplitude)
            // unchanged, so the gain at N·√(T/T_ref) and T equals the gain at N and T_ref.
            foreach (double rpm in new[] { 1500.0, 4000, 7000 })
                Assert.Equal(path.WaveGain(rpm, IntakeGasDynamics.ReferenceTemperature), path.WaveGain(rpm * s, t), 12);
        }
    }

    [Fact]
    public void TheEngineReadsTheRunnerGasOfThePreviousStep()
    {
        // Naturally aspirated: the manifold gas is ambient, so the tuned speed follows the ambient temperature exactly.
        var db = TestContent.Database;
        var tune = db.GetTune(db.GetEngine(TestContent.K20).StockTune);
        var cold = IntakeRig.Sweep(db, TestContent.StockK20(), "gasoline_95", tune, 4000, 4000, 100, 263.15)[0];
        var hot = IntakeRig.Sweep(db, TestContent.StockK20(), "gasoline_95", tune, 4000, 4000, 100, 323.15)[0];
        Assert.Equal(263.15, cold.ManifoldTemperature, 9);
        Assert.Equal(Math.Sqrt(323.15 / 263.15), hot.RunnerTunedRpm / cold.RunnerTunedRpm, 12);
        // Boosted: the intercooled charge is warmer than ambient, and the runner wave follows it.
        var turbo = TurboTests.TurboSim(TurboTests.TurboBuild());
        var t = SimFactory.At(turbo, 6000);
        Assert.True(t.ManifoldTemperature > 300);
        Assert.Equal(turbo.Config.Banks[0].RunnerStages[0].TunedRpm(t.ManifoldTemperature), t.RunnerTunedRpm, t.RunnerTunedRpm * 1e-3);
    }

    // ---- The M54 DISA derivation (A-D1) and N-stage switch speeds -----------------------------------------------------

    [Fact]
    public void TheDisaDerivationReproducesTheSourcedSwitchSpeedFromTheFrozenGeometry()
    {
        // docs/milestones/intake-gas-dynamics-2/M54_DISA_DERIVATION.md: the closed stage is derived so that the two stages'
        // gains cross at 3,925 rpm (the centre of the sourced 3,750–4,100 rpm band). The content carries it frozen at 0.1 mm.
        var bank = EngineConfiguration.Build(TestContent.StockM54(), TestContent.Database.GetFuel("gasoline_98")).GetOrThrow().Banks[0];
        Assert.Equal(2, bank.RunnerStages.Count);
        var (closed, open) = (bank.RunnerStages[0], bank.RunnerStages[1]);
        Assert.Equal(469.1, closed.Spec.RunnerLengthMm);
        Assert.Equal(380.0, open.Spec.RunnerLengthMm);
        Assert.Equal(closed.Diameter, open.Diameter);
        Assert.Equal(3925.0, RunnerStageDerivation.Crossover(closed.TunedRpmAtReference, open.TunedRpmAtReference), 2.0);

        var derived = RunnerStageDerivation.LowerStage(380, null, bank.Geometry, 3925);
        Assert.Equal(open.TunedRpmAtReference, derived.UpperTunedRpm, 1e-9);
        Assert.Equal(469.1, derived.LowerLengthMm!.Value, 0.05);
        Assert.Equal(3925.0, derived.CrossoverRpm, 1e-3);
        // The proposal's exclusions: a 450 mm open stage or a 30 mm runner tunes below the sourced band.
        Assert.Throws<ArgumentException>(() => RunnerStageDerivation.LowerStage(450, null, bank.Geometry, 3925));
        Assert.Throws<ArgumentException>(() => RunnerStageDerivation.LowerStage(380, 30, bank.Geometry, 3925));
    }

    [Fact]
    public void TheTuneDriverSwitchesAThreeStageIntakeOntoItsUpperEnvelope()
    {
        // A test-only three-stage intake on the fixed-cam I6: the driver's runner_switch step sets each stage's switch speed
        // at the crossover of the stages below and above it, and the switched engine then runs the best stage everywhere
        // away from the switches (± the hysteresis).
        var db = IntakeRig.Content(("syn.r6.intake", "test.intake.three_stage", s =>
        {
            s.Remove("switched_runner_length_mm");
            s["switched_stages"] = new JsonArray(new JsonObject { ["runner_length_mm"] = 330 }, new JsonObject { ["runner_length_mm"] = 200 });
        }));
        var manifest = TuneManifest.Load(RepoPaths.TuneManifest).Tunes.Single(t => t.Tune == "syn.r6.stock");
        var recipe = manifest with { Build = new BuildRecipe(new[] { new[] { "intake_manifold", "test.intake.three_stage" } }, null) };
        var start = db.GetTune(recipe.Tune) with { IntakeRunnerUpperSwitchRpm = new[] { 7000.0 } };
        var doc = TuneRegenerator.RunnerSwitchSpeeds(db, recipe, start);
        Assert.NotNull(doc.IntakeRunnerSwitchRpm);
        Assert.Single(doc.IntakeRunnerUpperSwitchRpm!);
        Assert.True(doc.IntakeRunnerUpperSwitchRpm![0] > doc.IntakeRunnerSwitchRpm!.Value, $"{doc.IntakeRunnerSwitchRpm} then {doc.IntakeRunnerUpperSwitchRpm[0]} rpm");
        Assert.Empty(doc.Validate());

        var engine = IntakeRig.Build(db, "syn_i6_vis", ("intake_manifold", "test.intake.three_stage"));
        IReadOnlyList<EngineTelemetry> Run(TuneDocument t) => IntakeRig.Sweep(db, engine, "gasoline_95", t, 1500, 6800, 100);
        var held = Enumerable.Range(0, 3).Select(k => Run(TuneRegenerator.HoldingStage(doc, k))).ToList();
        var switched = Run(doc);
        double[] switches = { doc.IntakeRunnerSwitchRpm.Value, doc.IntakeRunnerUpperSwitchRpm[0] };
        for (int i = 0; i < switched.Count; i++)
        {
            if (switches.Any(s => Math.Abs(switched[i].Rpm - s) <= EcuController.SwitchHysteresisRpm)) continue;
            double best = held.Max(h => h[i].Torque);
            Assert.True(switched[i].Torque >= best * (1 - 0.005), $"{switched[i].Rpm:F0} rpm: {switched[i].Torque:F1} N·m against the best stage's {best:F1}");
        }
        Assert.Equal(new[] { 0, 1, 2 }, switched.Select(p => p.RunnerStage).Distinct().OrderBy(x => x));
    }

    // ---- Every family, and spec fuzz over the new fields --------------------------------------------------------------

    [Fact]
    public void EveryFamilysTunedSpeedsLieInItsRevRangeAndItsGainIsBounded()
    {
        var db = TestContent.Matrix;
        foreach (var engine in db.Engines.Values)
        {
            var tune = EcuTune.FromDocument(db.GetTune(engine.StockTune));
            var config = EngineConfiguration.Build(IntakeRig.Build(db, engine.Id), db.GetFuel("gasoline_98"), new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa)).GetOrThrow();
            foreach (var bank in config.Banks)
            {
                Assert.All(bank.RunnerStages, s => Assert.InRange(s.TunedRpmAtReference, 2000, tune.RevLimitRpm));
                var path = new AirPath(bank);
                for (int stage = 0; stage < bank.RunnerStages.Count; stage++)
                    for (double rpm = 0; rpm <= tune.RevLimitRpm + 500; rpm += 250)
                        Assert.InRange(path.WaveGain(rpm, 298.15, 0, stage), 1.0, 1.0 + IntakeGasDynamics.MaxAmplitude);
            }
        }
    }

    [Theory]
    [InlineData(TestContent.K20, "k20.intake.oem", "gasoline_95")]
    [InlineData(TestContent.M54, "m54.intake.disa", "gasoline_98")]
    [InlineData("syn_v6_tt", "syn.v6tt.intake", "gasoline_98")]
    [InlineData("syn_v8_dohc_vvt", "syn.v8d.intake", "gasoline_98")]
    public void AnyRunnerGeometryTheValidatorAcceptsRunsToFiniteBoundedNumbers(string engine, string intake, string fuel)
    {
        int ran = 0;
        foreach (double length in new[] { 50.0, 1000 })
        foreach (double diameter in new[] { 15.0, 120 })
        {
            var db = IntakeRig.Content((intake, "test.intake.fuzz", s =>
            {
                s["runner_length_mm"] = length;
                s["runner_diameter_mm"] = diameter;
                s.Remove("switched_runner_length_mm");
                s["switched_stages"] = new JsonArray(new JsonObject { ["runner_length_mm"] = 1050 - length, ["runner_diameter_mm"] = 135 - diameter });
            }));
            var tuneDoc = db.GetTune(db.GetEngine(engine).StockTune) with { IntakeRunnerSwitchRpm = 3000 };
            var tune = EcuTune.FromDocument(tuneDoc);
            var result = EngineConfiguration.Build(IntakeRig.Build(db, engine, ("intake_manifold", "test.intake.fuzz")), db.GetFuel(fuel),
                new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa));
            Assert.True(result.Success, string.Join("; ", result.Report.Errors));
            foreach (var (rpm, throttle) in new[] { (900.0, 0.0), (2500.0, 1.0), (6500.0, 1.0) })
            {
                var sim = new EngineSimulation(result.Configuration!, tune.Clone(), EngineState.Warm()) { DamageEnabled = false };
                var input = new EngineInputs { Throttle = throttle, SpeedMode = SpeedMode.Held, HeldRpm = rpm, CoolantTemperatureOverride = 363.15 };
                EngineTelemetry t = null!;
                for (int i = 0; i < 60; i++) t = sim.Step(0.005, input);
                Assert.True(double.IsFinite(t.Torque) && double.IsFinite(t.AirMassFlow) && double.IsFinite(t.VeDynamic), $"L {length}, d {diameter}, {rpm} rpm");
                Assert.InRange(t.IntakeWaveGain, 1.0, 1.0 + IntakeGasDynamics.MaxAmplitude);
                Assert.True(t.VeDynamic > 0 && t.VeDynamic <= 1.35, $"VE_dyn {t.VeDynamic}");
                ran++;
            }
        }
        Assert.Equal(12, ran);
    }

    [Fact]
    public void TheValidatorSaysWhenTheRunnerDiameterIsTheModelDefault()
    {
        var db = IntakeRig.Content(("k20.intake.oem", "test.intake.k20_measured", s => s["runner_diameter_mm"] = 34.0));
        Assert.Contains(AssemblyValidator.Validate(TestContent.StockK20()).Issues, i => i.Code == "default_intake_geometry" && i.Severity == IssueSeverity.Info);
        var measured = IntakeRig.Build(db, TestContent.K20, ("intake_manifold", "test.intake.k20_measured"));
        Assert.DoesNotContain(AssemblyValidator.Validate(measured).Issues, i => i.Code == "default_intake_geometry");
        var config = EngineConfiguration.Build(measured, db.GetFuel("gasoline_95")).GetOrThrow();
        Assert.False(config.Banks[0].RunnerStages[0].DefaultDiameter);
        Assert.Equal(0.034, config.Banks[0].RunnerStages[0].Diameter, 12);
    }
}
