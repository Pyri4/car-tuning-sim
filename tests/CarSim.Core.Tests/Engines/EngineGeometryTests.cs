using CarSim.Core.Common;
using CarSim.Core.Engines;

namespace CarSim.Core.Tests.Engines;

public class EngineGeometryTests
{
    private static EngineGeometry GeometryOf(EngineAssembly a) =>
        EngineGeometry.TryCreate(a, out var missing) ?? throw new Xunit.Sdk.XunitException("missing: " + string.Join(",", missing));

    [Fact]
    public void StockK20Displacement()
    {
        var g = GeometryOf(TestContent.StockK20());
        // π/4 · 86² · 86 · 4 = 1998.2 cc
        Assert.Equal(1998.2, Units.M3ToCc(g.Displacement), 1);
        Assert.Equal(499.55, Units.M3ToCc(g.SweptVolumePerCylinder), 1);
    }

    [Fact]
    public void StockK20IsZeroDeckAndTenPointFiveToOne()
    {
        var g = GeometryOf(TestContent.StockK20());
        Assert.Equal(0.0, Units.MToMm(g.DeckClearance), 6);
        Assert.Equal(0.7, Units.MToMm(g.PistonToHeadClearance), 6);
        Assert.Equal(10.5, g.CompressionRatio, 1);
    }

    [Fact]
    public void CompressionRatioFollowsDefinition()
    {
        var g = GeometryOf(TestContent.StockK20());
        double expected = (g.SweptVolumePerCylinder + g.ClearanceVolume) / g.ClearanceVolume;
        Assert.Equal(expected, g.CompressionRatio, 12);
        // Clearance volume = chamber + gasket cylinder + deck + dish.
        double gasket = Math.PI / 4 * 0.087 * 0.087 * 0.0007;
        Assert.Equal(50.4e-6 + gasket + 0.0 - 2e-6, g.ClearanceVolume, 12);
    }

    [Fact]
    public void DishedPistonsLowerCompression()
    {
        var a = TestContent.StockK20();
        double stock = GeometryOf(a).CompressionRatio;
        TestContent.Swap(a, "pistons", "k20.pistons.forged_lc");
        double low = GeometryOf(a).CompressionRatio;
        Assert.True(low < stock);
        Assert.Equal(8.86, low, 1);
    }

    [Fact]
    public void DomedPistonsRaiseCompression()
    {
        var a = TestContent.StockK20();
        TestContent.Swap(a, "pistons", "k20.pistons.forged_hc");
        Assert.Equal(11.5, GeometryOf(a).CompressionRatio, 1);
    }

    [Fact]
    public void ThickerGasketLowersCompression()
    {
        var a = TestContent.StockK20();
        double stock = GeometryOf(a).CompressionRatio;
        TestContent.Swap(a, "head_gasket", "k20.head_gasket.thick");
        var g = GeometryOf(a);
        Assert.True(g.CompressionRatio < stock - 0.4);
        Assert.Equal(1.3, Units.MToMm(g.PistonToHeadClearance), 6);
    }

    [Fact]
    public void StrokerCrankIncreasesDisplacementAndPushesPistonsIntoHead()
    {
        var a = TestContent.StockK20();
        TestContent.Swap(a, "crankshaft", "k20.crankshaft.stroker90");
        var g = GeometryOf(a);
        Assert.Equal(2091.2, Units.M3ToCc(g.Displacement), 0);
        // +2 mm crank radius with the stock 30 mm compression-height pistons: crown 2 mm above deck.
        Assert.Equal(-2.0, Units.MToMm(g.DeckClearance), 6);
        Assert.True(g.PistonToHeadClearance < 0);
    }

    [Fact]
    public void StrokerCrankWithStrokerPistonsIsZeroDeck()
    {
        var a = TestContent.StockK20();
        TestContent.Swap(a, "crankshaft", "k20.crankshaft.stroker90");
        TestContent.Swap(a, "pistons", "k20.pistons.stroker_lc");
        var g = GeometryOf(a);
        Assert.Equal(0.0, Units.MToMm(g.DeckClearance), 6);
        Assert.Equal(9.2, g.CompressionRatio, 1);
    }

    [Fact]
    public void RodInertiaLoadMatchesSliderCrankFormulaAndScalesWithRpmSquared()
    {
        var g = GeometryOf(TestContent.StockK20());
        double w = Units.RpmToRadPerSec(8000);
        double mRecip = 0.380 + 0.540 / 3.0;
        double expected = mRecip * w * w * 0.043 * (1 + 0.043 / 0.139);
        Assert.Equal(expected, g.RodInertiaLoad(w), 6);
        Assert.Equal(4.0, g.RodInertiaLoad(2 * w) / g.RodInertiaLoad(w), 9);
    }

    [Fact]
    public void MeanPistonSpeed()
    {
        var g = GeometryOf(TestContent.StockK20());
        Assert.Equal(2 * 0.086 * 7000 / 60.0, g.MeanPistonSpeed(7000), 12);
    }

    [Fact]
    public void GeometryUnavailableWhileBottomEndIncomplete()
    {
        var a = TestContent.StockK20();
        foreach (var s in a.RemovalSequenceFor("pistons")) a.Remove(s, out _);
        a.Remove("pistons", out _);
        Assert.Null(EngineGeometry.TryCreate(a, out var missing));
        Assert.Contains("pistons", missing);
    }
}
