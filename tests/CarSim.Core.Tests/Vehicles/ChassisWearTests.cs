using CarSim.Core.Common;
using CarSim.Core.Damage;
using CarSim.Core.Ecu;
using CarSim.Core.Tests.Simulation;
using CarSim.Core.Vehicles;

namespace CarSim.Core.Tests.Vehicles;

public class ChassisWearTests
{
    private static VehicleSimulation TurboCar(params (string slot, string part)[] chassisSwaps) =>
        Car.Create(TurboTests.TurboBuild(), EcuTune.FromDocument(TestContent.Database.GetTune("k20.turbo_base")), "gasoline_98", chassisSwaps);

    /// <summary>Full throttle in fourth from <paramref name="kmh"/> for <paramref name="seconds"/>; returns the largest clutch slip seen with the clutch engaged.</summary>
    private static double PullInFourth(VehicleSimulation sim, double kmh = 90, double seconds = 8)
    {
        Car.Rolling(sim, kmh, 4);
        sim.Engine.State.Running = true;
        var input = new VehicleInputs { Throttle = 1 };
        double maxSlip = 0;
        for (int i = 0; i < seconds / Car.Dt; i++)
        {
            var t = sim.Step(Car.Dt, input);
            if (t.Clutch > 0.999) maxSlip = Math.Max(maxSlip, t.ClutchSlipRpm);
        }
        return maxSlip;
    }

    [Fact]
    public void ClutchWearLowersItsCapacity()
    {
        var sim = Car.Chassis();
        double rated = sim.Config.Clutch.MaxTorqueNm;
        Assert.Equal(rated, sim.Wear.ClutchCapacityNm, 6);
        sim.Config.Chassis.PartIn("clutch")!.Wear = 0.4;
        Assert.Equal(rated * (1 - 0.4 * ChassisWearModel.ClutchWearCapacityLoss), sim.Wear.ClutchCapacityNm, 6);
        sim.Config.Chassis.PartIn("clutch")!.Damage.Fail(FailureMode.ClutchBurnout, 0);
        Assert.Equal(rated * ChassisWearModel.BurntClutchCapacityFactor, sim.Wear.ClutchCapacityNm, 6);
    }

    [Fact]
    public void StockTorqueDoesNotSlipTheProjectCarsWornClutch()
    {
        var sim = Car.Chassis();
        sim.Config.Chassis.PartIn("clutch")!.Wear = 0.4;
        Assert.True(PullInFourth(sim) < 50, "189 N·m through a clutch still good for 224 N·m");
        Assert.DoesNotContain(sim.Wear.Warnings, w => w.Code == "clutch_slip");
    }

    [Fact]
    public void TurboTorqueSlipsTheWornOemClutchButNotASportClutch()
    {
        var worn = TurboCar();
        worn.Config.Chassis.PartIn("clutch")!.Wear = 0.4;
        double startWear = 0.4;
        double slip = PullInFourth(worn);
        Assert.True(slip > 200, $"slip {slip:F0} rpm");
        Assert.True(worn.Wear.ClutchTemperatureC > 60, $"clutch {worn.Wear.ClutchTemperatureC:F0} °C");
        Assert.True(worn.Config.Chassis.PartIn("clutch")!.Wear > startWear, "slipping wears the facings");

        var sport = TurboCar(("clutch", "clutch.sport"));
        Assert.True(PullInFourth(sport) < 50);
    }

    [Fact]
    public void AnOverheatedSlippingClutchBurnsOutWithAnExplanation()
    {
        var sim = TurboCar();
        var clutch = sim.Config.Chassis.PartIn("clutch")!;
        clutch.Wear = 0.4;
        FailureReport? report = null;
        for (int run = 0; run < 12 && report == null; run++)
        {
            PullInFourth(sim, 80, 6);
            report = sim.Wear.Failures.FirstOrDefault();
        }
        Assert.NotNull(report);
        Assert.Equal(FailureMode.ClutchBurnout, report!.Mode);
        Assert.True(clutch.IsFailed);
        Assert.Contains(report.ContributingFactors, f => f.Contains("40 % worn"));
        Assert.Contains(report.ContributingFactors, f => f.Contains("heated the facings"));
        Assert.Contains(report.Recommendations, r => r.Contains("sport or twin-plate"));
        // A burnt clutch barely transmits anything.
        Assert.True(sim.Wear.ClutchCapacityNm < 30);
    }

