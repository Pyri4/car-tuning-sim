using CarSim.Core.Common;
using CarSim.Core.Damage;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;
using CarSim.Gameplay;

namespace CarSim.Core.Tests.SecondFamily;

/// <summary>
/// The second engine family against its real-world reference: the BMW M54B30 as fitted to the European E46
/// 330i/330Ci (2000–2006, Siemens MS43, not the ZHP package), rated 170 kW at 5,900 rpm and 300 N·m at 3,500 rpm
/// on RON 98. Sources and the acceptance bands are in PARTS_DATABASE.md ("Isar M54 reference engine") and
/// SIMULATION_SPEC.md ("Second engine family"). Geometry must match exactly; output within bands, not to the
/// decimal: this is a mean-value model whose level-setting constants were fitted to another engine.
/// </summary>
public class M54ReferenceTests
{
    private const string Fuel = "gasoline_98"; // the fuel the reference rating is quoted on

    /// <summary>Published figures of the reference engine.</summary>
    private static class Reference
    {
        public const double DisplacementCc = 2979, BoreMm = 84.0, StrokeMm = 89.6, CompressionRatio = 10.2;
        public const double PowerKw = 170, PowerRpm = 5900, TorqueNm = 300, TorqueRpm = 3500, RevLimitRpm = 6500;
    }

    private static EngineSimulation Stock(string fuel = Fuel, EcuTune? tune = null) =>
        SimFactory.Create(TestContent.StockM54(), fuel, tune);

    private static IReadOnlyList<EngineTelemetry> FullLoadCurve() =>
        SteadyStateSweep.Run(Stock(), 1000, 6250, 250, settleSeconds: 1.0);

    // ---- Topology and geometry -----------------------------------------------------------------

    [Fact]
    public void SixCylinderInlineTopologyIsRepresented()
    {
        var def = TestContent.Database.GetEngine(TestContent.M54);
        Assert.Equal(6, def.Cylinders);
        Assert.Equal("inline", def.Layout);
        Assert.Empty(EngineTopology.CheckFamily(def));
        var a = TestContent.StockM54();
        Assert.Equal(6, a.SpecOf<BlockSpec>(PartCategory.Block)!.Cylinders);
        Assert.Equal(6, a.SpecOf<PistonSpec>(PartCategory.Pistons)!.Count);
        Assert.Equal(6, a.SpecOf<ConnectingRodSpec>(PartCategory.ConnectingRods)!.Count);
        Assert.Equal(6, a.SpecOf<InjectorSpec>(PartCategory.Injectors)!.Count);
        Assert.Equal(4, a.SpecOf<CylinderHeadSpec>(PartCategory.CylinderHead)!.ValvesPerCylinder);

        // Six firings per two revolutions: the fuel the engine burns per second is per-cylinder fuel × 6 × rpm/120.
        var t = SimFactory.At(Stock(), 3000);
        Assert.Equal(t.AirPerCycle * 6 * 3000 / 120.0, t.AirMassFlow, 1e-9);
        // A set of four injectors does not fit a six.
        var wrong = TestContent.StockM54();
        TestContent.Swap(wrong, "injectors", "injectors.310cc");
        Assert.True(AssemblyValidator.Validate(wrong).Has("set_count"));
    }

    [Fact]
    public void GeometryMatchesTheReferenceExactly()
    {
        var g = EngineGeometry.TryCreate(TestContent.StockM54(), out _)!;
        Assert.Equal(6, g.Cylinders);
        Assert.Equal(Reference.BoreMm, Units.MToMm(g.Bore), 9);
        Assert.Equal(Reference.StrokeMm, Units.MToMm(g.Stroke), 9);
        Assert.Equal(135.0, Units.MToMm(g.RodLength), 9);
        Assert.InRange(Units.M3ToCc(g.Displacement), Reference.DisplacementCc - 1, Reference.DisplacementCc + 1);
    }

