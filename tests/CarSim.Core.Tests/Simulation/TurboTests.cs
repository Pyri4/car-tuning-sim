using CarSim.Core.Common;
using CarSim.Core.Dyno;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

public class TurboTests
{
    private static TurbochargerSpec Spec(string id) => TestContent.Database.GetPart(id).GetSpec<TurbochargerSpec>();

    /// <summary>Forged, fuelled, standalone-ECU turbo build around the given turbo.</summary>
    public static EngineAssembly TurboBuild(string turbo = "turbo.t28_ball", string? intercooler = "intercooler.fmic_street",
        string ecu = "ecu.standalone", string cams = "k20.cams.oem")
    {
        string manifold = Spec(turbo).CompressorWheelDiameterMm > 70 ? "k20.exhaust_manifold.turbo_t3_tubular" : "k20.exhaust_manifold.turbo_log";
        var swaps = new List<(string, string)>
        {
            ("pistons", "k20.pistons.forged_lc"), ("connecting_rods", "k20.rods.forged_h"), ("head_gasket", "k20.head_gasket.race"),
            ("injectors", "injectors.550cc"), ("fuel_pump", "fuel_pump.hf_330"), ("ecu", ecu), ("exhaust", "exhaust.sport_63mm"),
            ("exhaust_manifold", manifold),
        };
        if (cams != "k20.cams.oem") { swaps.Add(("valve_springs", "k20.valve_springs.performance")); swaps.Add(("camshafts", cams)); }
        var a = SimFactory.Assembly(swaps.ToArray());
        var f = new PartInstanceFactory(5_000_000);
        Assert.True(a.Install("turbocharger", f.Create(TestContent.Database.GetPart(turbo))).Ok);
        if (intercooler != null) Assert.True(a.Install("intercooler", f.Create(TestContent.Database.GetPart(intercooler))).Ok);
        return a;
    }

    public static EngineSimulation TurboSim(EngineAssembly a, string tune = "k20.turbo_base", string fuel = "gasoline_98") =>
        SimFactory.Create(a, fuel, Ecu.EcuTune.FromDocument(TestContent.Database.GetTune(tune)));

    [Fact]
    public void CompressorPressureRatioRisesWithShaftSpeed()
    {
        // At 40 krpm, 0.15 kg/s is past this wheel's choke flow: it is a restriction (PR < 1), and
        // spinning it faster turns it back into a pump.
        var s = Spec("turbo.t28_ball");
        double last = 0.0;
        foreach (double krpm in new[] { 40, 80, 120, 160, 175 })
        {
            var p = TurbochargerModel.Compressor(s, Units.RpmToRadPerSec(krpm * 1000), 0.15, 101_325, 298);
            Assert.True(p.PressureRatio > last, $"{krpm} krpm: PR {p.PressureRatio:F3} after {last:F3}");
            last = p.PressureRatio;
        }
        Assert.InRange(last, 3.0, 4.0);
    }

    [Fact]
    public void SpeedLineDroopsAndChokes()
    {
        var s = Spec("turbo.t28_ball");
        double w = Units.RpmToRadPerSec(150_000);
        // Speed lines are flat across the efficiency island, droop towards choke and collapse past it.
        var island = TurbochargerModel.Compressor(s, w, 0.16, 101_325, 298);
        var mid = TurbochargerModel.Compressor(s, w, 0.25, 101_325, 298);
        var choke = TurbochargerModel.Compressor(s, w, 0.30, 101_325, 298);
        Assert.True(mid.PressureRatio < island.PressureRatio);
        Assert.True(choke.PressureRatio < mid.PressureRatio);
        Assert.True(choke.Efficiency < mid.Efficiency);
        Assert.True(choke.PressureRatio < 1.5, "past choke the compressor cannot hold pressure");
    }

    [Fact]
    public void CompressorEnergyIsConsistent()
    {
        var s = Spec("turbo.t28_ball");
        double omega = Units.RpmToRadPerSec(120_000);
        var p = TurbochargerModel.Compressor(s, omega, 0.15, 101_325, 298);
        Assert.False(p.Surge);
        // Shaft power = through-flow work + disk friction (which heats the housing, not the charge).
        double density = 101_325 / (PhysicalConstants.AirGasConstant * 298);
        double disk = 0.5 * TurbochargerModel.DiskFrictionCoefficient * density * Math.Pow(omega, 3) * Math.Pow(s.CompressorTipRadius, 5);
        Assert.Equal(0.15 * p.SpecificWork + disk, p.Power, 6);
        Assert.Equal(298 + p.SpecificWork / PhysicalConstants.AirCp, p.OutletTemperature, 6);
        // Isentropic outlet temperature is below the actual one by the efficiency.
        double isentropicRise = 298 * (Math.Pow(p.PressureRatio, 0.4 / 1.4) - 1);
        Assert.Equal(p.Efficiency, isentropicRise / (p.OutletTemperature - 298), 3);
    }

