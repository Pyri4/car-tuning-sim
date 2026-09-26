using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;
using CarSim.Core.Tests.Vehicles;
using CarSim.Core.Vehicles;

namespace CarSim.Core.Tests.Simulation;

/// <summary>
/// Guards that are not physics (floors and caps that keep a fitted law in range) must not be what normal running
/// depends on. Physical limits — a wheel cannot pull the road, a clutch slips at its capacity, an orifice chokes, a
/// relief valve opens — are not in this list; they are the model. See SIMULATION_SPEC.md, "Clamps".
/// </summary>
public class ClampActivationTests
{
    public static IEnumerable<object[]> Builds() => new[]
    {
        new object[] { "stock" }, new object[] { "built NA" }, new object[] { "T28" }, new object[] { "T25 at 240 kPa" },
    };

    private static EngineSimulation Build(string build) => build switch
    {
        "built NA" => SimFactory.Create(("valve_springs", "k20.valve_springs.performance"), ("camshafts", "k20.cams.race"),
            ("cylinder_head", "k20.head.ported"), ("exhaust_manifold", "k20.exhaust_manifold.header421")),
        "T28" => TurboTests.TurboSim(TurboTests.TurboBuild()),
        "T25 at 240 kPa" => SimFactory.Create(TurboTests.TurboBuild("turbo.t25_small"), "race_110", SimFactory.ExtendLoadAxis(TurboTests.TuneWithBoost(240), 300, 350)),
        _ => SimFactory.Create(),
    };

    [Theory]
    [MemberData(nameof(Builds))]
    public void EngineGuardsNeverActInNormalRunning(string build)
    {
        foreach (double rpm in new[] { 800.0, 1500, 3000, 5000, 7000 })
            foreach (double throttle in new[] { 0.0, 0.3, 1.0 })
            {
                var sim = Build(build);
                sim.DamageEnabled = false;
                var t = SimFactory.At(sim, rpm, throttle, build.StartsWith('T') ? 2.0 : 0.6);
                string at = $"{build} {rpm} rpm throttle {throttle}";
                Assert.False(t.WallHeatLimited, $"{at}: wall heat scaled");
                Assert.False(t.VeFloorActive, $"{at}: VE shape on its floor");
                Assert.True(t.ChargeTemperature > 250, $"{at}: charge {t.ChargeTemperature:F0} K (floor 200 K)");
                if (!t.Firing) continue;
                // Spark-factor floor (10 % torque) needs 55° from MBT; the peak-pressure timing clamp needs 28° retard
                // or 40° over-advance; the knock torque floor needs 50° of knock.
                Assert.True(Math.Abs(t.IgnitionAdvance - t.MbtAdvance) < 25, $"{at}: advance {t.IgnitionAdvance:F1}° vs MBT {t.MbtAdvance:F1}°");
                Assert.True(t.KnockIntensity < 10, at);
            }
    }

    [Fact]
    public void TheVeFloorLiesOutsideEveryShippedCamsRevRange()
    {
        var db = TestContent.Database;
        var fuel = db.GetFuel("gasoline_95");
        int checkedCombos = 0;
        foreach (var cams in db.Parts.Values.Where(p => p.Category == PartCategory.Camshafts))
            foreach (var intake in db.Parts.Values.Where(p => p.Category == PartCategory.IntakeManifold))
            {
                var a = SimFactory.Assembly(("valve_springs", "k20.valve_springs.performance"));
                var factory = new PartInstanceFactory(40_000_000);
                foreach (var (slot, part) in new[] { ("camshafts", cams), ("intake_manifold", intake) })
                {
                    var removed = new Stack<(string, PartInstance)>();
                    foreach (var s in a.RemovalSequenceFor(slot)) { a.Remove(s, out var p); removed.Push((s, p!)); }
                    a.Remove(slot, out _);
                    Assert.True(a.Install(slot, factory.Create(part)).Ok);
                    while (removed.Count > 0) { var (s, p) = removed.Pop(); a.Install(s, p); }
                }
                var result = EngineConfiguration.Build(a, fuel);
                if (!result.Success) continue;
                var path = new AirPath(result.Configuration!.Banks[0], result.Configuration!.Geometry);
                for (double rpm = 600; rpm <= 9000; rpm += 100)
                    Assert.True(path.VeShape(rpm) > AirPath.VeShapeFloor, $"{cams.Id} + {intake.Id} at {rpm} rpm: shape {path.VeShape(rpm):F3}");
                checkedCombos++;
            }
        Assert.True(checkedCombos >= 4);
    }

    [Fact]
    public void TheTyreFrictionCapCarriesAtMostAFewPercentOfTheForceOnAGrippyBuild()
    {
        // The worst case in the shipped content: semi-slicks on track coilovers, lapping at the autopilot's pace. Only
        // the unloaded inside front in fast corners reaches the light-load cap.
        var sim = Car.Chassis(("tires_front", "tires.semislick_235_40r17"), ("tires_rear", "tires.semislick_235_40r17"), ("suspension", "suspension.coilover_track"));
        var track = TrackLayout.TestFacility();
        sim.Track = track;
        var driver = TrackDriver.ForCar(track, sim.Config);
        var timer = new LapTimer(track);
        VehicleTelemetry? t = null;
        double capped = 0, total = 0;
        for (int i = 0; i < 200_000 && timer.Laps < 1; i++)
        {
            t = sim.Step(Car.Dt, driver.Drive(sim, t));
            timer.Update(driver.Index, t.Time);
            for (int w = 0; w < 4; w++)
            {
                double force = t.WheelLoad[w] * t.TyreUsage[w];
                total += force;
                if (TireModel.FrictionCapped(sim.Config.TireOf(w), t.WheelLoad[w])) capped += force;
            }
        }
        Assert.Equal(1, timer.Laps);
        Assert.True(capped / total < 0.02, $"capped wheels carried {capped / total:P2} of the tyre force");
    }

    [Fact]
    public void ChassisGuardsNeverActOnShippedParts()
    {
        var db = TestContent.Database;
        var def = db.GetVehicle("kestrel_s2");
        var engines = new[] { SimFactory.Assembly(), TurboTests.TurboBuild("turbo.t35_big") };
        foreach (var engine in engines)
            foreach (var slot in def.Slots)
                foreach (var part in db.Parts.Values.Where(p => p.Category == slot.Category))
                {
                    var factory = new PartInstanceFactory(41_000_000);
                    var chassis = VehicleAssembly.CreateStock(def, db, factory);
                    chassis.Remove(slot.Id, out _);
                    var instance = factory.Create(part);
                    // Lowest ride height the part allows.
                    if (part.FindAdjustment("ride_height_offset_mm") is { } adj) instance.Adjust(adj.Field, adj.Min);
                    if (!chassis.Install(slot.Id, instance).Ok) continue;
                    var config = new VehicleConfiguration(def, chassis, engine, db);
                    Assert.False(config.WeightDistributionClamped, $"{part.Id}: front share");
                    Assert.False(config.CgHeightFloored, $"{part.Id}: CG height");
                }
    }
}
