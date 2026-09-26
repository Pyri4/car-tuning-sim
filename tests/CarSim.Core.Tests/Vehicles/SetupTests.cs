using CarSim.Core.Content;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Vehicles;
using CarSim.Gameplay;

namespace CarSim.Core.Tests.Vehicles;

public class SetupTests
{
    private static PartInstance Part(VehicleSimulation sim, string slot) => sim.Config.Chassis.PartIn(slot)!;

    /// <summary>A fresh car whose chassis parts are adjusted before the simulation is built (as in the garage).</summary>
    private static VehicleSimulation CarWith((string slot, string part)[] swaps, params (string slot, string field, double value)[] settings)
    {
        var probe = Car.Chassis(swaps);
        foreach (var (slot, field, value) in settings) Assert.True(Part(probe, slot).Adjust(field, value), $"{slot}.{field}");
        // Rebuild the configuration from the adjusted parts.
        var sim = new VehicleSimulation(new VehicleConfiguration(probe.Config.Definition, probe.Config.Chassis, probe.Engine.Config.Assembly, TestContent.Database), probe.Engine);
        sim.StartIdling();
        return sim;
    }

    // ---- Adjustments as data ------------------------------------------------------------------

    [Fact]
    public void AdjustmentsClampSnapAndOnlyAffectTheirInstance()
    {
        var db = TestContent.Database;
        var def = db.GetPart("tires.street_225_50r16");
        var adj = def.FindAdjustment("cold_pressure_kpa")!;
        Assert.Equal(175, adj.Default);
        var a = new PartInstance("a", def);
        var b = new PartInstance("b", def);
        Assert.True(a.Adjust("cold_pressure_kpa", 187.4));
        Assert.Equal(185, a.SettingOf("cold_pressure_kpa"));
        Assert.Equal(185, a.Spec<TireSpec>().ColdPressureKpa);
        Assert.Equal(175, b.Spec<TireSpec>().ColdPressureKpa);
        Assert.Equal(175, ((TireSpec)def.Spec).ColdPressureKpa);
        a.Adjust("cold_pressure_kpa", 999);
        Assert.Equal(adj.Max, a.SettingOf("cold_pressure_kpa"));
        a.Adjust("cold_pressure_kpa", adj.Default);
        Assert.Empty(a.Settings);
        Assert.Same(def.Spec, a.EffectiveSpec);
        Assert.False(a.Adjust("width_mm", 255), "only declared fields are adjustable");
    }

    private const string Clutch = """
        { "id": "c", "name": "C", "category": "clutch", "price": 1, "mass_kg": 1, "spec": { "max_torque_nm": 300 }, ADJ }
        """;

    private static ContentLoadResult LoadClutch(string adjustable) =>
        ContentLoader.LoadFromStrings(new[] { ("t.json", "{ \"parts\": [" + Clutch.Replace("ADJ", adjustable) + "] }") });

    [Theory]
    [InlineData("""{ "max_torque": { "min": 1, "max": 2, "step": 1 } }""", "not a numeric spec field")]
    [InlineData("""{ "max_torque_nm": { "min": 400, "max": 500, "step": 10 } }""", "outside")]
    [InlineData("""{ "max_torque_nm": { "min": 200, "max": 400, "step": 0 } }""", "step")]
    [InlineData("""{ "max_torque_nm": { "min": 20, "max": 400, "step": 10 } }""", "max_torque_nm must be within")]
    public void BadAdjustmentsAreReported(string adjustable, string message)
    {
        var r = LoadClutch("\"adjustable\": " + adjustable);
        Assert.Contains(r.Errors, e => e.ItemId == "c" && e.Message.Contains(message));
    }

    [Fact]
    public void AGoodAdjustmentLoads()
    {
        var r = LoadClutch("""
            "adjustable": { "max_torque_nm": { "min": 200, "max": 400, "step": 10, "label": "Clamp" } }
            """);
        Assert.True(r.Success, string.Join("\n", r.Errors));
        var adj = Assert.Single(r.Database.GetPart("c").Adjustments);
        Assert.Equal(("max_torque_nm", "Clamp", 300.0), (adj.Field, adj.Label, adj.Default));
    }

    [Fact]
    public void SetupSurvivesSaveAndLoad()
    {
        var g = Garage.NewGame(TestContent.Database, "project_car");
        Assert.True(g.Adjust("suspension", "front_camber_deg", -1.2).Ok);
        Assert.True(g.Adjust("tires_rear", "cold_pressure_kpa", 160).Ok);
        Assert.False(g.Adjust("suspension", "rear_camber_deg", -2).Ok, "OEM rear camber is fixed");
        var loaded = SaveSystem.Deserialize(SaveSystem.Serialize(g), TestContent.Database);
        Assert.Equal(-1.2, loaded.PartIn("suspension")!.Spec<SuspensionSpec>().FrontCamberDeg, 9);
        Assert.Equal(160, loaded.PartIn("tires_rear")!.Spec<TireSpec>().ColdPressureKpa);
        Assert.Contains(loaded.Log, l => l.Contains("Front camber"));
    }