    [Fact]
    public void LowFlowAtHighPressureRatioSurges()
    {
        var s = Spec("turbo.t28_ball");
        Assert.True(TurbochargerModel.Compressor(s, Units.RpmToRadPerSec(160_000), 0.02, 101_325, 298).Surge);
        Assert.False(TurbochargerModel.Compressor(s, Units.RpmToRadPerSec(160_000), 0.18, 101_325, 298).Surge);
    }

    [Fact]
    public void TurbinePowerGrowsWithExpansionRatio()
    {
        var s = Spec("turbo.t28_ball");
        double w = Units.RpmToRadPerSec(100_000);
        Assert.Equal(0.0, TurbochargerModel.Turbine(s, w, 0.2, 110_000, 110_000, 1100).Power);
        double a = TurbochargerModel.Turbine(s, w, 0.2, 140_000, 110_000, 1100).Power;
        double b = TurbochargerModel.Turbine(s, w, 0.2, 180_000, 110_000, 1100).Power;
        Assert.True(b > a && a > 0);
    }

    [Fact]
    public void TurboBuildMakesFarMoreTorqueOnceSpooled()
    {
        var na = SimFactory.At(SimFactory.Create(), 5500);
        var turbo = SimFactory.At(TurboSim(TurboBuild()), 5500, 1.0, 2.0);
        Assert.True(turbo.BoostKpa > 60);
        Assert.True(turbo.Torque > 1.4 * na.Torque, $"NA {na.Torque:F0} N·m, turbo {turbo.Torque:F0} N·m");
        Assert.True(turbo.TurboRpm > 60_000);
    }

    [Fact]
    public void EcuBoostControlHoldsTheTarget()
    {
        var t = SimFactory.At(TurboSim(TurboBuild()), 6000, 1.0, 3.0);
        Assert.InRange(t.MapKpa, 165, 182);
        Assert.True(t.WastegateOpening > 0.05);
    }

    [Fact]
    public void WithoutBoostControlTheWastegateSpringSetsBoost()
    {
        var tune = Ecu.EcuTune.FromDocument(TestContent.Database.GetTune("k20.turbo_base"));
        var sim = SimFactory.Create(TurboBuild(ecu: "ecu.k20_oem"), "gasoline_98", tune);
        var t = SimFactory.At(sim, 6000, 1.0, 3.0);
        double spring = Units.PaToKpa(sim.Config.Turbo!.WastegateSpring);
        Assert.InRange(t.BoostKpa, spring - 5, spring + 25);
    }

    [Fact]
    public void UndersizedWastegateCreepsBoost()
    {
        var baseSpec = Spec("turbo.t28_ball");
        var tiny = new PartDefinition
        {
            Id = "test.turbo.tiny_wastegate", Name = "Tiny wastegate turbo", Category = PartCategory.Turbocharger,
            Requires = new[] { "turbo_flange.t25" }, Provides = new[] { "turbo.fitted", "boost.source" },
            Spec = new TurbochargerSpec
            {
                CompressorWheelDiameterMm = baseSpec.CompressorWheelDiameterMm, CompressorChokeFlowKgS = baseSpec.CompressorChokeFlowKgS,
                CompressorPeakEfficiency = baseSpec.CompressorPeakEfficiency, CompressorPeakEfficiencyFlowKgS = baseSpec.CompressorPeakEfficiencyFlowKgS,
                CompressorPeakEfficiencyPressureRatio = baseSpec.CompressorPeakEfficiencyPressureRatio,
                CompressorSurgeFlowAtPr2KgS = baseSpec.CompressorSurgeFlowAtPr2KgS, TurbineFlowAreaCm2 = baseSpec.TurbineFlowAreaCm2,
                TurbineWheelDiameterMm = baseSpec.TurbineWheelDiameterMm, TurbinePeakEfficiency = baseSpec.TurbinePeakEfficiency,
                RotorInertiaKgCm2 = baseSpec.RotorInertiaKgCm2, MaxShaftRpm = baseSpec.MaxShaftRpm,
                WastegateSpringKpa = baseSpec.WastegateSpringKpa, WastegateFlowAreaCm2 = 0.6, BallBearing = true,
            },
        };
        var normal = TurboBuild();
        var creep = TurboBuild();
        creep.Remove("intercooler", out var ic);
        creep.Remove("turbocharger", out _);
        creep.Install("turbocharger", new PartInstanceFactory(6_000_000).Create(tiny));
        creep.Install("intercooler", ic!);
        var tn = SimFactory.At(TurboSim(normal), 7000, 1.0, 3.0);
        var tc = SimFactory.At(TurboSim(creep), 7000, 1.0, 3.0);
        Assert.True(tc.WastegateOpening > 0.9, "the wastegate is wide open but cannot bypass enough");
        Assert.True(tc.BoostKpa > tn.BoostKpa + 20, $"normal {tn.BoostKpa:F0} kPa, creeping {tc.BoostKpa:F0} kPa");
    }

