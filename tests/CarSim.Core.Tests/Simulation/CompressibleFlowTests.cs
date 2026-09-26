using CarSim.Core.Common;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

public class CompressibleFlowTests
{
    private const double G = PhysicalConstants.AirGamma;
    private const double R = PhysicalConstants.AirGasConstant;

    [Fact]
    public void CriticalPressureRatioForAir()
    {
        Assert.Equal(0.5283, CompressibleFlow.CriticalPressureRatio(1.4), 4);
    }

    [Fact]
    public void SmallPressureDropMatchesIncompressibleOrifice()
    {
        double cdA = 1e-3, p = 101325, t = 293.15, dp = 500;
        double rho = p / (R * t);
        double expected = cdA * Math.Sqrt(2 * rho * dp);
        double actual = CompressibleFlow.MassFlow(cdA, p, t, p - dp, G, R);
        Assert.Equal(expected, actual, expected * 0.005);
    }

    [Fact]
    public void FlowChokesBelowCriticalRatio()
    {
        double cdA = 1e-3, p = 200_000, t = 300;
        double atCrit = CompressibleFlow.MassFlow(cdA, p, t, p * 0.5283, G, R);
        double belowCrit = CompressibleFlow.MassFlow(cdA, p, t, p * 0.2, G, R);
        Assert.Equal(atCrit, belowCrit, 1e-9);
        Assert.True(CompressibleFlow.MassFlow(cdA, p, t, p * 0.8, G, R) < atCrit);
    }

    [Fact]
    public void MassFlowIncreasesMonotonicallyWithPressureDrop()
    {
        double last = 0;
        for (double pr = 0.99; pr > 0.53; pr -= 0.02)
        {
            double m = CompressibleFlow.MassFlow(1e-3, 101325, 300, 101325 * pr, G, R);
            Assert.True(m > last);
            last = m;
        }
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.05)]
    [InlineData(0.15)]
    public void DownstreamAndUpstreamPressureInvertMassFlow(double massFlow)
    {
        double cdA = 1e-3, pUp = 101325, t = 300;
        double pDown = CompressibleFlow.DownstreamPressure(cdA, pUp, t, massFlow, G, R, out bool choked);
        Assert.False(choked);
        Assert.Equal(massFlow, CompressibleFlow.MassFlow(cdA, pUp, t, pDown, G, R), 9);
        double back = CompressibleFlow.UpstreamPressure(cdA, pDown, t, massFlow, G, R);
        Assert.Equal(pUp, back, 1.0);
    }

    [Fact]
    public void OverChokedFlowReportsChoked()
    {
        double choke = CompressibleFlow.ChokedMassFlow(1e-4, 101325, 300, G, R);
        CompressibleFlow.DownstreamPressure(1e-4, 101325, 300, choke * 1.5, G, R, out bool choked);
        Assert.True(choked);
    }

    [Fact]
    public void CfmRatingRoundTrips()
    {
        double cdA = CompressibleFlow.EffectiveAreaFromCfm(200);
        Assert.Equal(200, CompressibleFlow.CfmFromEffectiveArea(cdA), 9);
        // A 200 CFM port has roughly 8.8 cm² of effective area.
        Assert.InRange(cdA * 1e4, 8.5, 9.1);
    }

    [Fact]
    public void BrentFindsRoots()
    {
        Assert.Equal(Math.Sqrt(2), RootFinder.Brent(x => x * x - 2, 0, 2, 1e-12), 10);
        Assert.Equal(Math.PI, RootFinder.Brent(Math.Sin, 3, 4, 1e-12), 10);
        Assert.Throws<ArgumentException>(() => RootFinder.Brent(x => x * x + 1, -1, 1, 1e-9));
    }
}
