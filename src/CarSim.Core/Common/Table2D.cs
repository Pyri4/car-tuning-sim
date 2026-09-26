namespace CarSim.Core.Common;

/// <summary>
/// Editable 2D lookup table with bilinear interpolation, used for ECU maps
/// (e.g. ignition advance vs RPM × manifold pressure). Axes must be strictly increasing.
/// Lookups outside the axes clamp to the edge cells.
/// Values are indexed [row = y (load) index, column = x (rpm) index].
/// </summary>
public sealed class Table2D
{
    private readonly double[] _xAxis;
    private readonly double[] _yAxis;
    private readonly double[,] _values;

    public Table2D(IReadOnlyList<double> xAxis, IReadOnlyList<double> yAxis, double fill = 0.0)
    {
        ValidateAxis(xAxis, nameof(xAxis));
        ValidateAxis(yAxis, nameof(yAxis));
        _xAxis = xAxis.ToArray();
        _yAxis = yAxis.ToArray();
        _values = new double[_yAxis.Length, _xAxis.Length];
        for (int r = 0; r < _yAxis.Length; r++)
            for (int c = 0; c < _xAxis.Length; c++)
                _values[r, c] = fill;
    }

    public Table2D(IReadOnlyList<double> xAxis, IReadOnlyList<double> yAxis, double[][] rows)
        : this(xAxis, yAxis)
    {
        if (rows.Length != _yAxis.Length) throw new ArgumentException($"Table needs {_yAxis.Length} rows, got {rows.Length}.");
        for (int r = 0; r < rows.Length; r++)
        {
            if (rows[r].Length != _xAxis.Length) throw new ArgumentException($"Table row {r} needs {_xAxis.Length} values, got {rows[r].Length}.");
            for (int c = 0; c < _xAxis.Length; c++) _values[r, c] = rows[r][c];
        }
    }

    private static void ValidateAxis(IReadOnlyList<double> axis, string name)
    {
        if (axis.Count == 0) throw new ArgumentException($"{name} must not be empty.");
        for (int i = 1; i < axis.Count; i++)
            if (!(axis[i] > axis[i - 1])) throw new ArgumentException($"{name} must be strictly increasing (index {i}).");
    }

    public IReadOnlyList<double> XAxis => _xAxis;
    public IReadOnlyList<double> YAxis => _yAxis;
    public int Columns => _xAxis.Length;
    public int Rows => _yAxis.Length;

    public double this[int row, int column]
    {
        get => _values[row, column];
        set => _values[row, column] = value;
    }

    public double Evaluate(double x, double y)
    {
        (int c0, int c1, double tx) = Locate(_xAxis, x);
        (int r0, int r1, double ty) = Locate(_yAxis, y);
        double a = _values[r0, c0] + (_values[r0, c1] - _values[r0, c0]) * tx;
        double b = _values[r1, c0] + (_values[r1, c1] - _values[r1, c0]) * tx;
        return a + (b - a) * ty;
    }

    private static (int lo, int hi, double t) Locate(double[] axis, double v)
    {
        if (v <= axis[0]) return (0, 0, 0.0);
        if (v >= axis[^1]) return (axis.Length - 1, axis.Length - 1, 0.0);
        int hi = Array.BinarySearch(axis, v);
        if (hi >= 0) return (hi, hi, 0.0);
        hi = ~hi;
        int lo = hi - 1;
        return (lo, hi, (v - axis[lo]) / (axis[hi] - axis[lo]));
    }

    public double[][] ToRows()
    {
        var rows = new double[Rows][];
        for (int r = 0; r < Rows; r++)
        {
            rows[r] = new double[Columns];
            for (int c = 0; c < Columns; c++) rows[r][c] = _values[r, c];
        }
        return rows;
    }

    public Table2D Clone() => new(_xAxis, _yAxis, ToRows());
}
