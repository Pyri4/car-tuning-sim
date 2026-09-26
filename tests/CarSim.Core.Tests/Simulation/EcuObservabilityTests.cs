using CarSim.Core.Common;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

/// <summary>
/// The ECU is not an oracle: it fuels, times and boosts from its own sensors (rpm, MAP, IAT, knock sensor) and its
/// own calibration (VE table, displacement, injector flow and dead time, fuel stoich and density), never from the
/// simulation's true air mass, fuel or hardware. And the player's screens and logs only show what a dyno cell can
/// measure.
/// </summary>
public class EcuObservabilityTests
{
    /// <summary>Fuel per cylinder per cycle the ECU commands, computed from its inputs alone.</summary>
    private static double FuelFromEcuInputs(EcuTune tune, EngineTelemetry t, int cylinders)
    {
        double mapKpa = Units.PaToKpa(t.EcuMapReading);
        double air = tune.VolumetricEfficiencyAt(t.Rpm, mapKpa) * t.EcuMapReading * Units.CcToM3(tune.DisplacementCc) / cylinders
                     / (PhysicalConstants.AirGasConstant * t.ManifoldTemperature);
        return air / (tune.LambdaAt(t.Rpm, mapKpa) * tune.FuelStoichAfr);
    }

    public static IEnumerable<object[]> Configurations() => new[]
    {
        new object[] { "stock", 3000.0, 1.0 },
        new object[] { "stock", 6000.0, 0.3 },
        new object[] { "race cams", 2500.0, 1.0 },
        new object[] { "race cams", 7200.0, 1.0 },
        new object[] { "ported head", 7000.0, 1.0 },
        new object[] { "stroker", 3000.0, 1.0 },
        new object[] { "cold coolant", 3000.0, 1.0 },
        new object[] { "hot coolant", 3000.0, 1.0 },
        new object[] { "turbo", 5500.0, 1.0 },
    };

    private static (EngineSimulation Sim, EngineTelemetry T) Run(string config, double rpm, double throttle)
    {
        EngineSimulation sim = config switch
        {
            "race cams" => SimFactory.Create(("valve_springs", "k20.valve_springs.performance"), ("camshafts", "k20.cams.race")),
            "ported head" => SimFactory.Create(("cylinder_head", "k20.head.ported")),
            "stroker" => SimFactory.Create(("crankshaft", "k20.crankshaft.stroker90"), ("pistons", "k20.pistons.stroker_lc")),
            "turbo" => TurboTests.TurboSim(TurboTests.TurboBuild()),
            _ => SimFactory.Create(),
        };
        double coolant = config switch { "cold coolant" => 320.0, "hot coolant" => 385.0, _ => 363.15 };
        return (sim, SteadyStateSweep.Settle(sim, rpm, throttle, config == "turbo" ? 4.0 : 1.5, coolant));
    }

    /// <summary>
    /// The delivered fuel is exactly what the ECU's own formula gives from the sensor readings — across hardware whose
    /// true breathing differs by tens of percent. An ECU reading the true air mass anywhere would track the air instead.
    /// </summary>
    [Theory]
    [MemberData(nameof(Configurations))]
    public void DeliveredFuelDependsOnlyOnWhatTheEcuCanSee(string config, double rpm, double throttle)
    {
        var (sim, t) = Run(config, rpm, throttle);
        Assert.True(t.Firing);
        Assert.Equal(FuelLimit.None, t.FuelLimit);
        int cylinders = sim.Config.Geometry.Cylinders;
        double delivered = t.FuelMassFlow / (t.Rpm / 120.0 * cylinders);
        // The installed injectors match the tune's flow and dead time and run at their rated pressure, so the only
        // hardware factor left is the real fuel's density against the one the ECU meters with.
        var tune = sim.Ecu.Tune;
        Assert.Equal(tune.InjectorFlowCcMin, sim.Config.Injectors.FlowCcMin);
        Assert.Equal(tune.InjectorDeadTimeMs, sim.Config.Injectors.DeadTimeMs, 9);
        double expected = FuelFromEcuInputs(tune, t, cylinders) * sim.Config.Fuel.DensityKgL / tune.FuelDensityKgL;
        Assert.Equal(1.0, delivered / expected, 6);
    }

