using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests;

/// <summary>Builds engine simulations from the base content with optional part swaps and tune edits.</summary>
public static class SimFactory
{
    public static EngineAssembly Assembly(params (string slot, string part)[] swaps)
    {
        var a = TestContent.StockK20();
        foreach (var (slot, part) in swaps) TestContent.Swap(a, slot, part);
        return a;
    }

    public static EcuTune StockTune() => EcuTune.FromDocument(TestContent.Database.GetTune("k20.stock"));

    public static EngineSimulation Create(EngineAssembly assembly, string fuel = "gasoline_95", EcuTune? tune = null, EngineState? state = null)
    {
        tune ??= StockTune();
        var config = EngineConfiguration.Build(assembly, TestContent.Database.GetFuel(fuel), new ValidationContext(tune.RevLimitRpm)).GetOrThrow();
        return new EngineSimulation(config, tune, state ?? EngineState.Warm());
    }

    public static EngineSimulation Create(params (string slot, string part)[] swaps) => Create(Assembly(swaps));

    public static EngineTelemetry At(EngineSimulation sim, double rpm, double throttle = 1.0, double seconds = 1.0) =>
        SteadyStateSweep.Settle(sim, rpm, throttle, seconds);

    public static IReadOnlyList<EngineTelemetry> Sweep(EngineSimulation sim, double from = 1000, double to = 7500, double step = 500) =>
        SteadyStateSweep.Run(sim, from, to, step, settleSeconds: 1.0);

    public static EngineTelemetry PeakPower(IEnumerable<EngineTelemetry> points) => points.MaxBy(p => p.Power)!;
    public static EngineTelemetry PeakTorque(IEnumerable<EngineTelemetry> points) => points.MaxBy(p => p.Torque)!;
}
