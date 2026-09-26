using CarSim.Core.Common;
using CarSim.Core.Simulation;

namespace CarSim.Core.Ecu;

/// <summary>
/// Base cam-phase map generator for content authors (<c>carsim calibrate-cams</c>), alongside
/// <see cref="VeCalibrator"/> and <see cref="SparkCalibrator"/>: at every rpm column and throttle of the VE sweep it
/// holds the engine on a steady-state dyno at each intake cam advance the phaser allows and keeps the one that traps
/// the most air per cycle (the full-load objective of a factory phaser map). The result is written onto the tune's
/// rpm × MAP axes at the MAP each best point ran at. Part-load strategies that trade filling for internal EGR are not
/// modelled (the engine model has no pumping or emissions benefit from it), so every row is a filling optimum.
/// The game never calls it.
/// </summary>
public static class CamPhaseCalibrator
{
    /// <param name="sim">Engine to calibrate (state advanced; damage suspended meanwhile). Needs a phaser its ECU can drive.</param>
    /// <param name="stepDeg">Advance increment searched, crank degrees.</param>
    /// <param name="holdSeconds">Settling time per point (the phaser lags its target by <see cref="EngineSimulation.CamPhaserTimeConstant"/>).</param>
    /// <param name="coolantK">Test-cell coolant temperature.</param>
    public static double[][] Calibrate(EngineSimulation sim, double stepDeg = 5.0, double holdSeconds = 0.8, double coolantK = 363.15)
    {
        var original = sim.Ecu.Tune;
        var rpmAxis = original.IgnitionAdvance.XAxis;
        var loadAxis = original.IgnitionAdvance.YAxis;
        var table = new double[loadAxis.Count][];
        for (int r = 0; r < loadAxis.Count; r++) table[r] = new double[rpmAxis.Count];
        double range = sim.Config.IntakePhaserRange;
        if (range <= 0) return table;

        bool damage = sim.DamageEnabled;
        sim.DamageEnabled = false;
        try
        {
            int steps = Math.Max(1, (int)Math.Round(holdSeconds / VeCalibrator.DynoStep));
            int candidates = (int)Math.Floor(range / stepDeg + 1e-9) + 1;
            var advances = Enumerable.Range(0, candidates).Select(i => Math.Min(range, i * stepDeg)).ToArray();
            var tunes = advances.Select(a => WithAdvance(original, a)).ToArray();
            int lastInside = -1;
            for (int c = 0; c < rpmAxis.Count; c++) if (rpmAxis[c] < original.RevLimitRpm) lastInside = c;
            int measured = lastInside >= 0 ? lastInside + 1 : rpmAxis.Count;
            for (int c = 0; c < measured; c++)
            {
                var samples = new List<(double MapKpa, double Advance)>();
                foreach (double throttle in VeCalibrator.ThrottleSweep)
                {
                    var input = new EngineInputs
                    {
                        Throttle = throttle, SpeedMode = SpeedMode.Held, HeldRpm = rpmAxis[c], CoolantTemperatureOverride = coolantK,
                    };
                    double bestAir = double.NegativeInfinity, bestMap = 0, bestAdvance = 0;
                    for (int i = 0; i < tunes.Length; i++)
                    {
                        sim.Ecu.Tune = tunes[i];
                        EngineTelemetry t = sim.Step(VeCalibrator.DynoStep, input);
                        for (int k = 1; k < steps; k++) t = sim.Step(VeCalibrator.DynoStep, input);
                        if (t.AirPerCycle > bestAir + 1e-12)
                        {
                            bestAir = t.AirPerCycle;
                            bestMap = Units.PaToKpa(t.ManifoldPressure);
                            bestAdvance = advances[i];
                        }
                    }
                    if (bestAir > 0) samples.Add((bestMap, bestAdvance));
                }
                samples.Sort((a, b) => a.MapKpa.CompareTo(b.MapKpa));
                for (int r = 0; r < loadAxis.Count; r++)
                    table[r][c] = samples.Count == 0 ? 0.0 : Math.Round(VeCalibrator.Interpolate(samples, loadAxis[r]) * 2.0) / 2.0;
            }
            for (int c = measured; c < rpmAxis.Count; c++)
                for (int r = 0; r < loadAxis.Count; r++) table[r][c] = measured > 0 ? table[r][measured - 1] : 0.0;
            return table;
        }
        finally
        {
            sim.Ecu.Tune = original;
            sim.DamageEnabled = damage;
        }
    }

    /// <summary>A copy of <paramref name="tune"/> commanding <paramref name="advanceDeg"/> everywhere.</summary>
    private static EcuTune WithAdvance(EcuTune tune, double advanceDeg)
    {
        var d = tune.ToDocument();
        d.IntakeCamAdvanceDeg = d.LoadAxisKpa.Select(_ => d.RpmAxis.Select(_ => advanceDeg).ToArray()).ToArray();
        var copy = EcuTune.FromDocument(d);
        copy.KnockControlEnabled = false;
        return copy;
    }
}