    [Fact]
    public void TheConfigurationsReallyBreatheDifferentlyFromWhatTheEcuBelieves()
    {
        // Guards the test above: if every configuration breathed like the calibration, it would prove nothing.
        var errors = Configurations().Select(c => Run((string)c[0], (double)c[1], (double)c[2]).T)
            .Select(t => t.Lambda / t.TargetLambda - 1.0).ToList();
        Assert.True(errors.Max() - errors.Min() > 0.15, $"λ errors {string.Join(", ", errors.Select(e => e.ToString("P1")))}");
    }

    [Fact]
    public void CoolantTemperatureMovesTheMixtureTheEcuCannotSee()
    {
        // The IAT sensor sits in the manifold; the charge picks heat up from the ports and chamber on the way in. A
        // cold engine's charge is denser than the ECU thinks (lean), a hot one's thinner (rich): no warm-up or
        // coolant correction is modelled, so the player sees it on the wideband.
        var cold = Run("cold coolant", 3000, 1.0).T;
        var hot = Run("hot coolant", 3000, 1.0).T;
        Assert.Equal(cold.ManifoldTemperature, hot.ManifoldTemperature, 1);
        Assert.True(cold.Lambda > hot.Lambda * 1.01, $"cold λ {cold.Lambda:F3}, hot λ {hot.Lambda:F3}");
        Assert.True(cold.AirPerCycle > hot.AirPerCycle);
    }

    [Fact]
    public void OpenLoopErrorsPersistNoHiddenClosedLoop()
    {
        // A VE cell 10 % high fuels 10 % rich, and stays there: nothing trims the mixture towards target behind the
        // player's back.
        var tune = SimFactory.StockTune();
        for (int r = 0; r < tune.VolumetricEfficiency.Rows; r++)
            for (int c = 0; c < tune.VolumetricEfficiency.Columns; c++) tune.VolumetricEfficiency[r, c] *= 1.10;
        var sim = SimFactory.Create(SimFactory.Assembly(), tune: tune);
        var early = SimFactory.At(sim, 4000, 0.5, 1.0);
        var late = SimFactory.At(sim, 4000, 0.5, 30.0);
        Assert.InRange(early.TargetLambda / early.Lambda, 1.08, 1.12);
        Assert.Equal(early.Lambda, late.Lambda, 3);
    }

    [Fact]
    public void AHigherFuelPressureRunsRichUntilTheInjectorScalingIsChanged()
    {
        // Injector flow scales with √ΔP; the ECU's scaling is for the rated 3 bar. A 4 bar regulator: rich by √(4/3)
        // on the long part of each pulse.
        var stockPump = TestContent.Database.GetPart("fuel_pump.hf_330");
        var p = stockPump.GetSpec<FuelPumpSpec>();
        var fourBar = new PartDefinition
        {
            Id = "test.fuel_pump.4bar", Name = "4 bar regulator (test)", Category = stockPump.Category,
            Provides = stockPump.Provides, Requires = stockPump.Requires,
            Spec = new FuelPumpSpec { FreeFlowLph = p.FreeFlowLph, MaxPressureKpa = p.MaxPressureKpa, RegulatedPressureKpa = 400 },
        };
        var a = SimFactory.Assembly();
        a.Remove("fuel_pump", out _);
        Assert.True(a.Install("fuel_pump", new PartInstanceFactory(8_000_000).Create(fourBar)).Ok);
        var t = SimFactory.At(SimFactory.Create(a), 6000, 1.0);
        Assert.Equal(FuelLimit.None, t.FuelLimit);
        double richness = t.TargetLambda / t.Lambda;
        Assert.InRange(richness, 1.10, Math.Sqrt(400.0 / 300.0) + 0.005);
    }

