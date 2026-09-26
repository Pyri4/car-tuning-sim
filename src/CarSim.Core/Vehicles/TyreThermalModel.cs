using CarSim.Core.Common;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Vehicles;

/// <summary>
/// Tread temperature of each tyre (lumped tread + carcass). Heated by sliding (most of the friction
/// work stays in the rubber) and by rolling hysteresis (the rolling-resistance power, so an
/// under-inflated tyre runs hotter); cooled by the airflow over it. The pressure follows the
/// temperature (the cold pressure set in the garage rises on track), and grip follows the compound's
/// temperature window: cold semi-slicks are slippery, and so are overheated tyres.
/// </summary>
public sealed class TyreThermalModel
{
    /// <summary>Share of the sliding friction work that heats the tyre (the rest heats the road).</summary>
    public const double SlidingHeatFraction = 0.7;

    /// <summary>Convective cooling per tyre: base + per m/s of road speed, W/K.</summary>
    public const double CoolingBaseWPerK = 8.0, CoolingPerMs = 1.5;

    public const double AmbientK = 298.15;

    private readonly VehicleConfiguration _c;

    /// <summary>Starts warmed up (each tyre at its compound's optimal temperature).</summary>
    public TyreThermalModel(VehicleConfiguration config)
    {
        _c = config;
        for (int w = 0; w < 4; w++) TemperatureK[w] = Units.CToK(config.TireOf(w).OptimalTemperatureC);
    }

    /// <summary>Tread temperature per wheel (K), <see cref="Wheel"/> order.</summary>
    public double[] TemperatureK { get; } = new double[4];

    /// <summary>Puts every tyre at one temperature (e.g. ambient, straight out of the garage).</summary>
    public void SetAll(double temperatureK)
    {
        for (int w = 0; w < 4; w++) TemperatureK[w] = temperatureK;
    }

    public TyreState StateOf(int w) => TyreState.At(_c.TireOf(w), TemperatureK[w]);

    /// <param name="dt">Step (s).</param>
    /// <param name="slidingEnergy">Friction work per wheel this step (J).</param>
    /// <param name="rollingEnergy">Rolling-resistance work per wheel this step (J).</param>
    /// <param name="speed">Road speed (m/s).</param>
    public void Update(double dt, double[] slidingEnergy, double[] rollingEnergy, double speed)
    {
        double h = CoolingBaseWPerK + CoolingPerMs * speed;
        for (int w = 0; w < 4; w++)
        {
            TireSpec t = _c.TireOf(w);
            double heat = SlidingHeatFraction * slidingEnergy[w] + rollingEnergy[w];
            TemperatureK[w] += (heat - h * (TemperatureK[w] - AmbientK) * dt) / t.ThermalMassJPerK;
        }
    }
}
