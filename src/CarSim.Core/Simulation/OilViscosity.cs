using CarSim.Core.Common;

namespace CarSim.Core.Simulation;

/// <summary>
/// Dynamic viscosity of a typical 5W-30 engine oil versus temperature (log-interpolated table).
/// Oil grades as content are future work; this is the single default oil.
/// </summary>
public static class OilViscosity
{
    // °C → ln(mPa·s)
    private static readonly Curve1D LnViscosity = new(
        new double[] { -20, 0, 20, 40, 60, 80, 100, 120, 140, 160, 200 },
        new double[] { 3000, 900, 180, 62, 28, 15, 10, 6.5, 4.5, 3.2, 1.8 }.Select(v => Math.Log(v)).ToArray());

    /// <summary>Reference viscosity at 100 °C, Pa·s.</summary>
    public const double Reference = 0.010;

    /// <summary>Viscosity in Pa·s at temperature <paramref name="kelvin"/>.</summary>
    public static double At(double kelvin) => Math.Exp(LnViscosity.Evaluate(Units.KToC(kelvin))) * 1e-3;
}