    [Fact]
    public void BoostControlClosesTheLoopOnTheMapSensorItHas()
    {
        // A standalone ECU with a 2.5-bar MAP sensor chasing a 280 kPa target: the reading pegs at 250 kPa, the ECU never
        // sees the target reached, holds the wastegate shut and over-boosts. The validator warns.
        var standalone = TestContent.Database.GetPart("ecu.standalone");
        var e = standalone.GetSpec<EcuSpec>();
        var shortSensor = new PartDefinition
        {
            Id = "test.ecu.2p5bar", Name = "Standalone ECU, 2.5-bar sensor (test)", Category = standalone.Category,
            Provides = standalone.Provides, Requires = standalone.Requires,
            Spec = new EcuSpec { MapSensorMaxKpa = 250, BoostControl = true, KnockControl = e.KnockControl, MaxRevLimitRpm = e.MaxRevLimitRpm },
        };
        var a = TurboTests.TurboBuild();
        a.Remove("ecu", out _);
        Assert.True(a.Install("ecu", new PartInstanceFactory(8_100_000).Create(shortSensor)).Ok);
        var tune = SimFactory.ExtendLoadAxis(TurboTests.TuneWithBoost(280), 300, 350);
        Assert.True(AssemblyValidator.Validate(a, new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa)).Has("boost_target_above_map_sensor"));
        var sim = SimFactory.Create(a, "race_110", tune);
        sim.DamageEnabled = false;
        var blind = SimFactory.At(sim, 5500, 1.0, 4.0);
        Assert.True(blind.MapSensorSaturated);
        Assert.Equal(0.0, blind.WastegateOpening, 3);
        Assert.True(blind.MapKpa > 280, $"MAP {blind.MapKpa:F0} kPa");

        // The same target with the 4-bar sensor: held.
        var seeing = TurboTests.TurboSim(TurboTests.TurboBuild(), fuel: "race_110");
        seeing.Ecu.Tune = SimFactory.ExtendLoadAxis(TurboTests.TuneWithBoost(240), 300, 350);
        seeing.DamageEnabled = false;
        var held = SimFactory.At(seeing, 5500, 1.0, 4.0);
        Assert.InRange(held.MapKpa, 234, 244);
    }

    [Fact]
    public void BelowTheEnableThrottleTheWastegateSpringSetsBoost()
    {
        // Closed-loop boost control only runs above its pedal threshold; part throttle is left to the spring.
        var sim = TurboTests.TurboSim(TurboTests.TurboBuild());
        var t = SimFactory.At(sim, 5500, EngineSimulation.BoostControlMinThrottle - 0.05, 4.0);
        Assert.Equal(0.0, sim.State.Turbos[0].BoostControlIntegral);
        Assert.True(t.CompressorOutletPressure - 101_325 < sim.Config.Turbos[0].Spec.WastegateSpring + EngineSimulation.WastegateActuatorSpan);
    }

    [Fact]
    public void TheKnockSensorReportsKnockNotTheKnockLimit()
    {
        // Onset is exact: the sensor reads something exactly when the end gas autoignites.
        Assert.Equal(KnockLevel.None, KnockSensor.Level(0.0));
        Assert.Equal(KnockLevel.Trace, KnockSensor.Level(1e-6));
        // But a reading is a band several tenths of a degree wide, so it cannot be turned back into the limit.
        Assert.Equal(KnockSensor.Level(0.35), KnockSensor.Level(0.95));
        Assert.Equal(KnockSensor.Level(1.1), KnockSensor.Level(2.4));
        // Monotonic.
        var levels = Enumerable.Range(0, 60).Select(i => KnockSensor.Level(i * 0.1)).ToList();
        for (int i = 1; i < levels.Count; i++) Assert.True(levels[i] >= levels[i - 1]);
    }

    [Fact]
    public void TheDynoLogHoldsOnlyMeasurableChannels()
    {
        var headers = DynoRun.CsvChannels.Select(c => c.Header).ToList();
        foreach (var hidden in new[] { "mbt", "knock_limit", "knock_deg", "ve_dynamic", "residual", "charge" })
            Assert.DoesNotContain(headers, h => h.Contains(hidden));
        // The IAT channel is the manifold air temperature the ECU's sensor reads, not the trapped charge temperature.
        var t = SimFactory.At(TurboTests.TurboSim(TurboTests.TurboBuild()), 5500, 1.0, 3.0);
        var iat = DynoRun.CsvChannels.Single(c => c.Header == "iat_c").Value(t);
        Assert.Equal(Units.KToC(t.ManifoldTemperature), iat, 9);
        Assert.NotEqual(Units.KToC(t.ChargeTemperature), iat, 1);
    }
}