    [Fact]
    public void BiggerTurbosSpoolLater()
    {
        double SpoolRpm(string turbo)
        {
            var sim = TurboSim(TurboBuild(turbo));
            var samples = TransientSweep.Run(sim, 2000, 7300, 600);
            return TransientSweep.FirstAtBoost(samples, 50_000)?.Rpm ?? double.PositiveInfinity;
        }
        double small = SpoolRpm("turbo.t25_small"), mid = SpoolRpm("turbo.t28_ball"), big = SpoolRpm("turbo.t35_big");
        Assert.True(small < mid, $"small {small:F0}, mid {mid:F0}");
        Assert.True(mid < big, $"mid {mid:F0}, big {big:F0}");
        Assert.True(small < 4500);
    }

    [Fact]
    public void TransientPullSpoolsLaterThanSteadyState()
    {
        var steady = SimFactory.At(TurboSim(TurboBuild()), 4500, 1.0, 3.0);
        var samples = TransientSweep.Run(TurboSim(TurboBuild()), 2000, 4600, 800);
        var at4500 = samples.Last(s => s.Rpm <= 4500);
        Assert.True(at4500.BoostPressure < steady.BoostPressure, "lag: the turbo has not caught up during a fast pull");
    }

    [Fact]
    public void SmallTurboChokesAtHighRpm()
    {
        var sim = TurboSim(TurboBuild("turbo.t25_small"));
        sim.DamageEnabled = false;
        var t = SimFactory.At(sim, 7000, 1.0, 3.0);
        Assert.True(t.CompressorChokeRatio > 0.85);
        Assert.True(t.CompressorEfficiency < 0.65);
        // Out of breath: the small turbine also chokes the exhaust.
        Assert.True(t.TurbineInletPressure > 1.2 * t.ManifoldPressure);
    }

    /// <summary>Base turbo tune with the boost target raised to <paramref name="kpa"/> wherever it was at full boost.</summary>
    public static Ecu.EcuTune TuneWithBoost(double kpa)
    {
        var tune = Ecu.EcuTune.FromDocument(TestContent.Database.GetTune("k20.turbo_base"));
        for (int c = 0; c < tune.BoostTarget!.Columns; c++) if (tune.BoostTarget[0, c] >= 170) tune.BoostTarget[0, c] = kpa;
        return tune;
    }

    [Fact]
    public void SmallTurboOverspeedsChasingABoostTargetItCannotHold()
    {
        // Near choke a small compressor can only pass more air by spinning faster; the ECU keeps the
        // wastegate shut, so the shaft runs past its rating — but it settles where the compressor's work
        // balances the turbine (the work does not vanish at choke), it does not run away.
        var sim = SimFactory.Create(TurboBuild("turbo.t25_small"), "gasoline_98", TuneWithBoost(200));
        sim.DamageEnabled = false;
        var t = SimFactory.At(sim, 7000, 1.0, 4.0);
        Assert.True(t.TurboOverspeed);
        double tipSpeed = sim.State.TurboOmega * sim.Config.Turbo!.CompressorTipRadius;
        double ratedTip = sim.Config.Turbo.MaxShaftSpeed * sim.Config.Turbo.CompressorTipRadius;
        Assert.InRange(tipSpeed / ratedTip, 1.0, 1.3);
    }