    // ---- Physics of the settings ---------------------------------------------------------------

    [Fact]
    public void TyresGripBestAtTheirOptimalPressure()
    {
        (string, string)[] none = Array.Empty<(string, string)>();
        double G(double kpa) => Car.MaxLateralG(CarWith(none, ("tires_front", "cold_pressure_kpa", kpa), ("tires_rear", "cold_pressure_kpa", kpa)));
        double optimal = G(175), soft = G(125), hard = G(240);
        Assert.True(optimal > soft + 0.01, $"optimal {optimal:F3} g, soft {soft:F3} g");
        Assert.True(optimal > hard + 0.01, $"optimal {optimal:F3} g, hard {hard:F3} g");
    }

    [Fact]
    public void ASofterTyreNeedsMoreSlipAngleAndRollsHarder()
    {
        var tyre = (TireSpec)TestContent.Database.GetPart("tires.street_225_50r16").Spec;
        var soft = (TireSpec)SpecAdjuster.With(tyre, new Dictionary<string, double> { ["cold_pressure_kpa"] = 130 });
        double pSoft = TireModel.OperatingPressureKpa(soft), pStock = TireModel.OperatingPressureKpa(tyre);
        Assert.True(TireModel.PeakSlipAngle(soft, pSoft) > TireModel.PeakSlipAngle(tyre, pStock));
        Assert.True(TireModel.RollingResistance(soft, pSoft) > TireModel.RollingResistance(tyre, pStock));
        Assert.True(ChassisWearModel.TyreWearFactor(soft, -0.5) > ChassisWearModel.TyreWearFactor(tyre, -0.5));
    }

    [Fact]
    public void NegativeCamberAddsCorneringGripButCostsBraking()
    {
        var track = new[] { ("suspension", "suspension.coilover_track") };
        double upright = Car.MaxLateralG(CarWith(track, ("suspension", "front_camber_deg", 0), ("suspension", "rear_camber_deg", 0)));
        double cambered = Car.MaxLateralG(CarWith(track, ("suspension", "front_camber_deg", -3), ("suspension", "rear_camber_deg", -2)));
        Assert.True(cambered > upright * 1.01, $"upright {upright:F3} g, cambered {cambered:F3} g");
        // Straight-line: the contact patch rides on one edge.
        Assert.True(TireModel.CamberLongitudinalFactor(-3) < TireModel.CamberLongitudinalFactor(0));
        // Leaning away from the corner is worse than upright, which is worse than the optimum.
        Assert.True(TireModel.CamberLateralFactor(-2) < TireModel.CamberLateralFactor(0));
        Assert.True(TireModel.CamberLateralFactor(0) < TireModel.CamberLateralFactor(2));
    }

    [Fact]
    public void BodyRollTakesCamberAwayFromTheOutsideWheel()
    {
        var sim = Car.Chassis();
        // Cornering left (positive roll): the right-hand wheels are on the outside.
        double staticLean = sim.WheelLeanLeftDeg(Wheel.FR, 0);
        double rolled = sim.WheelLeanLeftDeg(Wheel.FR, 3);
        Assert.True(staticLean > 0, "negative camber leans the right wheel's top towards the car, i.e. left");
        Assert.True(rolled < staticLean, "roll tips it back towards (and past) upright");
    }

    [Fact]
    public void TheBiasValveMovesLockUpToTheRear()
    {
        var big = new[] { ("brakes", "brakes.big_kit") };
        // Squeeze the pedal slowly from 120 km/h and see which axle locks first.
        string FirstToLock(double factor)
        {
            var sim = CarWith(big, ("brakes", "rear_pressure_factor", factor));
            Car.Rolling(sim, 120, 0);
            for (int i = 0; i < 3.0 / Car.Dt; i++)
            {
                var t = sim.Step(Car.Dt, new VehicleInputs { Brake = i * Car.Dt / 3.0 });
                if (t.Speed < 5) break;
                if (t.WheelSpeed[Wheel.RL] < 0.5) return "rear";
                if (t.WheelSpeed[Wheel.FL] < 0.5) return "front";
            }
            return "none";
        }
        Assert.Equal("rear", FirstToLock(1.8));
        Assert.Equal("front", FirstToLock(0.6));
    }

    [Fact]
    public void AdjustingTheAntiRollBarsMovesTheBalance()
    {
        var track = new[] { ("suspension", "suspension.coilover_track") };
        double soft = CarWith(track, ("suspension", "front_arb_nm_deg", 500)).Config.FrontRollShare;
        double stiff = CarWith(track, ("suspension", "front_arb_nm_deg", 1400)).Config.FrontRollShare;
        Assert.True(stiff > soft + 0.05);
    }
}
