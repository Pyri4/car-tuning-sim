using CarSim.Core.Damage;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;
using CarSim.Core.Tests.Simulation;

namespace CarSim.Core.Tests.Damage;

public class DamageTests
{
    /// <summary>Hold the engine at a speed until a failure happens or time runs out.</summary>
    private static EngineTelemetry Hold(EngineSimulation sim, double rpm, double seconds, double throttle = 1.0, double sumpG = 0.0,
        double? coolantK = 363.15, double airSpeed = 10.0)
    {
        var input = new EngineInputs
        {
            Throttle = throttle, SpeedMode = SpeedMode.Held, HeldRpm = rpm, CoolantTemperatureOverride = coolantK,
            SumpAccelerationG = sumpG, CoolingAirSpeed = airSpeed,
        };
        EngineTelemetry t = sim.Step(0.005, input);
        int steps = (int)(seconds / 0.005);
        for (int i = 0; i < steps && sim.Damage.Failures.Count == 0; i++) t = sim.Step(0.005, input);
        return t;
    }

    [Fact]
    public void StockEngineSurvivesSustainedFullPowerWithinItsLimits()
    {
        var sim = SimFactory.Create();
        var t = Hold(sim, 7000, 120);
        Assert.Empty(sim.Damage.Failures);
        Assert.False(t.Seized);
        Assert.All(sim.Config.Assembly.AllParts, p => Assert.True(p.Damage.MaxFatigue < 0.05, $"{p.Definition.Id} fatigue {p.Damage.MaxFatigue:F3}"));
    }

