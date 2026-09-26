using CarSim.Core.Damage;
using CarSim.Core.Dyno;
using CarSim.Gameplay;

namespace CarSim.Core.Tests.Gameplay;

public class GarageTests
{
    private static Garage NewGame() => Garage.NewGame(TestContent.Database, "project_car");

    [Fact]
    public void ScenarioAppliesWearAndFatigue()
    {
        var g = NewGame();
        Assert.Equal(9000, g.Money);
        Assert.True(g.EngineInCar);
        Assert.Equal(0.55, g.Engine.PartIn("main_bearings")!.Wear, 9);
        Assert.Equal(0.30, g.Engine.PartIn("pistons")!.Damage.FatigueOf(FailureMode.Detonation), 9);
        Assert.Contains(PartInspector.Inspect(g.Engine.PartIn("pistons")!), f => f.Text.Contains("detonation"));
    }

    [Fact]
    public void ProjectCarRunsButShowsItsProblems()
    {
        var g = NewGame();
        var (sim, report) = g.CreateSimulation();
        Assert.True(report.CanRun, string.Join("\n", report.Issues));
        var healthy = SimFactory.Create();
        double idleWorn = sim!.OilPressure(850, 373.15, 1.0);
        double idleHealthy = healthy.OilPressure(850, 373.15, 1.0);
        Assert.True(idleWorn < 0.6 * idleHealthy, "worn bearings: the oil light flickers at idle");
        var worn = new DynoRunner(sim, new DynoSettings()).RunToCompletion();
        var fresh = new DynoRunner(healthy, new DynoSettings()).RunToCompletion();
        Assert.True(worn.PeakPower!.Power < fresh.PeakPower!.Power, "worn rings and clogged injectors cost power");
    }

    [Fact]
    public void BottomEndIsOutOfReachWithTheEngineInTheCar()
    {
        var g = NewGame();
        Assert.True(g.RemovePart("ecu").Ok, "the ECU is accessible in the car");
        var r = g.RemovePart("flywheel");
        Assert.False(r.Ok);
        Assert.Contains("Remove the engine first", r.Message);
        Assert.True(g.RemoveEngineFromCar().Ok);
        Assert.True(g.RemovePart("flywheel").Ok);
    }

    [Fact]
    public void RemovedPartsGoToTheShelfAndCanBeReinstalled()
    {
        var g = NewGame();
        Assert.True(g.RemovePart("exhaust").Ok);
        var exhaust = Assert.Single(g.Inventory);
        Assert.False(g.InstallEngineInCar().Ok);
        Assert.True(g.InstallPart(exhaust, "exhaust").Ok);
        Assert.Empty(g.Inventory);
    }

    [Fact]
    public void BuyingSpendsMoneyAndSellingReturnsSomeOfIt()
    {
        var g = NewGame();
        Assert.True(g.Buy("exhaust.sport_63mm").Ok);
        Assert.Equal(9000 - 850, g.Money);
        var part = Assert.Single(g.Inventory);
        Assert.True(g.Sell(part).Ok);
        Assert.Equal(9000 - 850 + Math.Round(850 * Garage.ResaleFraction), g.Money);
        Assert.False(g.Buy("k20.block.sleeved").Ok && g.Buy("k20.block.sleeved").Ok && g.Buy("k20.block.sleeved").Ok, "cannot overspend");
        Assert.True(g.Money >= 0);
    }

    [Fact]
    public void WornAndFailedPartsSellForLess()
    {
        var g = NewGame();
        g.RemovePart("exhaust");
        var worn = g.Inventory[0];
        Assert.True(Garage.SellPrice(worn) < worn.Definition.Price * Garage.ResaleFraction);
        worn.Damage.Fail(FailureMode.HeadGasketBreach, 0);
        Assert.Equal(Math.Round(worn.Definition.Price * Garage.ScrapFraction), Garage.SellPrice(worn));
    }

