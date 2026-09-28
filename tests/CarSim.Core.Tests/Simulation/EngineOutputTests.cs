using CarSim.Core.Common;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

/// <summary>Torque/power production and how parts change it.</summary>
public class EngineOutputTests
{
    [Fact]
    public void StockEngineCalibration()
    {
        var points = SimFactory.Sweep(SimFactory.Create());
        var peakT = SimFactory.PeakTorque(points);
        var peakP = SimFactory.PeakPower(points);
        Assert.InRange(peakT.Torque, 170, 210);
        Assert.InRange(peakT.Rpm, 3000, 5000);
        Assert.InRange(peakP.PowerKw, 95, 125);
        Assert.InRange(peakP.Rpm, 6000, 7600);
        Assert.InRange(points.Max(p => p.VolumetricEfficiency), 0.85, 1.0);
    }

    [Fact]
    public void PowerIsTorqueTimesAngularVelocity()
    {
        foreach (var t in SimFactory.Sweep(SimFactory.Create(), 1000, 7500, 1500))
            Assert.Equal(t.Torque * Units.RpmToRadPerSec(t.Rpm), t.Power, 1e-6);
    }

    [Fact]
    public void TorqueFollowsBmepDefinition()
    {
        var sim = SimFactory.Create();
        var t = SimFactory.At(sim, 4000);
        Assert.Equal(t.Bmep * sim.Config.Geometry.Displacement / (4 * Math.PI), t.Torque, 1e-6);
        Assert.Equal(t.Imep - t.Pmep - t.Fmep, t.Bmep, 1e-6);
    }

    [Fact]
    public void ClosingTheThrottleCreatesVacuumAndCutsTorque()
    {
        var sim = SimFactory.Create();
        double lastTorque = double.NegativeInfinity, lastMap = 0;
        foreach (double throttle in new[] { 0.1, 0.25, 0.5, 1.0 })
        {
            var t = SimFactory.At(sim, 3000, throttle);
            Assert.True(t.Torque > lastTorque, $"torque at throttle {throttle}");
            Assert.True(t.ManifoldPressure > lastMap, $"MAP at throttle {throttle}");
            lastTorque = t.Torque;
            lastMap = t.ManifoldPressure;
        }
        var closed = SimFactory.At(sim, 3000, 0.1);
        Assert.True(closed.MapKpa < 60, "part throttle pulls manifold vacuum");
        Assert.True(closed.Pmep > 0.3e5, "pumping loss at part throttle");
    }

    [Fact]
    public void AirflowRisesWithRpmAtFullThrottle()
    {
        var points = SimFactory.Sweep(SimFactory.Create(), 1000, 7000, 1000);
        for (int i = 1; i < points.Count; i++) Assert.True(points[i].AirMassFlow > points[i - 1].AirMassFlow);
    }

    [Fact]
    public void FreerExhaustGainsMostAtHighRpm()
    {
        var stock = SimFactory.Create();
        var race = SimFactory.Create(("exhaust", "exhaust.race_76mm"));
        double lowGain = SimFactory.At(race, 2000).Torque - SimFactory.At(stock, 2000).Torque;
        double highGain = SimFactory.At(race, 7000).Torque - SimFactory.At(stock, 7000).Torque;
        Assert.True(highGain > 0);
        Assert.True(highGain > 2 * Math.Max(lowGain, 0.1), $"low {lowGain:F2}, high {highGain:F2}");
        Assert.True(SimFactory.At(race, 7000).ExhaustBackPressure < SimFactory.At(stock, 7000).ExhaustBackPressure);
    }

    [Fact]
    public void BiggerCamsMoveTheTorquePeakUpAndCostLowEnd()
    {
        var stock = SimFactory.Sweep(SimFactory.Create(), 1500, 8000, 500);
        var cams = SimFactory.Sweep(SimFactory.Create(
            ("valve_springs", "k20.valve_springs.performance"), ("camshafts", "k20.cams.race")), 1500, 8000, 500);
        Assert.True(SimFactory.PeakTorque(cams).Rpm > SimFactory.PeakTorque(stock).Rpm);
        Assert.True(cams[0].Torque < stock[0].Torque, "race cams lose low-rpm torque");
    }

    [Fact]
    public void ShortRunnersTuneToAHigherSpeedWithoutMovingTheValveEvent()
    {
        // Intake Gas Dynamics 2.0: the runner is its own wave gain, tuned by its geometry; the valve-event filling peak
        // belongs to the cams and does not move with the runner.
        var stock = SimFactory.Create();
        var shortRunner = SimFactory.Create(("intake_manifold", "k20.intake.short_runner"));
        Assert.True(shortRunner.Config.Banks[0].RunnerStages[0].TunedRpmAtReference > stock.Config.Banks[0].RunnerStages[0].TunedRpmAtReference);
        Assert.Equal(stock.Config.Banks[0].VePeakRpm, shortRunner.Config.Banks[0].VePeakRpm);
    }

    [Fact]
    public void PortedHeadFlowGainGrowsWithCamLift()
    {
        // The head's advantage is at high lift: a high-lift cam uses more of it.
        double FlowGain(params (string, string)[] extra)
        {
            var baseSim = SimFactory.Create(extra);
            var ported = SimFactory.Create(extra.Append(("cylinder_head", "k20.head.ported")).ToArray());
            return ported.Config.Banks[0].Profiles[0].IntakePortCdA / baseSim.Config.Banks[0].Profiles[0].IntakePortCdA;
        }
        double withStockCams = FlowGain();
        double withRaceCams = FlowGain(("valve_springs", "k20.valve_springs.performance"), ("camshafts", "k20.cams.race"));
        Assert.True(withStockCams > 1.0);
        Assert.True(withRaceCams > withStockCams, $"stock cams ×{withStockCams:F3}, race cams ×{withRaceCams:F3}");
    }

