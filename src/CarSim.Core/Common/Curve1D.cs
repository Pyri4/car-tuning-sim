namespace CarSim.Core.Common;

/// <summary>
/// Piecewise-linear lookup table y = f(x). X values must be strictly increasing.
/// Queries outside the range are clamped to the end values (no extrapolation), which keeps
/// data-driven curves from producing absurd values when the simulation leaves the authored range.
/// </summary>
public sealed class Curve1D
{
    private readonly double[] _x;
    private readonly double[] _y;

    public Curve1D(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count) throw new ArgumentException("Curve x and y must have equal length.");
        if (x.Count == 0) throw new ArgumentException("Curve must have at least one point.");
        for (int i = 1; i < x.Count; i++)
        {
            if (!(x[i] > x[i - 1])) throw new ArgumentException($"Curve x values must be strictly increasing (index {i}).");
        }
        _x = x.ToArray();
        _y = y.ToArray();
    }

    /// <summary>Build from (x, y) pairs.</summary>
    public static Curve1D FromPoints(params (double x, double y)[] points) =>
        new(points.Select(p => p.x).ToArray(), points.Select(p => p.y).ToArray());

    public int Count => _x.Length;
    public IReadOnlyList<double> X => _x;
    public IReadOnlyList<double> Y => _y;
    public double MinX => _x[0];
    public double MaxX => _x[^1];

    public double Evaluate(double x)
    {
        if (x <= _x[0]) return _y[0];
        if (x >= _x[^1]) return _y[^1];
        int hi = Array.BinarySearch(_x, x);
        if (hi >= 0) return _y[hi];
        hi = ~hi;
        int lo = hi - 1;
        double t = (x - _x[lo]) / (_x[hi] - _x[lo]);
        return _y[lo] + (_y[hi] - _y[lo]) * t;
    }

    /// <summary>Returns a new curve with every y multiplied by <paramref name="factor"/>.</summary>
    public Curve1D Scaled(double factor) => new(_x, _y.Select(v => v * factor).ToArray());
}
