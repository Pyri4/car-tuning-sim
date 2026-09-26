using CarSim.Core.Common;

namespace CarSim.Core.Tests.Common;

public class InterpolationTests
{
    [Fact]
    public void Curve1DInterpolatesLinearlyAndClamps()
    {
        var c = Curve1D.FromPoints((0, 0), (10, 100), (20, 50));
        Assert.Equal(0, c.Evaluate(-5));
        Assert.Equal(50, c.Evaluate(5), 9);
        Assert.Equal(100, c.Evaluate(10), 9);
        Assert.Equal(75, c.Evaluate(15), 9);
        Assert.Equal(50, c.Evaluate(99));
    }

    [Fact]
    public void Curve1DRejectsNonIncreasingX()
    {
        Assert.Throws<ArgumentException>(() => Curve1D.FromPoints((0, 0), (0, 1)));
        Assert.Throws<ArgumentException>(() => Curve1D.FromPoints((1, 0), (0, 1)));
    }

    [Fact]
    public void Table2DBilinearAndClamped()
    {
        var t = new Table2D(new double[] { 1000, 2000 }, new double[] { 50, 100 },
            new[] { new double[] { 10, 20 }, new double[] { 30, 40 } });
        Assert.Equal(10, t.Evaluate(1000, 50), 9);
        Assert.Equal(40, t.Evaluate(2000, 100), 9);
        Assert.Equal(25, t.Evaluate(1500, 75), 9);
        Assert.Equal(10, t.Evaluate(0, 0), 9);
        Assert.Equal(40, t.Evaluate(9999, 9999), 9);
        Assert.Equal(15, t.Evaluate(1500, 10), 9);
    }

    [Fact]
    public void Table2DCloneIsIndependent()
    {
        var t = new Table2D(new double[] { 1, 2 }, new double[] { 1, 2 }, 5.0);
        var c = t.Clone();
        c[0, 0] = 99;
        Assert.Equal(5.0, t[0, 0]);
        Assert.Equal(99.0, c[0, 0]);
    }

    [Fact]
    public void LagFactorApproachesOne()
    {
        Assert.Equal(1.0 - Math.Exp(-1.0), MathUtil.LagFactor(1.0, 1.0), 12);
        Assert.Equal(1.0, MathUtil.LagFactor(0.1, 0.0));
    }
}