    [Theory]
    [InlineData("turbo.t28_ball", 240, 7000)]
    [InlineData("turbo.t28_ball", 280, 6000)]
    [InlineData("turbo.t25_small", 200, 6000)]
    public void ClosedLoopBoostSettlesWithoutStepToStepRipple(string turbo, double targetKpa, double rpm)
    {
        // Regression: the old compressor iteration flipped between two solutions every few steps
        // (torque 305 ↔ 375 N·m) once the boost target was above the shipped 175 kPa.
        // Race fuel keeps knock control out of it: this is about the compressor, not knock-control dither.
        var sim = SimFactory.Create(TurboBuild(turbo), "race_110", TuneWithBoost(targetKpa));
        sim.DamageEnabled = false;
        SimFactory.At(sim, rpm, 1.0, 4.0);
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = rpm, CoolantTemperatureOverride = 363.15 };
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < 250; i++)
        {
            var t = sim.Step(0.002, input);
            Assert.True(double.IsFinite(t.Torque));
            min = Math.Min(min, t.Torque);
            max = Math.Max(max, t.Torque);
        }
        Assert.True((max - min) / max < 0.01, $"torque {min:F1}..{max:F1} N·m");
    }

    [Fact]
    public void IntercoolerCoolsTheChargeAndRaisesTheKnockLimit()
    {
        var with = SimFactory.At(TurboSim(TurboBuild()), 6000, 1.0, 3.0);
        var without = SimFactory.At(TurboSim(TurboBuild(intercooler: null)), 6000, 1.0, 3.0);
        Assert.True(without.ChargeTemperature > with.ChargeTemperature + 30);
        Assert.True(without.KnockLimitAdvance < with.KnockLimitAdvance);
        Assert.True(with.AirPerCycle > without.AirPerCycle);
    }

    [Fact]
    public void StockEcuCannotSeeBoostAndRunsLean()
    {
        var a = SimFactory.Assembly(("exhaust_manifold", "k20.exhaust_manifold.turbo_log"), ("injectors", "injectors.550cc"));
        a.Install("turbocharger", new PartInstanceFactory(7_000_000).Create(TestContent.Database.GetPart("turbo.t28_ball")));
        var tune = SimFactory.StockTune();
        tune.InjectorFlowCcMin = 550;
        var t = SimFactory.At(SimFactory.Create(a, tune: tune), 5500, 1.0, 3.0);
        Assert.True(t.MapSensorSaturated);
        Assert.Equal(105_000, t.EcuMapReading, 1);
        Assert.True(t.Lambda > t.TargetLambda * 1.25, $"λ {t.Lambda:F2} vs target {t.TargetLambda:F2}");
        Assert.True(t.KnockIntensity > 0, "and the NA timing is far too advanced for boost");
    }

    [Fact]
    public void LongOverlapCamsSufferWhenDrivePressureExceedsBoost()
    {
        // A small turbine at high rpm drives exhaust manifold pressure well above boost; overlap then
        // pushes exhaust back into the intake.
        var turboCams = SimFactory.At(TurboSim(TurboBuild("turbo.t25_small", cams: "k20.cams.turbo")), 7000, 1.0, 3.0);
        var raceCams = SimFactory.At(TurboSim(TurboBuild("turbo.t25_small", cams: "k20.cams.race")), 7000, 1.0, 3.0);
        Assert.True(raceCams.TurbineInletPressure > 1.15 * raceCams.ManifoldPressure);
        Assert.True(raceCams.ResidualFactor < turboCams.ResidualFactor - 0.02,
            $"race {raceCams.ResidualFactor:F3}, turbo cams {turboCams.ResidualFactor:F3}");
    }

    [Fact]
    public void TurboSpinsDownWhenTheEngineStops()
    {
        var sim = TurboSim(TurboBuild());
        SimFactory.At(sim, 6000, 1.0, 2.0);
        double spinning = sim.State.TurboOmega;
        var input = new EngineInputs { Ignition = false };
        for (int i = 0; i < 4000; i++) sim.Step(0.005, input);
        Assert.True(sim.State.TurboOmega < 0.5 * spinning);
    }

    [Fact]
    public void TurboBuildValidatesClean()
    {
        var report = AssemblyValidator.Validate(TurboBuild(), new ValidationContext(7400));
        Assert.True(report.CanRun, string.Join("\n", report.Issues));
    }

    [Fact]
    public void TurboOnStockEcuWarnsAboutMapSensorRange()
    {
        var report = AssemblyValidator.Validate(TurboBuild(ecu: "ecu.k20_oem"));
        Assert.True(report.CanRun);
        Assert.True(report.Has("map_sensor_range"));
        Assert.True(report.Has("boost_by_spring"));
        Assert.False(AssemblyValidator.Validate(TurboBuild()).Has("map_sensor_range"));
    }

    [Fact]
    public void BoostTargetBelowSpringIsFlagged()
    {
        var report = AssemblyValidator.Validate(TurboBuild(), new ValidationContext(7400, MaxBoostTargetKpa: 120));
        Assert.True(report.Has("boost_target_below_spring"));
    }

    [Fact]
    public void TurboManifoldWithoutTurboIsRejected()
    {
        var a = SimFactory.Assembly(("exhaust_manifold", "k20.exhaust_manifold.turbo_log"));
        var report = AssemblyValidator.Validate(a);
        Assert.Contains(report.Errors, i => i.Code == "missing_interface" && i.Message.Contains("turbo.fitted"));
    }

    [Fact]
    public void IntercoolerNeedsATurbo()
    {
        var a = TurboBuild();
        a.Remove("intercooler", out var ic);
        a.Remove("turbocharger", out _);
        Assert.False(a.Install("intercooler", ic!).Ok, "slot order: intercooler goes on after the turbo");
    }
}