    [Fact]
    public void SaveAndLoadRoundTripsEverything()
    {
        var g = NewGame();
        g.RemoveEngineFromCar();
        g.RemovePart("exhaust");
        g.Buy("injectors.550cc");
        g.Tune.InjectorFlowCcMin = 550;
        g.SetFuel("gasoline_98");
        g.Engine.PartIn("crankshaft")!.Damage.Fail(FailureMode.CrankshaftOverspeed, 12.5);

        string json = SaveSystem.Serialize(g);
        var loaded = SaveSystem.Deserialize(json, TestContent.Database);

        Assert.Equal(g.Money, loaded.Money);
        Assert.False(loaded.EngineInCar);
        Assert.Equal("gasoline_98", loaded.FuelId);
        Assert.Equal(550, loaded.Tune.InjectorFlowCcMin);
        Assert.Equal(g.Inventory.Select(p => p.InstanceId).OrderBy(x => x), loaded.Inventory.Select(p => p.InstanceId).OrderBy(x => x));
        Assert.Equal(g.Engine.Installed.Keys.OrderBy(k => k), loaded.Engine.Installed.Keys.OrderBy(k => k));
        Assert.Equal(g.Chassis!.Installed.Keys.OrderBy(k => k), loaded.Chassis!.Installed.Keys.OrderBy(k => k));
        Assert.Equal(0.50, loaded.Chassis.PartIn("tires_rear")!.Wear, 9);
        Assert.Equal(0.55, loaded.Engine.PartIn("main_bearings")!.Wear, 9);
        Assert.Equal(0.30, loaded.Engine.PartIn("pistons")!.Damage.FatigueOf(FailureMode.Detonation), 9);
        Assert.Equal(FailureMode.CrankshaftOverspeed, loaded.Engine.PartIn("crankshaft")!.Damage.Failure!.Mode);
        // New parts after loading never reuse an id.
        loaded.Buy("throttle.70mm");
        var ids = loaded.Engine.AllParts.Concat(loaded.Inventory).Select(p => p.InstanceId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(json, SaveSystem.Serialize(SaveSystem.Deserialize(json, TestContent.Database)));
    }

    [Fact]
    public void ProjectCarCanBeDrivenOnlyWithTheEngineInIt()
    {
        var g = NewGame();
        Assert.NotNull(g.Chassis);
        Assert.Equal(0.40, g.Chassis!.PartIn("clutch")!.Wear, 9);
        var (sim, problem) = g.CreateVehicleSimulation();
        Assert.NotNull(sim);
        Assert.Equal("", problem);
        g.RemoveEngineFromCar();
        var (none, why) = g.CreateVehicleSimulation();
        Assert.Null(none);
        Assert.Contains("stand", why);
    }

    [Fact]
    public void ChassisPartsSwapThroughTheShelf()
    {
        var g = NewGame();
        Assert.True(g.CanAccess("differential"), "chassis parts are reachable with the engine in the car");
        Assert.True(g.RemovePart("differential").Ok);
        Assert.False(g.CreateVehicleSimulation().Sim != null, "no differential, no driving");
        Assert.True(g.Buy("differential.lsd_410").Ok);
        var lsd = g.Inventory.Single(p => p.Definition.Id == "differential.lsd_410");
        Assert.False(g.CanInstall("gearbox", lsd).Ok, "a differential does not go in the gearbox slot");
        Assert.True(g.CanInstall("differential", lsd).Ok);
        Assert.Contains(g.SlotsFor(lsd), s => s.Id == "differential");
        Assert.True(g.InstallPart(lsd, "differential").Ok);
        Assert.False(g.CanInstall("differential", lsd).Ok, "slot is now occupied");
        Assert.Equal("differential.lsd_410", g.PartIn("differential")!.Definition.Id);
        Assert.NotNull(g.CreateVehicleSimulation().Sim);
    }

    [Fact]
    public void ABrokenChassisPartKeepsTheCarOffTheTrackUntilReplaced()
    {
        var g = NewGame();
        g.PartIn("clutch")!.Damage.Fail(CarSim.Core.Damage.FailureMode.ClutchBurnout, 0);
        var (sim, problem) = g.CreateVehicleSimulation();
        Assert.Null(sim);
        Assert.Contains("OEM clutch", problem);
        Assert.True(g.RemovePart("clutch").Ok);
        Assert.True(g.Buy("clutch.sport").Ok);
        Assert.True(g.InstallPart(g.Inventory.Single(p => p.Definition.Id == "clutch.sport"), "clutch").Ok);
        Assert.NotNull(g.CreateVehicleSimulation().Sim);
    }

    [Fact]
    public void DamageFromDrivingPersistsInTheGarage()
    {
        var g = NewGame();
        var (sim, _) = g.CreateVehicleSimulation();
        sim!.State.Gear = 2;
        sim.State.U = 45;
        for (int w = 0; w < 4; w++) sim.State.WheelOmega[w] = sim.State.U / sim.Config.TireOf(w).Radius;
        var input = new CarSim.Core.Vehicles.VehicleInputs();
        for (int i = 0; i < 2000; i++) sim.Step(0.002, input);
        Assert.True(sim.Engine.Damage.Seized, "grabbing 2nd at 160 km/h over-revs the engine");
        Assert.NotEmpty(g.FailedParts);
        Assert.Null(g.CreateVehicleSimulation().Sim);
        var loaded = SaveSystem.Deserialize(SaveSystem.Serialize(g), TestContent.Database);
        Assert.NotEmpty(loaded.FailedParts);
    }

    [Fact]
    public void SaveReferencingMissingContentGivesAClearError()
    {
        string json = SaveSystem.Serialize(NewGame()).Replace("\"k20.head.oem\"", "\"modpack.head.missing\"");
        var ex = Assert.Throws<InvalidDataException>(() => SaveSystem.Deserialize(json, TestContent.Database));
        Assert.Contains("modpack.head.missing", ex.Message);
    }
}
