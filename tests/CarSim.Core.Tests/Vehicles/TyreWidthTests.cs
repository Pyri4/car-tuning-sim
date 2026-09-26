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
    /// <summary>
    /// The 225 street tyre at another width, the aspect ratio adjusted to keep the rolling radius. With
    /// <paramref name="costs"/>, its mass, rotating inertia and thermal mass scale with the width, as a real tyre's do.
    /// </summary>
    private static PartDefinition Tyre(double widthMm, bool costs = false)
    {
        var baseDef = TestContent.Database.GetPart("tires.street_225_50r16");
        var b = baseDef.GetSpec<TireSpec>();
        double k = costs ? widthMm / b.WidthMm : 1.0;
        var spec = (TireSpec)SpecAdjuster.With(b, new Dictionary<string, double>
        {
            ["width_mm"] = widthMm, ["aspect_ratio"] = b.WidthMm * b.AspectRatio / widthMm,
            ["inertia_kg_m2"] = b.InertiaKgM2 * k, ["thermal_mass_j_per_k"] = b.ThermalMassJPerK * k,
        });
        return new PartDefinition { Id = $"test.tires.{widthMm}", Name = $"{widthMm} mm", Category = PartCategory.Tires, Spec = spec, MassKg = baseDef.MassKg * k };
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
    public void WidthBuysGripAndBrakingByPercentButNoPower()
    {
        // Everything but the width equal: lateral grip and braking improve steadily (through the contact patch's load
        // sensitivity, not a width multiplier), the gain is far smaller than the width change, and a straight-line
        // launch that is not traction-limited is unchanged: width creates no power.
        var widths = new[] { 165.0, 225, 305 };
        var lateral = widths.Select(w => Car.MaxLateralG(CarOn(Tyre(w)))).ToArray();
        var braking = widths.Select(w => Car.BrakingDistance(CarOn(Tyre(w)), 100, 0.75)).ToArray();
        var launch = widths.Select(w => Car.Accelerate(CarOn(Tyre(w)), 80)).ToArray();
        for (int i = 1; i < widths.Length; i++)
        {
            Assert.True(lateral[i] > lateral[i - 1] && braking[i] < braking[i - 1], $"{widths[i]} mm: {lateral[i]:F3} g, {braking[i]:F1} m");
            // Within 1 %: a wider tyre may trim the first-gear wheelspin a little, but it cannot add power.
            Assert.InRange(launch[i] / launch[0], 0.99, 1.01);
        }
        Assert.True(lateral[^1] / lateral[0] - 1 < 0.25 * (widths[^1] / widths[0] - 1), "sub-linear in width");
    }

    [Fact]
    public void WidthCostsMassInertiaAndWarmUpWhenTheTyreCarriesThem()
    {
        // Real wide tyres are heavier, have more rotating inertia and more rubber to heat. Authored that way (as the
        // shipped tyres are), width stops being free: slower to accelerate, slower to reach its temperature window.
        var narrow = CarOn(Tyre(165, costs: true));
        var wide = CarOn(Tyre(305, costs: true));
        Assert.True(Car.Accelerate(wide, 100) > Car.Accelerate(narrow, 100) + 0.2);
        double Warm(VehicleSimulation sim)
        {
            sim.Tyres.SetAll(298.15);
            Car.Rolling(sim, 70, 3);
            var input = new VehicleInputs { Steer = 0.12 };
            var t = sim.Step(Car.Dt, input);
            for (int i = 0; i < 20_000; i++) { input.Throttle = Math.Clamp(0.3 + (70 / 3.6 - t.Speed) * 0.3, 0, 1); t = sim.Step(Car.Dt, input); }
            return t.TyreTemperatureC.Average();
        }
        double narrowC = Warm(CarOn(Tyre(165, costs: true))), wideC = Warm(CarOn(Tyre(305, costs: true)));
        Assert.True(wideC < narrowC - 10, $"after 40 s from cold: 165 mm {narrowC:F0} °C, 305 mm {wideC:F0} °C");
    }

    [Fact]
    public void TheShippedTyresCarryTheCostsOfTheirWidth()
    {
        // The model applies mass, inertia and thermal mass; the content must author them to grow with width within a
        // compound, or a wider tyre would be free grip.
        foreach (var compound in TestContent.Database.Parts.Values.Where(p => p.Category == PartCategory.Tires).GroupBy(p => ((TireSpec)p.Spec).Compound))
        {
            var byWidth = compound.OrderBy(p => ((TireSpec)p.Spec).WidthMm).ToList();
            for (int i = 1; i < byWidth.Count; i++)
            {
                TireSpec a = (TireSpec)byWidth[i - 1].Spec, b = (TireSpec)byWidth[i].Spec;
                if (b.WidthMm <= a.WidthMm) continue;
                Assert.True(byWidth[i].MassKg >= byWidth[i - 1].MassKg && b.InertiaKgM2 >= a.InertiaKgM2 && b.ThermalMassJPerK >= a.ThermalMassJPerK,
                    $"{byWidth[i].Id} is wider than {byWidth[i - 1].Id} but not heavier");
            }
        }
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