    [Fact]
    public void HardStopsHeatTheBrakesAndHotBrakesFade()
    {
        var sim = Car.Chassis();
        double before = sim.Wear.BrakeTemperature[0];
        // Below lock-up: locked wheels put the energy into the tyres instead of the brakes.
        Car.BrakingDistance(sim, 150, 0.55);
        double rise = sim.Wear.BrakeTemperature[0] - before;
        Assert.InRange(rise, 60, 200);
        Assert.True(sim.Wear.BrakeTemperature[0] > sim.Wear.BrakeTemperature[1], "the fronts do most of the work");
        Assert.True(sim.Config.Chassis.PartIn("brakes")!.Wear > 0);

        var cool = Car.Chassis();
        double coolDistance = Car.BrakingDistance(cool, 100, 0.6);
        var hot = Car.Chassis();
        hot.Wear.BrakeTemperature[0] = hot.Wear.BrakeTemperature[1] = Units.CToK(700);
        double hotDistance = Car.BrakingDistance(hot, 100, 0.6);
        Assert.True(hotDistance > coolDistance * 1.3, $"cool {coolDistance:F1} m, faded {hotDistance:F1} m");
        Assert.Contains(hot.Wear.Warnings, w => w.Code == "brake_fade");
    }

    [Fact]
    public void BigBrakesFadeLater()
    {
        var oem = Car.Chassis();
        var big = Car.Chassis(("brakes", "brakes.big_kit"));
        oem.Wear.BrakeTemperature[0] = big.Wear.BrakeTemperature[0] = Units.CToK(600);
        Assert.True(big.Wear.BrakeFactor(0) > oem.Wear.BrakeFactor(0));
        // More thermal mass: the same stop heats them less.
        var a = Car.Chassis();
        var b = Car.Chassis(("brakes", "brakes.big_kit"));
        Car.BrakingDistance(a, 150, 0.6);
        Car.BrakingDistance(b, 150, 0.6);
        Assert.True(b.Wear.BrakeTemperature[0] < a.Wear.BrakeTemperature[0]);
    }

    [Fact]
    public void WornTyresGripLess()
    {
        double fresh = Car.MaxLateralG(Car.Chassis());
        var worn = Car.Chassis();
        worn.Config.Chassis.PartIn("tires_front")!.Wear = 0.9;
        worn.Config.Chassis.PartIn("tires_rear")!.Wear = 0.9;
        double wornG = Car.MaxLateralG(worn);
        Assert.InRange(wornG / fresh, 0.85, 0.95);
    }

    [Fact]
    public void WheelspinWearsTheDrivenTyres()
    {
        var sim = Car.Chassis();
        Car.Accelerate(sim, 60);
        double front = sim.Config.Chassis.PartIn("tires_front")!.Wear;
        double rear = sim.Config.Chassis.PartIn("tires_rear")!.Wear;
        Assert.True(rear > front, $"rear {rear:E2}, front {front:E2}");
        Assert.True(rear > 0);
    }

    [Fact]
    public void WearCanBeSwitchedOffForIsolatedTests()
    {
        var sim = Car.Chassis();
        sim.Wear.Enabled = false;
        sim.Config.Chassis.PartIn("tires_rear")!.Wear = 1.0;
        Assert.Equal(1.0, sim.Wear.GripFactor(Wheel.RL));
        Car.Accelerate(sim, 60);
        Assert.Equal(1.0, sim.Config.Chassis.PartIn("tires_rear")!.Wear);
    }
}
