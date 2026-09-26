using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

/// <summary>Timestep independence and the per-step allocation budget of the engine model.</summary>
public class NumericsTests
{
    private static EngineTelemetry Settle(EngineSimulation sim, double rpm, double seconds, double dt)
    {
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = rpm, CoolantTemperatureOverride = 363.15 };
        EngineTelemetry t = null!;
        for (int i = 0; i < (int)Math.Round(seconds / dt); i++) t = sim.Step(dt, input);
        return t;
    }

    [Fact]
    public void EngineOutputConvergesAsTheTimestepShrinks()
    {
        var fine = Settle(SimFactory.Create(), 5000, 1.0, 0.0005);
        var coarse = Settle(SimFactory.Create(), 5000, 1.0, 0.01);
        Assert.Equal(1.0, coarse.Torque / fine.Torque, 2);
        // Turbo: spool is an ODE (rotor inertia, boost PI); steady boost must not depend on the step.
        var turboFine = Settle(TurboTests.TurboSim(TurboTests.TurboBuild()), 6000, 4.0, 0.001);
        var turboCoarse = Settle(TurboTests.TurboSim(TurboTests.TurboBuild()), 6000, 4.0, 0.005);
        Assert.Equal(1.0, turboCoarse.MapKpa / turboFine.MapKpa, 2);
        Assert.Equal(1.0, turboCoarse.Torque / turboFine.Torque, 2);
    }

    [Fact]
    public void AnEngineStepStaysWithinItsAllocationBudget()
    {
        // A regression guard, not a target: the air path's nested root finders still allocate closures.
        // Budget ≈ 15 % above what a turbo engine step allocated when this test was written (≈ 14 KB).
        const long budgetBytes = 16_500;
        var sim = TurboTests.TurboSim(TurboTests.TurboBuild());
        Settle(sim, 5000, 0.5, 0.002);
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 5000, CoolantTemperatureOverride = 363.15 };
        const int steps = 2000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < steps; i++) sim.Step(0.002, input);
        long perStep = (GC.GetAllocatedBytesForCurrentThread() - before) / steps;
        Assert.True(perStep < budgetBytes, $"{perStep} B allocated per engine step (budget {budgetBytes} B)");
    }
}
