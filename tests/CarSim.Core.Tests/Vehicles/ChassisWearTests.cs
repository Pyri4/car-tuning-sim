using CarSim.Core.Common;
using CarSim.Core.Damage;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
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

    /// <summary>
    /// A T35 on E85 at 300 kPa: about 475 N·m once spooled (6500 rpm), more than the stock gearbox is rated for (and the OEM
    /// crankshaft, which is why this build has a forged one and race bearings).
    /// </summary>
    private static VehicleSimulation BigTurboCar(params (string slot, string part)[] chassisSwaps) =>
        Car.Create(BigTurboEngine(), BigTurboTune.Value.Clone(), "e85", chassisSwaps);

    private static EngineAssembly BigTurboEngine()
    {
        var db = TestContent.Database;
        var a = SimFactory.Assembly(("crankshaft", "k20.crankshaft.forged"), ("main_bearings", "k20.main_bearings.race"), ("rod_bearings", "k20.rod_bearings.race"),
            ("pistons", "k20.pistons.forged_lc"), ("connecting_rods", "k20.rods.forged_h"), ("head_gasket", "k20.head_gasket.race"),
            ("injectors", "injectors.1000cc"), ("fuel_pump", "fuel_pump.hf_330"), ("ecu", "ecu.standalone"), ("exhaust", "exhaust.race_76mm"),
            ("exhaust_manifold", "k20.exhaust_manifold.turbo_t3_tubular"));
        var f = new CarSim.Core.Parts.PartInstanceFactory(6_000_000);
        Assert.True(a.Install("turbocharger", f.Create(db.GetPart("turbo.t35_big"))).Ok);
        Assert.True(a.Install("intercooler", f.Create(db.GetPart("intercooler.fmic_race"))).Ok);
        return a;
    }

    /// <summary>
    /// The turbo base tune taken to 300 kPa on E85, its map extended past the boost and its VE table
    /// calibrated on this build (on the T28's table the T35 runs lean at 300 kPa and melts a piston).
    /// </summary>
    private static readonly Lazy<EcuTune> BigTurboTune = new(() =>
    {
        var tune = SimFactory.ExtendLoadAxis(EcuTune.FromDocument(TestContent.Database.GetTune("k20.turbo_base")), 300, 350);
        for (int c = 0; c < tune.BoostTarget!.Columns; c++) if (tune.BoostTarget[0, c] >= 170) tune.BoostTarget[0, c] = 300;
        tune.InjectorFlowCcMin = 1000;
        tune.FuelStoichAfr = TestContent.Database.GetFuel("e85").StoichiometricAfr;
        return SimFactory.WithCalibratedVe(BigTurboEngine(), "e85", tune);
    });

    /// <summary>Full-throttle pulls in fourth from 110 km/h to the limiter until something in the driveline breaks.</summary>
    private static int PullsUntilBroken(VehicleSimulation sim, int maxPulls)
    {
        for (int pull = 1; pull <= maxPulls; pull++)
        {
            Car.Rolling(sim, 110, 4);
            var input = new VehicleInputs { Throttle = 1 };
            for (int i = 0; i < 8.0 / Car.Dt; i++)
                if (sim.Step(Car.Dt, input).EngineRpm > 7300) break;
            if (sim.Wear.DriveBroken) return pull;
        }
        return int.MaxValue;
    }

    [Fact]
    public void TorqueAboveTheGearboxRatingBreaksItAndTheDogBoxSurvives()
    {
        var stockBox = BigTurboCar(("clutch", "clutch.race_twin"));
        int pulls = PullsUntilBroken(stockBox, 30);
        Assert.InRange(pulls, 2, 30);
        Assert.Empty(stockBox.Engine.Damage.Failures);
        var report = Assert.Single(stockBox.Wear.Failures);
        Assert.Equal(FailureMode.GearboxOverload, report.Mode);
        Assert.Contains(report.ContributingFactors, f => f.Contains("close to or above what the gearbox is built for"));
        // No drive: full throttle goes nowhere.
        double speed = stockBox.Last!.Speed;
        for (int i = 0; i < 1.0 / Car.Dt; i++) stockBox.Step(Car.Dt, new VehicleInputs { Throttle = 1 });
        Assert.True(stockBox.Last!.Speed <= speed + 0.1);

        var dogBox = BigTurboCar(("clutch", "clutch.race_twin"), ("gearbox", "gearbox.close_ratio_6mt"));
        Assert.Equal(int.MaxValue, PullsUntilBroken(dogBox, pulls + 2));
    }

    [Fact]
    public void WheelspinProtectsTheDrivelineOnAClutchDump()
    {
        // Rev it in neutral and drop it into first: on street tyres the wheels spin before the gearbox sees much.
        var sim = TurboCar(("clutch", "clutch.race_twin"));
        var input = new VehicleInputs { Throttle = 1 };
        for (int i = 0; i < 1.0 / Car.Dt; i++) sim.Step(Car.Dt, input);
        input.ShiftUp = true;
        sim.Step(Car.Dt, input);
        input.ShiftUp = false;
        double peak = 0;
        for (int i = 0; i < 2.0 / Car.Dt; i++) { sim.Step(Car.Dt, input); peak = Math.Max(peak, sim.Wear.GearboxLoadNm); }
        Assert.Empty(sim.Wear.Failures);
        Assert.True(peak < sim.Config.Gearbox.MaxTorqueNm, $"peak {peak:F0} N·m");
        Assert.True(sim.Last!.SpeedKmh > 20);
    }

    [Fact]
    public void HardShiftingThroughTheGearsDoesNotHurtTheGearbox()
    {
        var sim = TurboCar(("clutch", "clutch.race_twin"));
        Assert.True(Car.Accelerate(sim, 170) < 30);
        var gearbox = sim.Config.Chassis.PartIn("gearbox")!;
        Assert.False(gearbox.IsFailed);
        Assert.True(gearbox.Damage.MaxFatigue < 0.01, $"fatigue {gearbox.Damage.MaxFatigue:F4}");
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