    [Fact]
    public void PortedHeadAndRaceCamsMakeMorePeakPower()
    {
        var tune = SimFactory.StockTune();
        tune.RevLimitRpm = 8200;
        var stock = SimFactory.Sweep(SimFactory.Create(SimFactory.Assembly(), tune: tune), 4000, 8000, 500);
        var built = SimFactory.Sweep(SimFactory.Create(SimFactory.Assembly(
            ("cylinder_head", "k20.head.ported"), ("valve_springs", "k20.valve_springs.performance"),
            ("camshafts", "k20.cams.race"), ("intake_manifold", "k20.intake.short_runner"),
            ("exhaust_manifold", "k20.exhaust_manifold.header421"), ("exhaust", "exhaust.race_76mm")), tune: tune.Clone()), 4000, 8000, 500);
        var ps = SimFactory.PeakPower(stock);
        var pb = SimFactory.PeakPower(built);
        Assert.True(pb.Power > ps.Power * 1.12, $"stock {ps.PowerHp:F0} hp, built {pb.PowerHp:F0} hp");
        Assert.True(pb.Rpm >= ps.Rpm);
    }

    [Fact]
    public void StrokerMakesMoreTorque()
    {
        var stock = SimFactory.Create();
        var stroker = SimFactory.Create(("crankshaft", "k20.crankshaft.stroker90"), ("pistons", "k20.pistons.stroker_lc"));
        Assert.True(stroker.Config.Geometry.Displacement > stock.Config.Geometry.Displacement);
        // Lower compression (9.2 vs 10.5) partly offsets the displacement gain, but low-rpm torque still rises.
        Assert.True(SimFactory.At(stroker, 3000).AirPerCycle > SimFactory.At(stock, 3000).AirPerCycle);
    }

    [Fact]
    public void HigherCompressionMakesMoreTorqueAtTheSameTiming()
    {
        var stock = SimFactory.At(SimFactory.Create(), 5000);
        var hc = SimFactory.At(SimFactory.Create(("pistons", "k20.pistons.forged_hc")), 5000);
        Assert.True(hc.Torque > stock.Torque);
        Assert.True(hc.KnockLimitAdvance < stock.KnockLimitAdvance, "and brings the knock limit closer");
        Assert.True(hc.PeakCylinderPressure > stock.PeakCylinderPressure);
    }

    [Fact]
    public void TimingHasAnOptimumAndOverAdvanceKnocks()
    {
        double TorqueWithOffset(double offset)
        {
            var tune = SimFactory.StockTune();
            tune.KnockControlEnabled = false;
            tune.OffsetIgnition(offset);
            return SimFactory.At(SimFactory.Create(SimFactory.Assembly(), tune: tune), 3000, 1.0, 0.3).Torque;
        }
        double retarded = TorqueWithOffset(-8), stock = TorqueWithOffset(0), nearMbt = TorqueWithOffset(2.5);
        Assert.True(stock > retarded);
        Assert.True(nearMbt > stock);

        var tune = SimFactory.StockTune();
        tune.KnockControlEnabled = false;
        tune.OffsetIgnition(15);
        var t = SimFactory.At(SimFactory.Create(SimFactory.Assembly(), tune: tune), 3000, 1.0, 0.3);
        Assert.True(t.KnockIntensity > 0);
        Assert.True(t.Torque < nearMbt);
    }

    [Fact]
    public void RevLimiterCutsFuel()
    {
        var t = SimFactory.At(SimFactory.Create(), 7800);
        Assert.True(t.RevLimiterActive);
        Assert.False(t.Firing);
        Assert.True(t.Torque < 0);
    }

    [Fact]
    public void ValveFloatCollapsesBreathing()
    {
        var tune = SimFactory.StockTune();
        tune.RevLimitRpm = 8200;
        var sim = SimFactory.Create(SimFactory.Assembly(), tune: tune);
        double floatRpm = sim.ValveFloatRpm();
        var below = SimFactory.At(sim, floatRpm - 300);
        var above = SimFactory.At(sim, Math.Min(8150, floatRpm + 600));
        Assert.False(below.ValveFloat);
        Assert.True(above.ValveFloat);
        Assert.True(above.VeDynamic < below.VeDynamic * 0.9);
    }

    [Fact]
    public void RodLoadGrowsWithTheSquareOfRpm()
    {
        var sim = SimFactory.Create();
        var a = SimFactory.At(sim, 3000, 0.0, 0.1);
        var b = SimFactory.At(sim, 6000, 0.0, 0.1);
        Assert.Equal(4.0, b.RodTensileLoad / a.RodTensileLoad, 6);
    }

    [Fact]
    public void SimulationIsDeterministic()
    {
        EngineTelemetry Run()
        {
            var sim = SimFactory.Create();
            var input = new EngineInputs { Throttle = 0.7, SpeedMode = SpeedMode.Free, Starter = true };
            EngineTelemetry? t = null;
            for (int i = 0; i < 600; i++)
            {
                input.Starter = i < 100;
                t = sim.Step(0.005, input);
            }
            return t!;
        }
        Assert.Equal(Run(), Run());
    }
}
