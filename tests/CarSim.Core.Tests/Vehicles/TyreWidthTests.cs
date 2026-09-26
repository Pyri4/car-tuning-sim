using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Vehicles;

namespace CarSim.Core.Tests.Vehicles;

/// <summary>
/// Tyre width must change grip. Originally 165, 225 and 305 mm tyres at the same rolling radius gave
/// bit-identical skidpad, braking and acceleration: width only fed the radius.
/// </summary>
public class TyreWidthTests
{
    /// <summary>The 225 street tyre at another width, the aspect ratio adjusted to keep the rolling radius.</summary>
    private static PartDefinition Tyre(double widthMm)
    {
        var baseDef = TestContent.Database.GetPart("tires.street_225_50r16");
        var b = baseDef.GetSpec<TireSpec>();
        var spec = (TireSpec)SpecAdjuster.With(b, new Dictionary<string, double>
        {
            ["width_mm"] = widthMm, ["aspect_ratio"] = b.WidthMm * b.AspectRatio / widthMm,
        });
        return new PartDefinition { Id = $"test.tires.{widthMm}", Name = $"{widthMm} mm", Category = PartCategory.Tires, Spec = spec, MassKg = baseDef.MassKg };
    }

    private static VehicleSimulation CarOn(PartDefinition tyre)
    {
        var db = TestContent.Database;
        var def = db.GetVehicle("kestrel_s2");
        var factory = new PartInstanceFactory(12_000_000);
        var chassis = VehicleAssembly.CreateStock(def, db, factory);
        foreach (var slot in new[] { def.TireSlot(front: true), def.TireSlot(front: false) })
        {
            Assert.True(chassis.Remove(slot, out _).Ok);
            Assert.True(chassis.Install(slot, factory.Create(tyre)).Ok);
        }
        var engine = SimFactory.Assembly();
        var sim = new VehicleSimulation(new VehicleConfiguration(def, chassis, engine, db), SimFactory.Create(engine));
        sim.StartIdling();
        return sim;
    }

    [Fact]
    public void AWiderContactPatchGripsMoreAndPeaksAtASmallerSlipAngle()
    {
        var narrow = (TireSpec)Tyre(165).Spec;
        var wide = (TireSpec)Tyre(305).Spec;
        Assert.Equal(narrow.Radius, wide.Radius, 9);
        Assert.Equal(TireModel.ReferenceLoadAtReferenceWidth * 305 / 205, TireModel.ReferenceLoad(wide), 9);
        foreach (double fz in new[] { 1500.0, 3500, 6000 })
            Assert.True(TireModel.Friction(wide, fz) > TireModel.Friction(narrow, fz) * 1.04, $"{fz} N");
        Assert.Equal(Math.Sqrt(305.0 / 165.0), TireModel.PeakSlipAngle(narrow, 220) / TireModel.PeakSlipAngle(wide, 220), 9);
        // Load transfer costs a wide tyre a smaller share of its grip.
        double Loss(TireSpec t) => 1 - TireModel.Friction(t, 6000) / TireModel.Friction(t, 3000);
        Assert.True(Loss(wide) < Loss(narrow));
    }

    [Fact]
    public void WiderTyresCornerHarderAtTheSameRollingRadius()
    {
        double g165 = Car.MaxLateralG(CarOn(Tyre(165)));
        double g225 = Car.MaxLateralG(CarOn(Tyre(225)));
        double g305 = Car.MaxLateralG(CarOn(Tyre(305)));
        Assert.True(g225 > g165 * 1.02 && g305 > g225 * 1.02, $"165: {g165:F3} g, 225: {g225:F3} g, 305: {g305:F3} g");
        Assert.True(g305 < g165 * 1.2, "width helps, but by percent, not by a compound change");
    }

    [Fact]
    public void CombinedForceNeverExceedsTheFrictionLimit()
    {
        foreach (var part in TestContent.Database.Parts.Values.Where(p => p.Category == PartCategory.Tires).Append(Tyre(165)).Append(Tyre(305)))
        {
            var t = (TireSpec)part.Spec;
            double upright = Math.Max(TireModel.CamberLateralFactor(0), TireModel.CamberLongitudinalFactor(0));
            foreach (double fz in new[] { 300.0, 3500, 9000 })
                for (double kappa = -1; kappa <= 1.0001; kappa += 0.05)
                    for (double alpha = -0.6; alpha <= 0.6001; alpha += 0.03)
                    {
                        var (fx, fy) = TireModel.Forces(t, fz, kappa, alpha);
                        double limit = TireModel.Friction(t, fz) * fz * upright;
                        Assert.True(Math.Sqrt(fx * fx + fy * fy) <= limit * (1 + 1e-9), $"{part.Id} fz {fz} κ {kappa:F2} α {alpha:F2}");
                    }
        }
    }
}
