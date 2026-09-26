using CarSim.Core.Common;
using CarSim.Core.Simulation;

namespace CarSim.Core.Ecu;

/// <summary>
/// Base spark-map generator for content authors (<c>carsim calibrate-spark</c>), the ignition counterpart of
/// <see cref="VeCalibrator"/>: holds the engine on a simulated steady-state dyno over the same throttle sweep at
/// every rpm column, reads the best-torque (MBT) advance and the knock-limited advance at each point — debug
/// channels no player screen shows, as a calibration engineer finds them by sweeping timing — and writes
/// <c>min(MBT − mbtMargin, knock limit − knockMargin)</c> onto the tune's rpm × MAP axes. The knock limit is
/// accurate near the running advance, so each pass runs on the previous pass's table. The game never calls it.
/// </summary>
public static class SparkCalibrator
{
    /// <param name="sim">Engine to calibrate, running on the fuel the map is for (its state is advanced; damage is suspended).</param>
    /// <param name="knockMarginDeg">Degrees kept under the knock limit on that fuel.</param>
    /// <param name="mbtMarginDeg">Degrees kept under best-torque timing.</param>
    /// <param name="holdSeconds">Settling time per point.</param>
    /// <param name="passes">Measurement passes.</param>
    /// <param name="coolantK">Test-cell coolant temperature.</param>
    public static double[][] Calibrate(EngineSimulation sim, double knockMarginDeg = 1.5, double mbtMarginDeg = 1.0,
        double holdSeconds = 1.0, int passes = 3, double coolantK = 363.15)
    {
        var original = sim.Ecu.Tune;
        bool damage = sim.DamageEnabled;
        var work = original.Clone();
        work.KnockControlEnabled = false;
        sim.Ecu.Tune = work;
        sim.DamageEnabled = false;
        try
        {
            double[][] table = work.IgnitionAdvance.ToRows();
            for (int pass = 0; pass < passes; pass++)
            {
                table = MeasurePass(sim, work, knockMarginDeg, mbtMarginDeg, holdSeconds, coolantK);
                for (int r = 0; r < table.Length; r++)
                    for (int c = 0; c < table[r].Length; c++) work.IgnitionAdvance[r, c] = table[r][c];
            }
            return table;
        }
        finally
        {
            sim.Ecu.Tune = original;
            sim.DamageEnabled = damage;
        }
    }

    private static double[][] MeasurePass(EngineSimulation sim, EcuTune tune, double knockMargin, double mbtMargin, double holdSeconds, double coolantK)
    {
        var rpmAxis = tune.IgnitionAdvance.XAxis;
        var loadAxis = tune.IgnitionAdvance.YAxis;
        var table = new double[loadAxis.Count][];
        for (int r = 0; r < loadAxis.Count; r++) table[r] = new double[rpmAxis.Count];
        int steps = Math.Max(1, (int)Math.Round(holdSeconds / VeCalibrator.DynoStep));
        // At and above the rev limit the engine only runs on the limiter (fuel cut): those columns repeat the last one inside.
        int lastInside = -1;
        for (int c = 0; c < rpmAxis.Count; c++) if (rpmAxis[c] < tune.RevLimitRpm) lastInside = c;
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
                EngineTelemetry t = sim.Step(VeCalibrator.DynoStep, input);
                for (int i = 1; i < steps; i++) t = sim.Step(VeCalibrator.DynoStep, input);
                if (!t.Firing) continue;
                double advance = Math.Min(t.MbtAdvance - mbtMargin, t.KnockLimitAdvance - knockMargin);
                samples.Add((Units.PaToKpa(t.ManifoldPressure), advance));
            }
            samples.Sort((a, b) => a.MapKpa.CompareTo(b.MapKpa));
            for (int r = 0; r < loadAxis.Count; r++)
                table[r][c] = samples.Count > 0 ? Math.Round(VeCalibrator.Interpolate(samples, loadAxis[r]) * 2.0) / 2.0
                    : c > 0 ? table[r][c - 1] : tune.IgnitionAdvance[r, c];
        }
        for (int c = measured; c < rpmAxis.Count; c++)
            for (int r = 0; r < loadAxis.Count; r++) table[r][c] = table[r][measured - 1];
        return table;
    }
}
