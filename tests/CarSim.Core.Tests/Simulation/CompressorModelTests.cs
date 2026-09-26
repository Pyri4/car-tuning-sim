using CarSim.Core.Common;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

/// <summary>
/// Properties the compressor model must have everywhere, not just at the shipped boost level. Each of
/// these failed on the original model (non-converging efficiency/PR iteration, work collapsing at choke).
/// </summary>
public class CompressorModelTests
{
    public static readonly string[] Turbos = { "turbo.t25_small", "turbo.t28_ball", "turbo.t35_big" };

    private static TurbochargerSpec Spec(string id) => TestContent.Database.GetPart(id).GetSpec<TurbochargerSpec>();

    private static double Omega(TurbochargerSpec s, double speedRatio) => speedRatio * s.MaxShaftSpeed;

    [Theory]
    [InlineData("turbo.t25_small")]
    [InlineData("turbo.t28_ball")]
    [InlineData("turbo.t35_big")]
    public void IslandCentreSitsOnTheAuthoredPeakPoint(string id)
    {
        var s = Spec(id);
        double n = TurbochargerModel.IslandSpeedRatio(s);
        var p = TurbochargerModel.Compressor(s, Omega(s, n), s.CompressorPeakEfficiencyFlowKgS, TurbochargerModel.ReferencePressure, TurbochargerModel.ReferenceTemperature);
        Assert.Equal(s.CompressorPeakEfficiencyPressureRatio, p.PressureRatio, 3);
        Assert.Equal(s.CompressorPeakEfficiency, p.Efficiency, 3);
        Assert.InRange(n, 0.4, 0.95);
    }

    [Theory]
    [InlineData("turbo.t25_small")]
    [InlineData("turbo.t28_ball")]
    [InlineData("turbo.t35_big")]
    public void PressureRatioRisesWithShaftSpeedAtAnyFlow(string id)
    {
        var s = Spec(id);
        for (double flow = 0.0; flow <= 1.2 * s.CompressorChokeFlowKgS; flow += 0.02 * s.CompressorChokeFlowKgS)
        {
            double last = 0.0;
            for (double n = 0.1; n <= 1.3; n += 0.01)
            {
                var p = TurbochargerModel.Compressor(s, Omega(s, n), flow, 101_325, 298.15);
                Assert.True(double.IsFinite(p.PressureRatio) && p.PressureRatio > 0);
                // Monotonic wherever the flow is below twice the speed's choke flow (the whole operating
                // envelope); deeper in choke the comparison restarts.
                if (p.ChokeRatio < 2.0)
                {
                    Assert.True(p.PressureRatio >= last - 1e-12, $"{id}: flow {flow:F3} kg/s, n {n:F2}: PR {p.PressureRatio:F4} after {last:F4}");
                    last = p.PressureRatio;
                }
                else last = 0.0;
            }
        }
    }

    [Theory]
    [InlineData("turbo.t25_small")]
    [InlineData("turbo.t28_ball")]
    [InlineData("turbo.t35_big")]
    public void SpeedLinesAreContinuousThroughSurgeAndChoke(string id)
    {
        var s = Spec(id);
        foreach (double n in new[] { 0.3, 0.5, 0.7, 0.9, 1.0, 1.15 })
        {
            double step = 0.0005 * s.CompressorChokeFlowKgS;
            var prev = TurbochargerModel.Compressor(s, Omega(s, n), 0.0, 101_325, 298.15);
            bool sawSurge = prev.Surge, sawChoke = false;
            for (double flow = step; flow <= 1.3 * s.CompressorChokeFlowKgS; flow += step)
            {
                var p = TurbochargerModel.Compressor(s, Omega(s, n), flow, 101_325, 298.15);
                Assert.True(Math.Abs(p.PressureRatio - prev.PressureRatio) / prev.PressureRatio < 0.01,
                    $"{id} n={n}: PR jumps {prev.PressureRatio:F4} → {p.PressureRatio:F4} at {flow:F4} kg/s");
                Assert.True(Math.Abs(p.Power - prev.Power) < 0.01 * Math.Max(1000, prev.Power), $"{id} n={n}: power jumps at {flow:F4} kg/s");
                sawSurge |= p.Surge;
                sawChoke |= p.ChokeRatio > 1.0;
                prev = p;
            }
            Assert.True(sawChoke, "the sweep crosses choke");
            if (n >= 0.7) Assert.True(sawSurge, "the sweep starts left of the surge line");
        }
    }