    [Fact]
    public void OverRevBendsTheValvesAndSeizesTheEngine()
    {
        var sim = SimFactory.Create();
        Hold(sim, 7000, 1);
        var t = Hold(sim, 9300, 20, throttle: 0.0);
        var report = Assert.Single(sim.Damage.Failures);
        Assert.Equal(FailureMode.ValvePistonContact, report.Mode);
        Assert.Equal(FailureSeverity.Catastrophic, report.Severity);
        Assert.True(sim.Damage.Seized);
        Assert.True(sim.Config.Part("cylinder_head").IsFailed);
        Assert.Contains(report.ContributingFactors, f => f.Contains("above the rev limiter"));
        Assert.Contains(report.Recommendations, r => r.Contains("stiffer valve springs"));
        Assert.NotEmpty(report.CollateralDamage);
        // Once seized, nothing turns.
        var after = sim.Step(0.005, new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 3000 });
        Assert.True(after.Seized);
        Assert.False(after.Firing);
    }

    [Fact]
    public void ValveFloatReportQuotesTheWornSpringForce()
    {
        var sim = SimFactory.Create();
        sim.Config.Part("valve_springs").Wear = 0.4;
        Hold(sim, 7000, 1);
        Hold(sim, 9300, 20, throttle: 0.0);
        var report = Assert.Single(sim.Damage.Failures);
        Assert.Equal(FailureMode.ValvePistonContact, report.Mode);
        var springs = (CarSim.Core.Parts.Specs.ValveSpringSpec)sim.Config.Part("valve_springs").Definition.Spec;
        double worn = springs.OpenForceN * (1 - ValvetrainModel.SpringForceLossAtFullWear * 0.4);
        var measured = Assert.Single(report.Measurements, m => m.Label == "Valve spring force at full lift");
        Assert.StartsWith($"{worn:N0} N", measured.Value.Replace(",", ""));
        Assert.Contains(report.ContributingFactors, f => f.Contains("springs were worn (40 %)"));
    }

    [Fact]
    public void StifferSpringsMoveTheWeakLinkToTheFlywheel()
    {
        var sim = SimFactory.Create(("valve_springs", "k20.valve_springs.performance"));
        Hold(sim, 9800, 30, throttle: 0.0);
        Assert.Equal(FailureMode.FlywheelBurst, Assert.Single(sim.Damage.Failures).Mode);
    }

    [Fact]
    public void WithTheValvetrainCrankAndFlywheelUpgradedTheRodsAreTheWeakLink()
    {
        var a = SimFactory.Assembly(("valve_springs", "k20.valve_springs.performance"), ("flywheel", "k20.flywheel.light"),
            ("crankshaft", "k20.crankshaft.forged"));
        var tune = SimFactory.StockTune();
        var sim = SimFactory.Create(a, tune: tune);
        // At 9800 rpm the rods see 111 % of their rating: about half a minute of life (per-cycle fatigue).
        Hold(sim, 9800, 120, throttle: 0.0);
        var report = Assert.Single(sim.Damage.Failures);
        Assert.Equal(FailureMode.RodTensileOverload, report.Mode);
        Assert.InRange(report.Time, 10, 60);
        Assert.Contains(report.Recommendations, r => r.Contains("Keep engine speed below"));
        Assert.Contains(report.CollateralDamage, c => c.Contains("block"));
        Assert.True(sim.Config.Part("block").IsFailed, "a thrown rod takes the block with it");
    }

    [Fact]
    public void ForgedRodsSurviveWhatKillsStockRods()
    {
        double safeStock = FailureDiagnostics.SafeRpmForRods(SimFactory.Create().Config);
        double safeForged = FailureDiagnostics.SafeRpmForRods(SimFactory.Create(("connecting_rods", "k20.rods.forged_h")).Config);
        Assert.True(safeForged > safeStock + 1000);
    }

    [Fact]
    public void OverAdvancedTimingDestroysPistonsWithDetonation()
    {
        var tune = SimFactory.StockTune();
        tune.KnockControlEnabled = false;
        tune.OffsetIgnition(12, 90);
        var sim = SimFactory.Create(SimFactory.Assembly(), fuel: "gasoline_91", tune: tune);
        var t = Hold(sim, 2500, 300);
        var report = Assert.Single(sim.Damage.Failures);
        Assert.Contains(report.Mode, new[] { FailureMode.Detonation, FailureMode.PistonCrownOverheat, FailureMode.PistonPressureOverload });
        Assert.Contains(report.Recommendations, r => r.Contains("timing") || r.Contains("octane") || r.Contains("richer"));
    }

    [Fact]
    public void KnockControlProtectsAgainstMildKnock()
    {
        var tune = SimFactory.StockTune();
        tune.OffsetIgnition(5, 90);
        var protectedSim = SimFactory.Create(SimFactory.Assembly(), fuel: "gasoline_91", tune: tune);
        Hold(protectedSim, 2500, 120);
        Assert.Empty(protectedSim.Damage.Failures);
        Assert.True(protectedSim.Ecu.KnockRetard > 0);
    }

    [Fact]
    public void DetonationShowsOnInspectionBeforeFailure()
    {
        var tune = SimFactory.StockTune();
        tune.KnockControlEnabled = false;
        tune.OffsetIgnition(10, 90);
        var sim = SimFactory.Create(SimFactory.Assembly(), fuel: "gasoline_91", tune: tune);
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 2500, CoolantTemperatureOverride = 363.15 };
        var pistons = sim.Config.Part("pistons");
        while (pistons.Damage.FatigueOf(FailureMode.Detonation) < 0.3 && sim.Damage.Failures.Count == 0) sim.Step(0.005, input);
        Assert.Empty(sim.Damage.Failures);
        var findings = PartInspector.Inspect(pistons);
        Assert.Contains(findings, f => f.Text.Contains("detonation") && f.Severity == FindingSeverity.Minor);
        Assert.True(pistons.Condition < 0.75);
    }

    [Fact]
    public void TurboOnStockEcuRunsLeanAndBreaksThePistons()
    {
        var a = SimFactory.Assembly(("exhaust_manifold", "k20.exhaust_manifold.turbo_log"));
        a.Install("turbocharger", new PartInstanceFactory(8_000_000).Create(TestContent.Database.GetPart("turbo.t28_ball")));
        var sim = SimFactory.Create(a);
        Hold(sim, 5500, 120);
        var report = Assert.Single(sim.Damage.Failures);
        Assert.Equal(FailureSeverity.Catastrophic, report.Severity);
        Assert.Contains(report.ContributingFactors, f => f.Contains("MAP sensor"));
        Assert.Contains(report.Recommendations, r => r.Contains("MAP sensor") || r.Contains("standalone"));
    }

    [Fact]
    public void OilStarvationSpinsABearing()
    {
        var sim = SimFactory.Create();
        Hold(sim, 6500, 300, sumpG: 1.25);
        var report = Assert.Single(sim.Damage.Failures);
        Assert.Equal(FailureMode.OilStarvation, report.Mode);
        Assert.Contains(report.Recommendations, r => r.Contains("baffled oil pan"));
        Assert.True(sim.Config.Part("crankshaft").Wear > 0.5, "collateral journal damage");
    }

    [Fact]
    public void BaffledPanPreventsStarvationAtTheSameCorneringLoad()
    {
        var sim = SimFactory.Create(("oil_pan", "k20.oil_pan.baffled"));
        Hold(sim, 6500, 120, sumpG: 1.25);
        Assert.Empty(sim.Damage.Failures);
    }

    [Fact]
    public void OverheatingBlowsTheHeadGasketAndCostsPower()
    {
        var sim = SimFactory.Create();
        var before = Hold(sim, 5000, 1, coolantK: 363.15);
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 6500, CoolingAirSpeed = 0.5 };
        for (int i = 0; i < 200_000 && sim.Damage.Failures.Count == 0; i++) sim.Step(0.01, input);
        var report = Assert.Single(sim.Damage.Failures);
        Assert.Equal(FailureMode.HeadGasketBreach, report.Mode);
        Assert.Equal(FailureSeverity.Degraded, report.Severity);
        Assert.Contains(report.ContributingFactors, f => f.Contains("boiling"));
        Assert.False(sim.Damage.Seized, "a blown gasket still runs");
        var after = Hold(sim, 5000, 1, coolantK: 363.15);
        Assert.True(after.Torque < 0.85 * before.Torque);
        Assert.True(EngineDiagnostics.CompressionTestBar(sim.Config.Assembly) < 0.5 * EngineDiagnostics.CompressionTestBar(TestContent.StockK20()));
    }

    [Fact]
    public void SmallTurboOverspeedKillsTheTurboButNotTheEngine()
    {
        // Asked for more boost than it can make efficiently at 7000 rpm, the small turbo over-speeds.
        var sim = SimFactory.Create(TurboTests.TurboBuild("turbo.t25_small"), "gasoline_98", TurboTests.TuneWithBoost(200));
        Hold(sim, 7000, 60);
        var report = Assert.Single(sim.Damage.Failures);
        Assert.Equal(FailureMode.TurboOverspeed, report.Mode);
        Assert.False(sim.Damage.Seized);
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 6000, CoolantTemperatureOverride = 363.15 };
        EngineTelemetry after = sim.Step(0.005, input);
        for (int i = 0; i < 400; i++) after = sim.Step(0.005, input);
        Assert.True(after.BoostKpa < 15, $"no boost from a failed turbo ({after.BoostKpa:F0} kPa)");
        Assert.True(after.Firing, "the engine keeps running");
        Assert.Contains(report.Recommendations, r => r.Contains("larger compressor"));
    }

    [Fact]
    public void ReplacingFailedPartsRestoresTheEngine()
    {
        var sim = SimFactory.Create();
        Hold(sim, 9300, 20, throttle: 0.0);
        Assert.True(sim.Damage.Seized);
        var a = sim.Config.Assembly;
        var failed = a.Installed.Where(kv => kv.Value.IsFailed).Select(kv => kv.Key).ToList();
        Assert.NotEmpty(failed);
        foreach (var slot in failed)
            TestContent.Swap(a, slot, a.PartIn(slot)!.Definition.Id);
        var fresh = SimFactory.Create(a);
        var t = Hold(fresh, 5000, 1);
        Assert.False(t.Seized);
        Assert.True(t.Torque > 150);
    }

    [Fact]
    public void ReportTextReadsLikeADiagnosis()
    {
        var sim = SimFactory.Create();
        Hold(sim, 9300, 20, throttle: 0.0);
        string text = sim.Damage.Failures[0].ToText();
        Assert.Contains("ENGINE FAILURE", text);
        Assert.Contains("Cause:", text);
        Assert.Contains("Contributing factors:", text);
        Assert.Contains("Recommendations:", text);
        Assert.Contains("9,300 rpm", text);
    }

    [Fact]
    public void WarningsReportKnockAndLowOilPressure()
    {
        var tune = SimFactory.StockTune();
        tune.KnockControlEnabled = false;
        tune.OffsetIgnition(10, 90);
        var sim = SimFactory.Create(SimFactory.Assembly(), fuel: "gasoline_91", tune: tune);
        Hold(sim, 2500, 0.2);
        Assert.Contains(sim.Damage.Warnings, w => w.Code == "knock");

        var oil = SimFactory.Create();
        oil.Step(0.005, new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 6000, SumpAccelerationG = 1.4 });
        Assert.Contains(oil.Damage.Warnings, w => w.Code == "oil_pressure" && w.Level == WarningLevel.Danger);
        Assert.Contains(oil.Damage.Warnings, w => w.Code == "oil_surge");
    }

    [Fact]
    public void CompressionTestDetectsWornRings()
    {
        var a = TestContent.StockK20();
        double healthy = EngineDiagnostics.CompressionTestBar(a);
        Assert.InRange(healthy, 11, 16);
        a.PartIn("pistons")!.Wear = 0.8;
        Assert.True(EngineDiagnostics.CompressionTestBar(a) < 0.8 * healthy);
    }

    [Fact]
    public void DamageIsDeterministic()
    {
        (double time, FailureMode mode) Run()
        {
            var sim = SimFactory.Create();
            Hold(sim, 6500, 300, sumpG: 1.25);
            return (sim.Damage.Failures[0].Time, sim.Damage.Failures[0].Mode);
        }
        Assert.Equal(Run(), Run());
    }
}
