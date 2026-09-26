using CarSim.Core.Common;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Simulation;

public class ThermalOilTests
{
    [Fact]
    public void OilPressureRisesWithRpmUntilTheReliefValve()
    {
        var sim = SimFactory.Create();
        double hot = Units.CToK(100);
        double idle = sim.OilPressure(850, hot, 1.0);
        double mid = sim.OilPressure(3000, hot, 1.0);
        double high = sim.OilPressure(7000, hot, 1.0);
        Assert.True(mid > idle);
        Assert.InRange(Units.PaToBar(idle), 0.6, 2.0);
        Assert.InRange(Units.PaToBar(mid), 2.5, 5.0);
        Assert.InRange(high, sim.Config.OilPump.ReliefPressure, sim.Config.OilPump.ReliefPressure * 1.1);
    }

    [Fact]
    public void HotOilAndWornBearingsLowerOilPressure()
    {
        var sim = SimFactory.Create();
        double normal = sim.OilPressure(2000, Units.CToK(100), 1.0);
        Assert.True(sim.OilPressure(2000, Units.CToK(140), 1.0) < normal);
        Assert.True(sim.OilPressure(2000, Units.CToK(40), 1.0) > normal);
        sim.Config.Part("main_bearings").Wear = 0.8;
        sim.Config.Part("rod_bearings").Wear = 0.8;
        double worn = sim.OilPressure(2000, Units.CToK(100), 1.0);
        Assert.True(worn < 0.5 * normal);
        Assert.True(worn < EngineSimulation.RequiredOilPressure(2000), "badly worn bearings fall below what the bearings need");
    }

    [Fact]
    public void OilStarvationUnderSustainedCornering()
    {
        var sim = SimFactory.Create();
        double normal = sim.OilPressure(5000, Units.CToK(100), 1.0);
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 5000, SumpAccelerationG = 1.3 };
        var t = sim.Step(0.005, input);
        Assert.True(t.OilPressure < 0.5 * normal);
    }

    [Fact]
    public void SustainedFullPowerWithWeakAirflowOverheats()
    {
        var sim = SimFactory.Create();
        sim.DamageEnabled = false;
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 6500, CoolingAirSpeed = 2.0 };
        EngineTelemetry t = sim.Step(0.01, input);
        for (int i = 0; i < 12_000; i++) t = sim.Step(0.01, input);
        Assert.True(t.CoolantC > 110, $"coolant {t.CoolantC:F1} °C");
    }

    [Fact]
    public void BetterRadiatorRunsCooler()
    {
        double Equilibrium(EngineSimulation sim)
        {
            var input = new EngineInputs { Throttle = 0.6, SpeedMode = SpeedMode.Held, HeldRpm = 5000, CoolingAirSpeed = 20.0 };
            EngineTelemetry t = sim.Step(0.02, input);
            for (int i = 0; i < 15_000; i++) t = sim.Step(0.02, input);
            return t.CoolantTemperature;
        }
        double stock = Equilibrium(SimFactory.Create());
        double race = Equilibrium(SimFactory.Create(("radiator", "radiator.alloy_race")));
        Assert.True(race < stock);
    }

    [Fact]
    public void ThermostatLetsAColdEngineWarmUp()
    {
        var sim = SimFactory.Create(SimFactory.Assembly(), state: new EngineState());
        var input = new EngineInputs { Throttle = 0.3, SpeedMode = SpeedMode.Held, HeldRpm = 3000, CoolingAirSpeed = 15.0 };
        EngineTelemetry t = sim.Step(0.02, input);
        for (int i = 0; i < 20_000; i++) t = sim.Step(0.02, input);
        Assert.InRange(t.CoolantC, 85, 100);
    }

    [Fact]
    public void EngineStartsIdlesAndRevs()
    {
        var sim = SimFactory.Create(SimFactory.Assembly(), state: EngineState.Warm());
        var input = new EngineInputs { Throttle = 0, Starter = true };
        EngineTelemetry t = sim.Step(0.005, input);
        for (int i = 0; i < 300; i++) t = sim.Step(0.005, input);
        input.Starter = false;
        for (int i = 0; i < 2000; i++) t = sim.Step(0.005, input);
        Assert.True(t.Running);
        Assert.InRange(t.Rpm, 650, 1100);

        input.Throttle = 0.5;
        for (int i = 0; i < 200; i++) t = sim.Step(0.005, input);
        Assert.True(t.Rpm > 2500, $"revs up to {t.Rpm:F0}");

        input.Throttle = 1.0;
        for (int i = 0; i < 1000; i++) t = sim.Step(0.005, input);
        Assert.InRange(t.Rpm, 7000, 7700);

        input.Ignition = false;
        input.Throttle = 0;
        for (int i = 0; i < 3000; i++) t = sim.Step(0.005, input);
        Assert.False(t.Running);
        Assert.True(t.Rpm < 50);
    }
}
