using CarSim.Core.Simulation;

namespace CarSim.Core.Dyno;

/// <summary>
/// Accelerating (ramp) dyno pull: the brake lets the engine speed rise at a fixed rate under full
/// throttle, the way an inertia or ramp-controlled dyno does. Unlike a steady-state test this shows
/// transient effects — above all turbo lag.
/// </summary>
public static class TransientSweep
{
    public const double Dt = 0.005;

    /// <param name="sim">Engine to test (state is advanced).</param>
    /// <param name="startRpm">Pull start speed; the engine is stabilised there first.</param>
    /// <param name="endRpm">Pull end speed.</param>
    /// <param name="rampRpmPerSecond">Acceleration of the pull.</param>
    /// <param name="sampleEverySeconds">Logging interval.</param>
    /// <param name="preSettleSeconds">Time held at <paramref name="startRpm"/> before the pull, at the pre-pull throttle.</param>
    /// <param name="prePullThrottle">Throttle while holding the start speed (light load so the turbo is not pre-spooled).</param>
    /// <param name="coolantK">Coolant temperature held by the test cell, K.</param>
    public static IReadOnlyList<EngineTelemetry> Run(EngineSimulation sim, double startRpm, double endRpm, double rampRpmPerSecond,
        double sampleEverySeconds = 0.05, double preSettleSeconds = 2.0, double prePullThrottle = 0.15, double coolantK = 363.15)
    {
        if (rampRpmPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(rampRpmPerSecond));
        var input = new EngineInputs
        {
            Throttle = prePullThrottle,
            SpeedMode = SpeedMode.Held,
            HeldRpm = startRpm,
            CoolantTemperatureOverride = coolantK,
            CoolingAirSpeed = 10.0,
        };
        int settleSteps = (int)Math.Round(preSettleSeconds / Dt);
        for (int i = 0; i < settleSteps; i++) sim.Step(Dt, input);

        var samples = new List<EngineTelemetry>();
        input.Throttle = 1.0;
        double rpm = startRpm;
        double sinceSample = sampleEverySeconds;
        while (rpm <= endRpm)
        {
            input.HeldRpm = rpm;
            var t = sim.Step(Dt, input);
            sinceSample += Dt;
            if (sinceSample >= sampleEverySeconds - 1e-9)
            {
                samples.Add(t);
                sinceSample = 0.0;
            }
            rpm += rampRpmPerSecond * Dt;
        }
        return samples;
    }

    /// <summary>First sample at or above <paramref name="boostPa"/> (gauge), or null if never reached.</summary>
    public static EngineTelemetry? FirstAtBoost(IEnumerable<EngineTelemetry> samples, double boostPa) =>
        samples.FirstOrDefault(s => s.BoostPressure >= boostPa);
}
