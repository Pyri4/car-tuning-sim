namespace CarSim.Core.Vehicles;

public enum Surface { Asphalt, Kerb, Grass }

/// <summary>
/// A closed circuit: a centreline sampled every ~2 m (from a Catmull-Rom spline through control points)
/// with a constant width, kerbs and grass run-off. Provides surface grip for the tyres, progress along
/// the lap, and the geometry the game renders.
/// </summary>
/// <summary>A piece of circuit: a straight, or a constant-radius arc turning left or right.</summary>
public readonly record struct TrackSegment(double Length, double Radius = 0, double AngleDeg = 0, bool Left = true)
{
    public static TrackSegment Straight(double length) => new(length);
    public static TrackSegment LeftArc(double radius, double angleDeg) => new(radius * angleDeg * Math.PI / 180, radius, angleDeg, true);
    public static TrackSegment RightArc(double radius, double angleDeg) => new(radius * angleDeg * Math.PI / 180, radius, angleDeg, false);
    public bool IsArc => Radius > 0;
}

/// <summary>
/// A closed circuit built from straights and constant-radius arcs (like a real test facility), sampled
/// every ~2 m, with constant width, kerbs and grass run-off. Provides surface grip for the tyres,
/// progress along the lap, exact curvature for drivers, and the geometry the game renders.
/// </summary>
public sealed class TrackLayout
{
    public const double KerbWidth = 1.2;
    public const double KerbGrip = 0.9;
    public const double GrassGrip = 0.55;

    private readonly double[] _x, _y, _s, _heading, _curvature;

    public TrackLayout(string name, IReadOnlyList<TrackSegment> segments, double width, double sampleSpacing = 2.0)
    {
        Name = name;
        Width = width;
        Segments = segments;
        var xs = new List<double>(); var ys = new List<double>(); var hs = new List<double>(); var ks = new List<double>();
        double x = 0, y = 0, h = 0;
        foreach (var seg in segments)
        {
            int n = Math.Max(1, (int)Math.Round(seg.Length / sampleSpacing));
            double k = seg.IsArc ? (seg.Left ? 1 : -1) / seg.Radius : 0.0;
            for (int i = 0; i < n; i++)
            {
                xs.Add(x); ys.Add(y); hs.Add(h); ks.Add(k);
                double ds = seg.Length / n;
                if (!seg.IsArc)
                {
                    x += Math.Cos(h) * ds;
                    y += Math.Sin(h) * ds;
                }
                else
                {
                    double sign = seg.Left ? 1 : -1;
                    double cx = x - sign * seg.Radius * Math.Sin(h), cy = y + sign * seg.Radius * Math.Cos(h);
                    h += sign * ds / seg.Radius;
                    x = cx + sign * seg.Radius * Math.Sin(h);
                    y = cy - sign * seg.Radius * Math.Cos(h);
                }
            }
        }
        ClosureError = Math.Sqrt(x * x + y * y);
        _x = xs.ToArray(); _y = ys.ToArray(); _heading = hs.ToArray(); _curvature = ks.ToArray();
        _s = new double[_x.Length];
        for (int i = 1; i < _x.Length; i++) _s[i] = _s[i - 1] + Math.Sqrt(Sq(_x[i] - _x[i - 1]) + Sq(_y[i] - _y[i - 1]));
        Length = _s[^1] + Math.Sqrt(Sq(x - _x[^1]) + Sq(y - _y[^1]));
        // Headings sampled at the start of each step; use the chord direction for a smooth centreline heading.
        for (int i = 0; i < _x.Length; i++)
        {
            int a = Wrap(i - 1), b = Wrap(i + 1);
            _heading[i] = Math.Atan2(_y[b] - _y[a], _x[b] - _x[a]);
        }
    }

    public string Name { get; }
    public double Width { get; }
    public double Length { get; }
    public IReadOnlyList<TrackSegment> Segments { get; }

    /// <summary>Distance between the end of the last segment and the start (0 for a properly closed layout).</summary>
    public double ClosureError { get; }

    public int Count => _x.Length;

    public (double X, double Y) Point(int i) => (_x[Wrap(i)], _y[Wrap(i)]);
    public double HeadingAt(int i) => _heading[Wrap(i)];

