using CarSim.Core.Dyno;
using CarSim.Core.Simulation;
using CarSim.Core.Tests.Simulation;

namespace CarSim.Core.Tests.Dyno;

public class DynoRunnerTests
{
    [Fact]
    public void SweepCoversTheRangeInOrder()
    {
        var run = new DynoRunner(SimFactory.Create(), new DynoSettings { StartRpm = 2000, EndRpm = 7000 }).RunToCompletion();
        Assert.True(run.Completed);
        Assert.InRange(run.Samples[0].Rpm, 2000, 2100);
        Assert.InRange(run.Samples[^1].Rpm, 6900, 7000);
        for (int i = 1; i < run.Samples.Count; i++) Assert.True(run.Samples[i].Rpm > run.Samples[i - 1].Rpm);
        Assert.NotNull(run.PeakPower);
        Assert.InRange(run.PeakPower!.PowerKw, 95, 125);
    }

    [Fact]
    public void SteadyStateLogsOnePointPerStep()
    {
        var run = new DynoRunner(SimFactory.Create(), new DynoSettings { Mode = DynoMode.SteadyState, StartRpm = 2000, EndRpm = 4000, StepRpm = 500, SettleSeconds = 0.5 })
            .RunToCompletion();
        Assert.Equal(5, run.Samples.Count);
        Assert.Equal(new[] { 2000.0, 2500, 3000, 3500, 4000 }, run.Samples.Select(s => Math.Round(s.Rpm)).ToArray());
    }

    [Fact]
    public void IncrementalAdvanceMatchesRunToCompletion()
    {
        var settings = new DynoSettings { StartRpm = 3000, EndRpm = 5000 };
        var a = new DynoRunner(SimFactory.Create(), settings).RunToCompletion();
        var runner = new DynoRunner(SimFactory.Create(), settings);
        while (!runner.IsDone) runner.Advance(1.0 / 60.0);
        var b = runner.Result();
        Assert.Equal(a.Samples.Count, b.Samples.Count);
        Assert.Equal(a.PeakPower!.Power, b.PeakPower!.Power, 6);
    }

    [Fact]
    public void PullEndsAtTheRevLimiter()
    {
        var run = new DynoRunner(SimFactory.Create(), new DynoSettings { StartRpm = 6000, EndRpm = 9800 }).RunToCompletion();
        Assert.True(run.Completed);
        Assert.Contains("rev limiter", run.Note);
        Assert.InRange(run.Samples[^1].Rpm, 7400, 7600);
        Assert.Empty(run.Failures);
    }

    /// <summary>Standalone ECU (no 8,200 rpm hardware cap) with the limiter set to 9,000 rpm.</summary>
    private static EngineSimulation HighLimit()
    {
        var tune = SimFactory.StockTune();
        tune.RevLimitRpm = 9000;
        return SimFactory.Create(SimFactory.Assembly(("flywheel", "k20.flywheel.light"), ("ecu", "ecu.standalone")), tune: tune);
    }

    [Fact]
    public void RunAbortsWhenTheEngineFails()
    {
        // Worn springs float early; with the limiter raised the engine revs itself into its valves.
        var sim = HighLimit();
        sim.Config.Part("valve_springs").Wear = 1.0;
        var run = new DynoRunner(sim, new DynoSettings { StartRpm = 7000, EndRpm = 9800, RampRpmPerSecond = 300 }).RunToCompletion();
        Assert.False(run.Completed);
        Assert.NotEmpty(run.Failures);
        Assert.Contains("rpm", run.AbortReason);
        var again = new DynoRunner(SimFactory.Create(), new DynoSettings());
        Assert.Equal(DynoPhase.Ready, again.Phase);
    }

    [Fact]
    public void SeizedEngineCannotBeTested()
    {
        var sim = HighLimit();
        sim.Config.Part("valve_springs").Wear = 1.0;
        new DynoRunner(sim, new DynoSettings { StartRpm = 7000, EndRpm = 9800, RampRpmPerSecond = 300 }).RunToCompletion();
        var second = new DynoRunner(sim, new DynoSettings()).RunToCompletion();
        Assert.False(second.Completed);
        Assert.Contains("seized", second.AbortReason);
    }

    [Fact]
    public void ComparisonShowsTheEffectOfAChange()
    {
        var baseline = new DynoRunner(SimFactory.Create(), new DynoSettings(), "stock").RunToCompletion();
        var exhaust = new DynoRunner(SimFactory.Create(("exhaust", "exhaust.race_76mm")), new DynoSettings(), "race exhaust").RunToCompletion();
        var cmp = DynoComparison.Compare(baseline, exhaust);
        Assert.True(cmp.PeakPowerDeltaW > 0);
        Assert.True(cmp.AreaUnderPowerDeltaPercent > 0);
        Assert.NotEmpty(cmp.Points);
    }

    [Fact]
    public void TurboLagVisibleInSweepButNotSteadyState()
    {
        var sweep = new DynoRunner(TurboTests.TurboSim(TurboTests.TurboBuild()), new DynoSettings { StartRpm = 2500, EndRpm = 6000, RampRpmPerSecond = 800 }).RunToCompletion();
        var steady = new DynoRunner(TurboTests.TurboSim(TurboTests.TurboBuild()), new DynoSettings { Mode = DynoMode.SteadyState, StartRpm = 2500, EndRpm = 6000, StepRpm = 500, SettleSeconds = 2.0 }).RunToCompletion();
        double sweepBoost = sweep.At(4500, t => t.BoostKpa)!.Value;
        double steadyBoost = steady.At(4500, t => t.BoostKpa)!.Value;
        Assert.True(sweepBoost < steadyBoost - 5, $"sweep {sweepBoost:F0} kPa vs steady {steadyBoost:F0} kPa");
    }

    [Fact]
    public void CsvHasHeaderAndOneLinePerSample()
    {
        var run = new DynoRunner(SimFactory.Create(), new DynoSettings { StartRpm = 3000, EndRpm = 4000 }).RunToCompletion();
        var lines = run.ToCsv().TrimEnd().Split('\n');
        Assert.StartsWith("time_s,rpm,torque_nm", lines[0]);
        Assert.Equal(run.Samples.Count + 1, lines.Length);
    }
}