    [Fact]
    public void CompressionRatioIsDerivedToTheReferenceSpecification()
    {
        // Never authored: chamber 34 cc + 0.7 mm gasket at 85 mm + 0.7 mm deck clearance + 12.1 cc bowl.
        var g = EngineGeometry.TryCreate(TestContent.StockM54(), out _)!;
        Assert.InRange(g.CompressionRatio, Reference.CompressionRatio - 0.05, Reference.CompressionRatio + 0.05);
        var hc = TestContent.StockM54();
        TestContent.Swap(hc, "pistons", "m54.pistons.forged_hc");
        Assert.InRange(EngineGeometry.TryCreate(hc, out _)!.CompressionRatio, 11.2, 11.4);
    }

    [Fact]
    public void StockEngineAssemblesWithoutWarnings()
    {
        var tune = SimFactory.StockTuneOf(TestContent.M54);
        var report = AssemblyValidator.Validate(TestContent.StockM54(), new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa));
        Assert.True(report.CanRun, string.Join("\n", report.Errors));
        Assert.DoesNotContain(report.Issues, i => i.Severity == IssueSeverity.Warning);
    }

    // ---- Start, idle, redline ------------------------------------------------------------------

    [Fact]
    public void StartsOnTheStarterAndIdlesAtItsTargetSpeed()
    {
        var sim = Stock();
        var input = new EngineInputs { Throttle = 0, SpeedMode = SpeedMode.Free, Starter = true };
        double startedAt = double.NaN;
        EngineTelemetry t = null!;
        for (int i = 0; i < 3000; i++) // 15 s
        {
            input.Starter = !sim.State.Running && i < 600;
            t = sim.Step(0.005, input);
            if (double.IsNaN(startedAt) && t.Running) startedAt = t.Time;
        }
        Assert.True(startedAt < 3.0, $"started after {startedAt:F2} s");
        Assert.True(t.Running);
        Assert.InRange(t.Rpm, 700 - 75, 700 + 75);
        // Manifold vacuum at idle. Low for a real engine (≈ 30 kPa): the model has no accessory load, and the K20 idles at
        // ≈ 21 kPa for the same reason. The fuel map covers it, so idle runs on its target λ.
        Assert.InRange(t.MapKpa, 10, 40);
        Assert.True(Math.Abs(t.Lambda / t.TargetLambda - 1) < 0.03, $"idle λ {t.Lambda:F3} vs {t.TargetLambda:F3}");
        Assert.True(t.OilPressure > t.OilPressureRequired, $"oil {t.OilPressureBar:F2} bar at idle");
        Assert.InRange(t.IntakeCamAdvance, 40, 55); // the idle column of the VANOS map
    }

    [Fact]
    public void RespectsItsRevLimitAndTheEcuHardwareCeiling()
    {
        // Free-revving at full throttle, fuel cut holds it at the 6,500 rpm limit (150 rpm hysteresis).
        var sim = Stock();
        SimFactory.At(sim, 3000, 1.0, 0.5);
        var free = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Free, CoolantTemperatureOverride = 363.15 };
        double max = 0;
        for (int i = 0; i < 2000; i++) max = Math.Max(max, sim.Step(0.005, free).Rpm);
        Assert.InRange(max, Reference.RevLimitRpm, Reference.RevLimitRpm + 250);
        Assert.Empty(sim.Damage.Failures);

        // The dyno pull ends at the limiter.
        var run = new DynoRunner(Stock(), new DynoSettings { StartRpm = 2000, EndRpm = 7500 }).RunToCompletion();
        Assert.InRange(run.Samples.Max(s => s.Rpm), Reference.RevLimitRpm - 250, Reference.RevLimitRpm + 50);

        // Beyond the limit fuel is cut; a tune cannot raise the limit past what the ECU hardware allows.
        Assert.False(SimFactory.At(Stock(), 6700).Firing);
        var tune = SimFactory.StockTuneOf(TestContent.M54);
        tune.RevLimitRpm = 9000;
        Assert.Equal(7200, Stock(tune: tune).Ecu.EffectiveRevLimit);
    }

    // ---- Full-load output against the reference ------------------------------------------------

    [Fact]
    public void PeakTorqueIsWithinTheReferenceBand()
    {
        var curve = FullLoadCurve();
        var peak = SimFactory.PeakTorque(curve);
        // Band: ±10 % of the published 300 N·m.
        Assert.InRange(peak.Torque, 0.9 * Reference.TorqueNm, 1.1 * Reference.TorqueNm);
        // At the published torque speed the curve is within 10 % of the rating as well.
        Assert.InRange(curve.Single(p => p.Rpm == Reference.TorqueRpm).Torque, 0.9 * Reference.TorqueNm, 1.1 * Reference.TorqueNm);
    }

    [Fact]
    public void PeakPowerIsWithinTheReferenceBand()
    {
        var peak = SimFactory.PeakPower(FullLoadCurve());
        // Band: ±15 % of the published 170 kW (the same model sits ≈ 7 % under a comparable real K20 at high piston
        // speed; SIMULATION_SPEC.md, "Second engine family"). The peak falls between the rated speed and the limit.
        Assert.InRange(peak.PowerKw, 0.85 * Reference.PowerKw, 1.15 * Reference.PowerKw);
        Assert.InRange(peak.Rpm, 5500, Reference.RevLimitRpm);
    }

    [Fact]
    public void ModelReferenceValuesArePinned()
    {
        // Not a validation target: the model's own numbers, pinned loosely so calibration drift shows up here
        // (compare SIMULATION_SPEC.md, "Calibration reference (Isar M54)").
        var curve = FullLoadCurve();
        var peakT = SimFactory.PeakTorque(curve);
        var peakP = SimFactory.PeakPower(curve);
        Assert.InRange(peakT.Torque, 295, 312);
        Assert.InRange(peakP.PowerKw, 149, 158);
        Assert.InRange(curve.Max(p => p.VolumetricEfficiency), 0.95, 1.03);
        Assert.InRange(curve.Max(p => p.PeakCylinderPressureBar), 60, 75);
    }

    [Fact]
    public void TorqueCurveIsAPlausibleNaturallyAspiratedVariableCamCurve()
    {
        var curve = FullLoadCurve();
        double peak = curve.Max(p => p.Torque);
        foreach (var p in curve)
        {
            Assert.InRange(p.MapKpa, 95, 102); // naturally aspirated: never boosted, only small intake losses
            Assert.True(p.Bmep > 0);
        }
        // Peak BMEP of a good naturally aspirated four-valve engine: 11–14 bar.
        Assert.InRange(Units.PaToBar(curve.Max(p => p.Bmep)), 11, 14);
        // Broad plateau (VANOS): ≥ 85 % of peak torque from 1,500 to 5,000 rpm, ≥ 75 % up to 6,000.
        foreach (var p in curve.Where(p => p.Rpm is >= 1500 and <= 5000)) Assert.True(p.Torque >= 0.85 * peak, $"{p.Rpm} rpm: {p.Torque:F0} N·m");
        foreach (var p in curve.Where(p => p.Rpm is > 5000 and <= 6000)) Assert.True(p.Torque >= 0.75 * peak, $"{p.Rpm} rpm: {p.Torque:F0} N·m");
        // Smooth: no step between 250 rpm points larger than 4 % of peak torque.
        for (int i = 1; i < curve.Count; i++) Assert.True(Math.Abs(curve[i].Torque - curve[i - 1].Torque) < 0.04 * peak, $"step at {curve[i].Rpm}");
        // Power rises to its peak and does not fall back before it.
        var peakP = SimFactory.PeakPower(curve);
        foreach (var p in curve.Where(p => p.Rpm < peakP.Rpm - 1)) Assert.True(p.Power < peakP.Power);
        // The phaser closes the intake early at low speed and parks it (late closing) near the power peak.
        Assert.True(curve.First().IntakeCamAdvance > 40 && curve.Last().IntakeCamAdvance < 1);
    }

    [Fact]
    public void VariableCamTimingIsWhatBroadensTheCurve()
    {
        // The same engine with the phaser parked (a tune without a cam map) loses low-speed torque and keeps its top end.
        var parked = SimFactory.StockTuneOf(TestContent.M54).ToDocument();
        parked.IntakeCamAdvanceDeg = null;
        var withVanos = Stock();
        var withoutVanos = Stock(tune: EcuTune.FromDocument(parked));
        Assert.True(SimFactory.At(withoutVanos, 2000).Torque < 0.9 * SimFactory.At(withVanos, 2000).Torque);
        Assert.Equal(SimFactory.At(withVanos, 6000).Torque, SimFactory.At(withoutVanos, 6000).Torque, 1.0);
    }

    // ---- Fueling -------------------------------------------------------------------------------

    private static double LambdaAt(EngineSimulation sim, double rpm, double throttle = 1.0, double seconds = 1.0) =>
        SimFactory.At(sim, rpm, throttle, seconds).Lambda;

    private static void Near(double expected, double actual, double relative, string what) =>
        Assert.True(Math.Abs(actual / expected - 1) <= relative, $"{what}: expected {expected:F4}, got {actual:F4}");

    [Fact]
    public void ShippedFuelMapIsOnTargetAcrossTheMap()
    {
        foreach (double rpm in new[] { 1500.0, 3000, 4500, 6000 })
            foreach (double throttle in new[] { 0.1, 0.3, 1.0 })
            {
                var t = SimFactory.At(Stock(), rpm, throttle);
                Assert.True(Math.Abs(t.Lambda / t.TargetLambda - 1) < 0.02, $"{rpm} rpm, throttle {throttle}: λ {t.Lambda:F3} vs {t.TargetLambda:F3}");
            }
    }

    [Fact]
    public void FuelingFollowsTheVeTableInjectorScalingAndDeadTime()
    {
        double baseline = LambdaAt(Stock(), 3000);

        var richVe = SimFactory.StockTuneOf(TestContent.M54);
        for (int r = 0; r < richVe.VolumetricEfficiency.Rows; r++)
            for (int c = 0; c < richVe.VolumetricEfficiency.Columns; c++) richVe.VolumetricEfficiency[r, c] *= 1.10;
        Near(baseline / 1.10, LambdaAt(Stock(tune: richVe), 3000), 0.01, "VE table +10 %");

        var smallInjectors = SimFactory.StockTuneOf(TestContent.M54);
        smallInjectors.InjectorFlowCcMin = 230 * 0.9; // the ECU believes the injectors flow less: longer pulses
        Assert.True(LambdaAt(Stock(tune: smallInjectors), 3000) < baseline * 0.93);

        // Too much dead time: short idle pulses run much richer than long full-load ones.
        var dead = SimFactory.StockTuneOf(TestContent.M54);
        dead.InjectorDeadTimeMs += 0.4;
        double idleError = LambdaAt(Stock(), 800, 0.0, 2.0) / LambdaAt(Stock(tune: dead), 800, 0.0, 2.0) - 1;
        double fullError = baseline / LambdaAt(Stock(tune: dead), 3000) - 1;
        Assert.True(idleError > 0.10, $"idle {idleError:P1} rich");
        Assert.True(idleError > 3 * fullError, $"idle {idleError:P1}, full load {fullError:P1}");
    }

    [Fact]
    public void FuelingFollowsFuelPressureAndFuelProperties()
    {
        double baseline = LambdaAt(Stock(), 3000);
        // A 3.0 bar regulator on injectors rated at 3.5 bar: √(3.5/3.0) less flow, leaner by the same.
        var lowPressure = SimFactory.Create(SimFactory.AssemblyOf(TestContent.M54, ("fuel_pump", "fuel_pump.hf_330")), Fuel);
        Near(baseline * Math.Sqrt(350.0 / 300.0), LambdaAt(lowPressure, 3000), 0.01, "3.0 bar regulator");
        // Race fuel is 4 % lighter and has a 14.4 stoichiometric ratio against the 0.750 kg/L and 14.7 the tune meters with.
        Near(baseline * 14.7 / 14.4 * 0.750 / 0.720, LambdaAt(Stock("race_110"), 3000), 0.02, "race fuel on a RON 98 calibration");
        // E85 on a gasoline calibration: stoichiometry (14.7 vs 9.8) and density put it ≈ 44 % lean, and its stronger
        // evaporative cooling (after the IAT sensor) packs in a few percent more air than the map expects.
        double e85 = LambdaAt(Stock("e85"), 3000), expected = baseline * 14.7 / 9.8 * 0.750 / 0.781;
        Assert.InRange(e85, expected, expected * 1.08);
    }

    [Fact]
    public void FuelingFollowsMapIatAndCoolantTemperature()
    {
        // The ECU corrects for manifold pressure and air temperature through its own sensors ...
        var hot = SimFactory.Create(TestContent.StockM54(), Fuel);
        var input = new EngineInputs
        {
            Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 3000, CoolantTemperatureOverride = 363.15,
            AmbientTemperature = 313.15,
        };
        EngineTelemetry hotAir = null!;
        for (int i = 0; i < 200; i++) hotAir = hot.Step(0.005, input);
        var normal = SimFactory.At(Stock(), 3000);
        Assert.True(hotAir.AirMassFlow < 0.96 * normal.AirMassFlow, "hot air is thinner");
        Assert.True(Math.Abs(hotAir.Lambda / normal.Lambda - 1) < 0.03, $"λ {hotAir.Lambda:F3} vs {normal.Lambda:F3}");
        var partLoad = SimFactory.At(Stock(), 3000, 0.15);
        Assert.True(partLoad.MapKpa < 70 && Math.Abs(partLoad.Lambda / partLoad.TargetLambda - 1) < 0.02);

        // ... but not for coolant temperature: the charge picks up heat after the IAT sensor, so a cold engine runs lean.
        var cold = SimFactory.Create(TestContent.StockM54(), Fuel);
        var coldInput = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 3000, CoolantTemperatureOverride = 313.15 };
        EngineTelemetry coldT = null!;
        for (int i = 0; i < 200; i++) coldT = cold.Step(0.005, coldInput);
        Assert.True(coldT.Lambda > normal.Lambda * 1.01, $"cold λ {coldT.Lambda:F3} vs warm {normal.Lambda:F3}");
    }

    // ---- Ignition and knock --------------------------------------------------------------------

    [Fact]
    public void TimingChangesTorqueAndKnockTheWayAnEngineDoes()
    {
        EngineTelemetry With(double offset, string fuel = Fuel, double rpm = 4000, bool knockControl = false)
        {
            var tune = SimFactory.StockTuneOf(TestContent.M54);
            tune.KnockControlEnabled = knockControl;
            tune.OffsetIgnition(offset, 90);
            return SimFactory.At(Stock(fuel, tune), rpm);
        }
        // Near best-torque timing at 4,000 rpm: retarding 8° costs torque and heats the exhaust.
        var stock = With(0);
        var retarded = With(-8);
        Assert.True(retarded.Torque < stock.Torque * 0.99);
        Assert.True(retarded.EgtC > stock.EgtC);
        Assert.Equal(0, stock.KnockIntensity);
        // At 1,500 rpm the factory map sits just under the knock limit: 6° more knocks.
        Assert.Equal(KnockLevel.None, With(0, rpm: 1500).KnockSensorLevel);
        Assert.True(With(6, rpm: 1500).KnockSensorLevel >= KnockLevel.Light);
        // Lower octane knocks where the rating fuel does not, and the knock sensor pulls timing.
        var ron91 = SimFactory.Create(TestContent.StockM54(), "gasoline_91");
        SimFactory.At(ron91, 2000, 1.0, 5.0);
        Assert.True(ron91.Ecu.KnockRetard > 1.0, $"retard {ron91.Ecu.KnockRetard:F1}°");
        var ron98 = Stock();
        SimFactory.At(ron98, 2000, 1.0, 5.0);
        Assert.True(ron98.Ecu.KnockRetard < 0.5);
    }

    // ---- Damage --------------------------------------------------------------------------------

    private static EngineTelemetry Hold(EngineSimulation sim, double rpm, double seconds, double throttle = 1.0)
    {
        var input = new EngineInputs { Throttle = throttle, SpeedMode = SpeedMode.Held, HeldRpm = rpm, CoolantTemperatureOverride = 363.15 };
        EngineTelemetry t = sim.Step(0.005, input);
        for (int i = 0; i < (int)(seconds / 0.005) && sim.Damage.Failures.Count == 0; i++) t = sim.Step(0.005, input);
        return t;
    }

    [Fact]
    public void StockEngineSurvivesSustainedFullPower()
    {
        var sim = Stock();
        Hold(sim, 6000, 300);
        Assert.Empty(sim.Damage.Failures);
    }

    [Fact]
    public void OverAdvancedTimingWithKnockControlOffDestroysThePistons()
    {
        var tune = SimFactory.StockTuneOf(TestContent.M54);
        tune.KnockControlEnabled = false;
        tune.OffsetIgnition(12, 90);
        var sim = Stock("gasoline_95", tune);
        Hold(sim, 2500, 300);
        var report = Assert.Single(sim.Damage.Failures);
        Assert.Contains(report.Mode, new[] { FailureMode.Detonation, FailureMode.PistonCrownOverheat, FailureMode.PistonPressureOverload });
        Assert.Contains(report.Recommendations, r => r.Contains("timing") || r.Contains("octane"));
        Assert.True(sim.Damage.Seized);
        Assert.Contains(PartInspector.Inspect(sim.Config.Part(PartCategory.Pistons)), f => f.Severity != FindingSeverity.Good);
    }

    [Fact]
    public void OverRevvingPastValveFloatBendsTheValves()
    {
        // A missed downshift (speed imposed by the drivetrain): the hydraulic-tappet valvetrain floats first.
        var sim = Stock();
        Assert.InRange(sim.ValveFloatRpm(), 6600, 7600);
        Hold(sim, sim.ValveFloatRpm() * 1.08, 30, throttle: 0.0);
        var report = Assert.Single(sim.Damage.Failures);
        Assert.Equal(FailureMode.ValvePistonContact, report.Mode);
    }

    [Fact]
    public void DamagePersistsThroughSaveAndLoad()
    {
        var g = Garage.NewGame(TestContent.Database, "isar_c30_six");
        Assert.Equal(TestContent.M54, g.Engine.Definition.Id);
        var (sim, report) = g.CreateSimulation();
        Assert.True(sim != null, string.Join("\n", report.Errors));
        var tune = g.Tune;
        tune.KnockControlEnabled = false;
        tune.OffsetIgnition(12, 90);
        g.SetFuel("gasoline_95");
        (sim, _) = g.CreateSimulation();
        Hold(sim!, 2500, 300);
        Assert.NotEmpty(sim!.Damage.Failures);
        var pistons = g.Engine.PartIn("pistons")!;
        Assert.True(pistons.IsFailed);

        var loaded = SaveSystem.Deserialize(SaveSystem.Serialize(g), TestContent.Database);
        var loadedPistons = loaded.Engine.PartIn("pistons")!;
        Assert.Equal(pistons.Damage.Failure!.Mode, loadedPistons.Damage.Failure!.Mode);
        Assert.NotEmpty(pistons.Damage.Fatigue);
        foreach (var (mode, value) in pistons.Damage.Fatigue) Assert.Equal(value, loadedPistons.Damage.FatigueOf(mode), 12);
        foreach (var (mode, exposure) in pistons.Damage.Exposure) Assert.Equal(exposure, loadedPistons.Damage.ExposureOf(mode));
        Assert.Equal(g.Engine.PartIn("rod_bearings")!.Wear, loaded.Engine.PartIn("rod_bearings")!.Wear, 12);
        Assert.True(DamageModel.IsSeized(loaded.Engine));
        Assert.Equal("isar_c30_six", loaded.ScenarioId);
        Assert.Equal(SaveSystem.Serialize(g), SaveSystem.Serialize(loaded));
    }

    [Fact]
    public void DynoRunsAreDeterministic()
    {
        DynoRun Run() => new DynoRunner(Stock(), new DynoSettings { StartRpm = 2000, EndRpm = 6500 }).RunToCompletion();
        var a = Run();
        var b = Run();
        Assert.Equal(a.Samples.Count, b.Samples.Count);
        for (int i = 0; i < a.Samples.Count; i++) Assert.Equal(a.Samples[i], b.Samples[i]);
    }
}
