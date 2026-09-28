namespace CarSim.Core.Common;

/// <summary>Deterministic scalar root finding.</summary>
public static class RootFinder
{
    /// <summary>
    /// Brent's method (inverse quadratic interpolation with bisection fallback). <paramref name="f"/>(a)
    /// and <paramref name="f"/>(b) must have opposite signs (or one be zero). Converges superlinearly
    /// and never leaves the bracket.
    /// </summary>
    public static double Brent(Func<double, double> f, double a, double b, double tolerance, int maxIterations = 100) =>
        Brent(new DelegateFunction(f), a, b, tolerance, maxIterations);

    /// <summary>
    /// <see cref="Brent(Func{double, double}, double, double, double, int)"/> for a function given as a struct: the JIT
    /// specialises the solver for it and calls it directly, so a hot caller allocates nothing and pays no delegate call
    /// (the same iterations, bit for bit).
    /// </summary>
    public static double Brent<TFunction>(TFunction f, double a, double b, double tolerance, int maxIterations = 100)
        where TFunction : struct, IRootFunction
    {
        double fa = f.Evaluate(a), fb = f.Evaluate(b);
        if (fa == 0) return a;
        if (fb == 0) return b;
        if (fa * fb > 0) throw new ArgumentException($"Root not bracketed: f({a})={fa}, f({b})={fb}.");
        double c = a, fc = fa, d = b - a, e = d;
        for (int iter = 0; iter < maxIterations; iter++)
        {
            if (fb * fc > 0)
            {
                c = a; fc = fa; d = b - a; e = d;
            }
            if (Math.Abs(fc) < Math.Abs(fb))
            {
                a = b; b = c; c = a;
                fa = fb; fb = fc; fc = fa;
            }
            double tol1 = 2.0 * 1e-15 * Math.Abs(b) + 0.5 * tolerance;
            double xm = 0.5 * (c - b);
            if (Math.Abs(xm) <= tol1 || fb == 0) return b;
            if (Math.Abs(e) >= tol1 && Math.Abs(fa) > Math.Abs(fb))
            {
                double p, q, r;
                double s = fb / fa;
                if (a == c)
                {
                    p = 2.0 * xm * s;
                    q = 1.0 - s;
                }
                else
                {
                    q = fa / fc;
                    r = fb / fc;
                    p = s * (2.0 * xm * q * (q - r) - (b - a) * (r - 1.0));
                    q = (q - 1.0) * (r - 1.0) * (s - 1.0);
                }
                if (p > 0) q = -q;
                p = Math.Abs(p);
                double min1 = 3.0 * xm * q - Math.Abs(tol1 * q);
                double min2 = Math.Abs(e * q);
                if (2.0 * p < Math.Min(min1, min2))
                {
                    e = d;
                    d = p / q;
                }
                else
                {
                    d = xm;
                    e = d;
                }
            }
            else
            {
                d = xm;
                e = d;
            }
            a = b;
            fa = fb;
            b += Math.Abs(d) > tol1 ? d : (xm >= 0 ? tol1 : -tol1);
            fb = f.Evaluate(b);
        }
        return b;
    }

    private readonly struct DelegateFunction(Func<double, double> f) : IRootFunction
    {
        public double Evaluate(double x) => f(x);
    }
}

/// <summary>A scalar function for <see cref="RootFinder.Brent{TFunction}"/>.</summary>
public interface IRootFunction
{
    double Evaluate(double x);
}