    [Theory]
    [InlineData("turbo.t25_small")]
    [InlineData("turbo.t28_ball")]
    [InlineData("turbo.t35_big")]
    public void WorkIsRetainedThroughChokeAndChokeFlowGrowsWithSpeed(string id)
    {
        // The original model scaled the Euler work towards zero near choke, so the compressor stopped
        // absorbing power and the shaft ran away to a 829 m/s tip speed.
        var s = Spec(id);
        foreach (double n in new[] { 0.5, 1.0, 1.2 })
        {
            double omega = Omega(s, n);
            double tip = omega * s.CompressorTipRadius;
            foreach (double x in new[] { 0.5, 0.9, 1.0, 1.2, 1.5 })
            {
                double flow = x * TurbochargerModel.ChokeFlow(s, n);
                var p = TurbochargerModel.Compressor(s, omega, flow, TurbochargerModel.ReferencePressure, TurbochargerModel.ReferenceTemperature);
                Assert.True(p.SpecificWork >= (1 - 2 * TurbochargerModel.WorkBacksweep) * TurbochargerModel.WorkCoefficient * tip * tip);
                Assert.True(p.Power >= flow * p.SpecificWork);
            }
        }
        Assert.True(TurbochargerModel.ChokeFlow(s, 1.0) > TurbochargerModel.ChokeFlow(s, 0.5));
        Assert.Equal(s.CompressorChokeFlowKgS, TurbochargerModel.ChokeFlow(s, 1.0), 9);
    }

    [Fact]
    public void PastChokeTheWheelIsARestrictionThatVanishesAtLowSpeed()
    {
        var s = Spec("turbo.t28_ball");
        // Fast wheel, flow 20 % past choke: less pressure out than in.
        var fast = TurbochargerModel.Compressor(s, Omega(s, 0.6), 1.2 * TurbochargerModel.ChokeFlow(s, 0.6), 101_325, 298.15);
        Assert.True(fast.PressureRatio < 1.0);
        Assert.True(fast.Efficiency < 0.0);
        // Barely turning: the wheel neither pumps nor restricts (an engine idling with a stopped turbo breathes).
        var slow = TurbochargerModel.Compressor(s, Omega(s, 0.01), 0.05, 101_325, 298.15);
        Assert.InRange(slow.PressureRatio, 0.99, 1.01);
        var stopped = TurbochargerModel.Compressor(s, 0.0, 0.05, 101_325, 298.15);
        Assert.Equal(1.0, stopped.PressureRatio);
        Assert.Equal(0.0, stopped.Power);
    }

    [Fact]
    public void SurgeRecirculatesAndCostsEfficiencyGradually()
    {
        var s = Spec("turbo.t28_ball");
        double omega = Omega(s, 0.8);
        var healthy = TurbochargerModel.Compressor(s, omega, 0.16, 101_325, 298.15);
        var surging = TurbochargerModel.Compressor(s, omega, 0.01, 101_325, 298.15);
        Assert.False(healthy.Surge);
        Assert.True(surging.Surge);
        // Almost no through-flow, yet the wheel still absorbs power recirculating air.
        Assert.True(surging.Power > 3 * 0.01 * surging.SpecificWork);
    }

    [Theory]
    [InlineData("turbo.t25_small")]
    [InlineData("turbo.t28_ball")]
    [InlineData("turbo.t35_big")]
    public void AirPathHasExactlyOneSolution(string id)
    {
        // The through-flow is the root of demand(ṁ) − ṁ. Scan it: exactly one sign change for any turbo
        // speed, wastegate, engine speed and throttle.
        var a = TurboTests.TurboBuild(id);
        var sim = TurboTests.TurboSim(a);
        var path = new AirPath(sim.Config);
        foreach (double n in new[] { 0.0, 0.3, 0.6, 0.9, 1.1 })
        foreach (double rpm in new[] { 1000.0, 3000, 5000, 7000 })
        foreach (double throttle in new[] { 0.1, 1.0 })
        {
            double thr = sim.Config.ThrottleCdA * (EngineSimulation.ThrottleAreaFraction(throttle) + EngineSimulation.ThrottleLeakAreaFraction);
            var k = new AirPathConditions(rpm, thr, 101_325, 298.15, 1100, 363.15, 1 / 12.5, 5, 99_999,
                n * sim.Config.Turbo!.MaxShaftSpeed, 0.0, 10, 950);
            double ve = path.VeDynamic(rpm, 99_999);
            double hi = 4.0 * sim.Config.Geometry.Displacement * rpm / 120.0 * 1.2;
            int changes = 0;
            double last = double.NaN;
            for (int i = 1; i <= 400; i++)
            {
                double m = hi * i / 400.0;
                double r = path.Evaluate(m, ve, in k).MassFlow - m;
                Assert.True(double.IsFinite(r));
                if (!double.IsNaN(last) && Math.Sign(r) != Math.Sign(last)) changes++;
                last = r;
            }
            Assert.True(changes == 1, $"{id}: n {n}, {rpm} rpm, throttle {throttle}: {changes} roots");
        }
    }
}