    /// <summary>Signed curvature (1/m, positive = left) of the segment the sample belongs to.</summary>
    public double CurvatureAt(int i) => _curvature[Wrap(i)];

    public double DistanceAt(int i) => _s[Wrap(i)];
    public int Wrap(int i) => ((i % Count) + Count) % Count;

    public static double NormalizeAngle(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }

    private static double Sq(double v) => v * v;

    /// <summary>Nearest centreline sample, searching around <paramref name="hint"/> (or everywhere if hint &lt; 0).</summary>
    public int Nearest(double x, double y, int hint = -1, int window = 40)
    {
        int best = 0;
        double bestD = double.MaxValue;
        if (hint < 0)
        {
            for (int i = 0; i < Count; i++)
            {
                double d = Sq(_x[i] - x) + Sq(_y[i] - y);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }
        for (int k = -window; k <= window; k++)
        {
            int i = Wrap(hint + k);
            double d = Sq(_x[i] - x) + Sq(_y[i] - y);
            if (d < bestD) { bestD = d; best = i; }
        }
        // Far from the hint (e.g. after a reset): fall back to a full search.
        if (bestD > Sq(3 * Width)) return Nearest(x, y);
        return best;
    }

    /// <summary>Signed lateral offset from the centreline at sample <paramref name="i"/> (positive = left).</summary>
    public double LateralOffset(double x, double y, int i)
    {
        double h = HeadingAt(i);
        var (px, py) = Point(i);
        return -(x - px) * Math.Sin(h) + (y - py) * Math.Cos(h);
    }

    public Surface SurfaceAt(double x, double y, int nearest)
    {
        double off = Math.Abs(LateralOffset(x, y, nearest));
        if (off <= Width / 2) return Surface.Asphalt;
        if (off <= Width / 2 + KerbWidth) return Surface.Kerb;
        return Surface.Grass;
    }

    public static double Grip(Surface s) => s switch { Surface.Asphalt => 1.0, Surface.Kerb => KerbGrip, _ => GrassGrip };

    /// <summary>
    /// The prototype test facility (1083 m, counter-clockwise, start/finish at the origin heading +X):
    /// a 420 m main straight into a braking zone, a hairpin complex (two R25 left-handers joined by a
    /// 50 m straight), a 222 m back straight, an R45 chicane, a fast R55 sweeper and a tight R20 final
    /// corner. The back straight's length is what closes the loop exactly.
    /// </summary>
    public static TrackLayout TestFacility() => new("Kestrel test facility", new[]
    {
        TrackSegment.Straight(420),
        TrackSegment.LeftArc(25, 90), TrackSegment.Straight(50), TrackSegment.LeftArc(25, 90),
        TrackSegment.Straight(221.756),
        TrackSegment.RightArc(45, 35), TrackSegment.LeftArc(45, 70), TrackSegment.RightArc(45, 35),
        TrackSegment.Straight(60),
        TrackSegment.LeftArc(55, 90), TrackSegment.Straight(25), TrackSegment.LeftArc(20, 90),
    }, width: 11.0);
}

/// <summary>Counts laps and times them from centreline progress.</summary>
public sealed class LapTimer
{
    private readonly TrackLayout _track;
    private int _lastIndex = -1;
    private double _lapStart = double.NaN;

    public LapTimer(TrackLayout track) => _track = track;

    public int Laps { get; private set; }
    public double? LastLap { get; private set; }
    public double? BestLap { get; private set; }
    public double CurrentLapTime(double now) => double.IsNaN(_lapStart) ? 0 : now - _lapStart;

    /// <summary>Update with the car's nearest centreline index; returns true when a lap was completed.</summary>
    public bool Update(int index, double time)
    {
        bool crossed = false;
        if (_lastIndex >= 0)
        {
            int n = _track.Count;
            // Crossing sample 0 forwards (from the end of the lap back to the start).
            if (_lastIndex > n * 3 / 4 && index < n / 4)
            {
                if (!double.IsNaN(_lapStart))
                {
                    LastLap = time - _lapStart;
                    BestLap = BestLap is double b ? Math.Min(b, LastLap.Value) : LastLap;
                    Laps++;
                    crossed = true;
                }
                _lapStart = time;
            }
        }
        _lastIndex = index;
        return crossed;
    }
}
