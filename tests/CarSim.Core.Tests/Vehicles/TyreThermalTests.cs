using CarSim.Core.Common;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Vehicles;

namespace CarSim.Core.Tests.Vehicles;

public class TyreThermalTests
{
    private static readonly (string, string)[] Semis = { ("tires_front", "tires.semislick_235_40r17"), ("tires_rear", "tires.semislick_235_40r17") };

    /// <summary>Weaves at 100 km/h for <paramref name="seconds"/> (sliding the tyres a little, as on track).</summary>
    private static void Weave(VehicleSimulation sim, double seconds)
    {
        Car.Rolling(sim, 100, 4);
        for (int i = 0; i < seconds / Car.Dt; i++)
        {
            double time = i * Car.Dt;
            var input = new VehicleInputs { Steer = 0.25 * Math.Sin(time * 1.5), Throttle = Math.Clamp(0.4 + (100 / 3.6 - sim.State.U) * 0.3, 0, 1) };
            sim.Step(Car.Dt, input);
        }
    }

    [Fact]
    public void ColdTyresWarmUpWhenWorkedAndCoolDownWhenNot()
    {
        var sim = Car.Chassis();
        sim.Tyres.SetAll(TyreThermalModel.AmbientK);
        Weave(sim, 60);
        double worked = sim.Tyres.TemperatureK.Average();
        Assert.True(Units.KToC(worked) > 40, $"{Units.KToC(worked):F0} °C after a minute of hard driving");
        // Parked (put at rest: a locked-wheel stop would heat them by sliding): slowly back towards ambient.
        Car.Rolling(sim, 0, 0);
        for (int i = 0; i < 120 / Car.Dt; i++) sim.Step(Car.Dt, new VehicleInputs { Brake = 1 });
        Assert.True(sim.Tyres.TemperatureK.Average() < worked - 3, $"{Units.KToC(worked):F1} → {Units.KToC(sim.Tyres.TemperatureK.Average()):F1} °C");
    }

    [Fact]
    public void PressureRisesWithTemperature()
    {
        var tyre = (TireSpec)TestContent.Database.GetPart("tires.street_225_50r16").Spec;
        Assert.Equal(tyre.ColdPressureKpa, TireModel.HotPressureKpa(tyre, TireModel.ColdReferenceK), 9);
        double hot = TireModel.OperatingPressureKpa(tyre);
        Assert.InRange(hot, 210, 230);
        // Ideal gas: absolute pressure proportional to absolute temperature.
        Assert.Equal((tyre.ColdPressureKpa + TireModel.AtmosphereKpa) * Units.CToK(tyre.OptimalTemperatureC) / TireModel.ColdReferenceK,
            hot + TireModel.AtmosphereKpa, 6);
    }

    [Fact]
    public void ColdSemiSlicksGripFarLessThanWarmOnes()
    {
        var warm = Car.Chassis(Semis);
        var cold = Car.Chassis(Semis);
        cold.Tyres.SetAll(TyreThermalModel.AmbientK);
        double gWarm = Car.MaxLateralG(warm), gCold = Car.MaxLateralG(cold);
        Assert.True(gCold < gWarm * 0.9, $"warm {gWarm:F3} g, cold {gCold:F3} g");
        // Street tyres have a wide window: cold costs them far less.
        var street = Car.Chassis();
        street.Tyres.SetAll(TyreThermalModel.AmbientK);
        Assert.True(Car.MaxLateralG(street) > Car.MaxLateralG(Car.Chassis()) * 0.9);
    }

    [Fact]
    public void OverheatedTyresLoseGrip()
    {
        var semi = (TireSpec)TestContent.Database.GetPart("tires.semislick_235_40r17").Spec;
        double optimal = TireModel.ThermalGripFactor(semi, Units.CToK(semi.OptimalTemperatureC));
        Assert.Equal(1.0, optimal, 9);
        Assert.True(TireModel.ThermalGripFactor(semi, Units.CToK(150)) < 0.8);
    }

    [Fact]
    public void AnUnderinflatedTyreRunsHotter()
    {
        var stock = Car.Chassis();
        var soft = Car.Chassis();
        foreach (var slot in new[] { "tires_front", "tires_rear" }) soft.Config.Chassis.PartIn(slot)!.Adjust("cold_pressure_kpa", 125);
        soft = new VehicleSimulation(new VehicleConfiguration(soft.Config.Definition, soft.Config.Chassis, soft.Engine.Config.Assembly, TestContent.Database), soft.Engine);
        // Straight-line cruising isolates the flexing (rolling hysteresis) heat, which grows as the pressure drops.
        foreach (var sim in new[] { stock, soft })
        {
            sim.Tyres.SetAll(TyreThermalModel.AmbientK);
            Car.Rolling(sim, 130, 5);
            for (int i = 0; i < 60 / Car.Dt; i++)
                sim.Step(Car.Dt, new VehicleInputs { Throttle = Math.Clamp(0.4 + (130 / 3.6 - sim.State.U) * 0.3, 0, 1) });
        }
        Assert.True(soft.Tyres.TemperatureK.Average() > stock.Tyres.TemperatureK.Average() + 1,
            $"soft {Units.KToC(soft.Tyres.TemperatureK.Average()):F1} °C, stock {Units.KToC(stock.Tyres.TemperatureK.Average()):F1} °C");
    }
}
