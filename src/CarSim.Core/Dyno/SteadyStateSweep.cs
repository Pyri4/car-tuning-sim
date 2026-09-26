using CarSim.Core.Common;
using CarSim.Core.Simulation;

namespace CarSim.Core.Dyno;

/// <summary>
/// Steady-state (step) engine-dyno test: the brake holds each speed until readings settle, then the
/// point is logged. Coolant is held by the test cell's heat exchanger. Used for calibration and
/// regression tests; the interactive dyno also offers transient sweeps.
/// </summary>
public static class SteadyStateSweep
{
    public const double Dt = 0.005;

    public static IReadOnlyList<EngineTelemetry> Run(EngineSimulation sim, double startRpm, double endRpm, double stepRpm,
        double throttle = 1.0, double settleSeconds = 1.5, double coolantK = 363.15)
    {
        var points = new List<EngineTelemetry>();
        for (double rpm = startRpm; rpm <= endRpm + 1e-6; rpm += stepRpm)
            points.Add(Settle(sim, rpm, throttle, settleSeconds, coolantK));
        return points;
    }

    public static EngineTelemetry Settle(EngineSimulation sim, double rpm, double throttle = 1.0, double settleSeconds = 1.5, double coolantK = 363.15)
    {
        var input = new EngineInputs
        {
            Throttle = throttle,
            SpeedMode = SpeedMode.Held,
            HeldRpm = rpm,
            CoolantTemperatureOverride = coolantK,
            CoolingAirSpeed = 10.0,
        };
        EngineTelemetry? last = null;
        int steps = Math.Max(1, (int)Math.Round(settleSeconds / Dt));
        for (int i = 0; i < steps; i++) last = sim.Step(Dt, input);
        return last!;
    }
}
