using CarSim.Core.Common;
using CarSim.Core.Simulation;

namespace CarSim.Core.Ecu;

/// <summary>
/// Base-map generator for content authors: runs the engine on a simulated steady-state dyno and writes its
/// real breathing into the ECU's volumetric-efficiency table (what a calibration engineer does with a
/// wideband and a lot of time). It is a development tool (<c>carsim calibrate-ve</c>) — the game never
/// calls it, so the player's ECU only knows what its tune says.
/// </summary>
public static class VeCalibrator
{
    /// <summary>Throttle openings sampled at each engine speed.</summary>
    public static readonly double[] ThrottleSweep = { 0.0, 0.02, 0.04, 0.07, 0.1, 0.14, 0.2, 0.28, 0.38, 0.5, 0.65, 0.8, 1.0 };

    public const double DynoStep = 0.005;

    /// <summary>
    /// Measures the VE table for the simulation's engine on the rpm × MAP axes of its current tune. Turbo
    /// builds with ECU boost control are run with the boost target at the top of the load axis so the whole
    /// range is sampled; cells beyond what the engine can reach at an engine speed take the nearest measured
    /// value. Two passes: the second is fuelled from the first pass's table.
    /// </summary>
    /// <param name="sim">Engine to calibrate (its state is advanced; damage is suspended meanwhile).</param>
    /// <param name="holdSeconds">Settling time per point (turbo builds need a few seconds to spool).</param>
    /// <param name="passes">Measurement passes.</param>
    /// <param name="coolantK">Test-cell coolant temperature.</param>
    public static double[][] Calibrate(EngineSimulation sim, double holdSeconds = 1.0, int passes = 2, double coolantK = 363.15)
    {
        var original = sim.Ecu.Tune;
        bool damage = sim.DamageEnabled;
        var work = original.Clone();
        double topLoad = work.VolumetricEfficiency.YAxis[^1];
        if (work.BoostTarget != null)
            for (int c = 0; c < work.BoostTarget.Columns; c++) work.BoostTarget[0, c] = topLoad;
        sim.Ecu.Tune = work;
        sim.DamageEnabled = false;
        try
        {
            double[][] table = work.VolumetricEfficiency.ToRows();
            for (int pass = 0; pass < passes; pass++)
            {
                table = MeasurePass(sim, work, holdSeconds, coolantK);
                for (int r = 0; r < table.Length; r++)
                    for (int c = 0; c < table[r].Length; c++) work.VolumetricEfficiency[r, c] = table[r][c];
            }
            return table;
        }
        finally
        {
            sim.Ecu.Tune = original;
            sim.DamageEnabled = damage;
        }
    }

    /// <summary>The VE a speed-density ECU would need at this operating point: true air / (MAP · V_cyl / (R · IAT)).</summary>
    public static double MeasuredVe(EngineTelemetry t, double displacementCc, int cylinders)
    {
        double cylinderVolume = Units.CcToM3(displacementCc) / cylinders;
        return t.AirPerCycle * PhysicalConstants.AirGasConstant * t.ManifoldTemperature / (t.ManifoldPressure * cylinderVolume);
    }

    private static double[][] MeasurePass(EngineSimulation sim, EcuTune tune, double holdSeconds, double coolantK)
    {
        var rpmAxis = tune.VolumetricEfficiency.XAxis;
        var loadAxis = tune.VolumetricEfficiency.YAxis;
        var table = new double[loadAxis.Count][];
        for (int r = 0; r < loadAxis.Count; r++) table[r] = new double[rpmAxis.Count];
        int cylinders = sim.Config.Geometry.Cylinders;
        int steps = Math.Max(1, (int)Math.Round(holdSeconds / DynoStep));
        // Above the rev limit the engine only runs on the limiter (fuel cut, floating valves); like an OEM
        // calibration, those columns repeat the last one inside the limit instead of being measured.
        int lastInside = -1;
        for (int c = 0; c < rpmAxis.Count; c++) if (rpmAxis[c] <= tune.RevLimitRpm) lastInside = c;
        int measured = lastInside >= 0 ? lastInside + 1 : rpmAxis.Count;
        for (int c = 0; c < measured; c++)
        {
            var samples = new List<(double MapKpa, double Ve)>();
            foreach (double throttle in ThrottleSweep)
            {
                var input = new EngineInputs
                {
                    Throttle = throttle, SpeedMode = SpeedMode.Held, HeldRpm = rpmAxis[c], CoolantTemperatureOverride = coolantK,
                };
                EngineTelemetry t = sim.Step(DynoStep, input);
                for (int i = 1; i < steps; i++) t = sim.Step(DynoStep, input);
                if (t.ManifoldPressure > 0 && t.AirPerCycle > 0)
                    samples.Add((Units.PaToKpa(t.ManifoldPressure), MeasuredVe(t, tune.DisplacementCc, cylinders)));
            }
            samples.Sort((a, b) => a.MapKpa.CompareTo(b.MapKpa));
            for (int r = 0; r < loadAxis.Count; r++) table[r][c] = Math.Round(Interpolate(samples, loadAxis[r]), 3);
        }
        for (int c = measured; c < rpmAxis.Count; c++)
            for (int r = 0; r < loadAxis.Count; r++) table[r][c] = table[r][measured - 1];
        return table;
    }

    /// <summary>Linear interpolation over (MAP, value) samples sorted by MAP, held flat beyond both ends.</summary>
    internal static double Interpolate(List<(double MapKpa, double Ve)> samples, double mapKpa)
    {
        if (samples.Count == 0) return 0.85;
        if (mapKpa <= samples[0].MapKpa) return samples[0].Ve;
        if (mapKpa >= samples[^1].MapKpa) return samples[^1].Ve;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].MapKpa >= mapKpa)
            {
                var (m0, v0) = samples[i - 1];
                var (m1, v1) = samples[i];
                double t = m1 > m0 ? (mapKpa - m0) / (m1 - m0) : 0.0;
                return v0 + (v1 - v0) * t;
            }
        }
        return samples[^1].Ve;
    }
}
