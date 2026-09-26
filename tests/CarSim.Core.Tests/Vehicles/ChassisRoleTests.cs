using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Vehicles;

namespace CarSim.Core.Tests.Vehicles;

/// <summary>The vehicle model finds its clutch, gearbox, tyres… by category and axle from the car's data, not by slot id.</summary>
public class ChassisRoleTests
{
    private static VehicleDefinition S2With(Func<EngineSlotDefinition, EngineSlotDefinition> map)
    {
        var d = TestContent.Database.GetVehicle("kestrel_s2");
        var slots = d.Slots.Select(map).ToList();
        var renamed = d.Slots.Zip(slots).ToDictionary(p => p.First.Id, p => p.Second.Id);
        return new VehicleDefinition
        {
            Id = "s2_variant", Name = d.Name, Engine = d.Engine, Drivetrain = d.Drivetrain, CurbMassKg = d.CurbMassKg,
            FrontWeightFraction = d.FrontWeightFraction, WheelbaseM = d.WheelbaseM, TrackFrontM = d.TrackFrontM, TrackRearM = d.TrackRearM,
            CgHeightM = d.CgHeightM, YawInertiaKgM2 = d.YawInertiaKgM2, DragCoefficient = d.DragCoefficient, FrontalAreaM2 = d.FrontalAreaM2,
            MaxSteerDeg = d.MaxSteerDeg, Slots = slots,
            StockParts = d.StockParts.Where(kv => renamed.ContainsKey(kv.Key)).ToDictionary(kv => renamed[kv.Key], kv => kv.Value),
        };
    }

    private static EngineSlotDefinition Slot(EngineSlotDefinition s, string? id = null, string? axle = null) => new()
    {
        Id = id ?? s.Id, Category = s.Category, DisplayName = s.DisplayName, Required = s.Required, AccessibleInVehicle = true, Axle = axle ?? s.Axle,
    };

    [Fact]
    public void RenamedSlotsDriveExactlyLikeTheOriginal()
    {
        var def = S2With(s => Slot(s, id: "my_" + s.Id));
        Assert.Empty(def.Validate());
        var chassis = VehicleAssembly.CreateStock(def, TestContent.Database, new PartInstanceFactory(9_500_000));
        var engine = SimFactory.Assembly();
        var renamed = new VehicleSimulation(new VehicleConfiguration(def, chassis, engine, TestContent.Database), SimFactory.Create(engine));
        renamed.StartIdling();
        Assert.Equal("my_tires_rear", renamed.Config.TiresRearSlot);
        Assert.Equal("my_gearbox", renamed.Config.GearboxSlot);

        double reference = Car.Accelerate(Car.Create(), 100);
        Assert.Equal(reference, Car.Accelerate(renamed, 100), 9);
    }

    [Fact]
    public void RolesMustBeUnambiguous()
    {
        var noAxle = S2With(s => s.Category == PartCategory.Tires ? Slot(s, axle: "") : s);
        Assert.Contains(noAxle.Validate(), p => p.Contains("needs \"axle\""));

        var bothFront = S2With(s => s.Category == PartCategory.Tires ? Slot(s, axle: VehicleDefinition.FrontAxle) : s);
        Assert.Contains(bothFront.Validate(), p => p.Contains("front axle") && p.Contains("uses one"));
        Assert.Contains(bothFront.Validate(), p => p.Contains("rear axle") && p.Contains("needs a slot"));

        var axleOnGearbox = S2With(s => s.Category == PartCategory.Gearbox ? Slot(s, axle: "rear") : s);
        Assert.Contains(axleOnGearbox.Validate(), p => p.Contains("only valid"));
    }
}
