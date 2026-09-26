using System.Reflection;
using System.Text.Json;
using CarSim.Core.Content;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;
using CarSim.Core.Tests.Simulation;
using CarSim.Core.Tests.Vehicles;
using CarSim.Core.Vehicles;

namespace CarSim.Core.Tests.Content;

/// <summary>
/// "Valid-looking data must never crash the runtime": every numeric spec field of every installed part is scaled far
/// from its authored value (0, negative, 1000× smaller and larger). Wherever the part's own validator accepts the
/// value, the part is installed: the build must then be refused with reasons, or run to finite numbers. A crash or a
/// NaN here means the validator accepts something the simulation cannot represent.
/// </summary>
public class SpecFuzzTests
{
    private static readonly double[] Factors = { -1, 0, 1e-3, 0.3, 3, 1e3 };

    private static PartSpec Clone(PartSpec s) =>
        (PartSpec)JsonSerializer.Deserialize(JsonSerializer.Serialize(s, s.GetType(), ContentJson.Options), s.GetType(), ContentJson.Options)!;

    /// <summary>Copies of <paramref name="part"/> with one numeric spec field scaled, that its validator accepts.</summary>
    private static IEnumerable<(PartDefinition Part, string What)> Variants(PartDefinition part)
    {
        var props = part.Spec.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && (p.PropertyType == typeof(double) || p.PropertyType == typeof(int) || p.PropertyType == typeof(double?)));
        foreach (var prop in props)
            foreach (double f in Factors)
            {
                var spec = Clone(part.Spec);
                if (prop.GetValue(spec) is null) continue; // an optional field the part does not author
                double v0 = Convert.ToDouble(prop.GetValue(spec));
                double v = v0 == 0 ? f : v0 * f;
                if (prop.PropertyType == typeof(int)) prop.SetValue(spec, (int)Math.Round(Math.Clamp(v, -1e6, 1e6)));
                else prop.SetValue(spec, v);
                if (spec.Validate().Count > 0) continue;
                yield return (new PartDefinition
                {
                    Id = part.Id + ".fuzz", Name = part.Name, Category = part.Category, Provides = part.Provides, Requires = part.Requires,
                    MassKg = part.MassKg, Adjustments = part.Adjustments, Spec = spec,
                }, $"{part.Id}.{prop.Name} = {v:G4}");
            }
    }

    [Theory]
    [InlineData(TestContent.K20, false)]
    [InlineData(TestContent.K20, true)]
    [InlineData(TestContent.M54, false)]
    public void AnyEngineSpecTheValidatorAcceptsBuildsAndRunsOrIsRefused(string engineId, bool turbo)
    {
        var db = TestContent.Database;
        var fuel = db.GetFuel("gasoline_98");
        var tune = turbo ? CarSim.Core.Ecu.EcuTune.FromDocument(db.GetTune("k20.turbo_base")) : SimFactory.StockTuneOf(engineId);
        var factory = new PartInstanceFactory(9_000_000);
        var reference = turbo ? TurboTests.TurboBuild() : TestContent.Stock(engineId);
        int built = 0, refused = 0;
        foreach (var slot in reference.Definition.Slots)
        {
            if (reference.PartIn(slot.Id) is not { } original) continue;
            foreach (var (part, what) in Variants(original.Definition))
            {
                var a = turbo ? TurboTests.TurboBuild() : TestContent.Stock(engineId);
                var removed = new Stack<(string, PartInstance)>();
                foreach (var s in a.RemovalSequenceFor(slot.Id)) { Assert.True(a.Remove(s, out var p).Ok); removed.Push((s, p!)); }
                Assert.True(a.Remove(slot.Id, out _).Ok);
                if (!a.Install(slot.Id, factory.Create(part)).Ok) { refused++; continue; }
                while (removed.Count > 0) { var (s, p) = removed.Pop(); Assert.True(a.Install(s, p).Ok); }
                var result = EngineConfiguration.Build(a, fuel, new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa));
                if (!result.Success) { Assert.NotEmpty(result.Report.Errors); refused++; continue; }
                built++;
                foreach (var (rpm, throttle) in new[] { (900.0, 0.0), (6500.0, 1.0) })
                {
                    var sim = new EngineSimulation(result.Configuration!, tune.Clone(), EngineState.Warm());
                    var input = new EngineInputs { Throttle = throttle, SpeedMode = SpeedMode.Held, HeldRpm = rpm, CoolantTemperatureOverride = 363.15 };
                    EngineTelemetry t = null!;
                    for (int i = 0; i < 60; i++) t = sim.Step(0.005, input);
                    Assert.True(double.IsFinite(t.Torque) && double.IsFinite(t.AirMassFlow) && double.IsFinite(t.PortGasTemperature)
                                && double.IsFinite(t.FuelPower) && double.IsFinite(sim.State.TurboOmega), $"{what} at {rpm} rpm");
                }
            }
        }
        Assert.True(built > 100 && refused > 20, $"built {built}, refused {refused}: the fuzz must exercise both outcomes");
    }

    [Fact]
    public void AnyChassisSpecTheValidatorAcceptsDrivesToFiniteNumbers()
    {
        var db = TestContent.Database;
        var def = db.GetVehicle("kestrel_s2");
        var factory = new PartInstanceFactory(31_000_000);
        var stock = VehicleAssembly.CreateStock(def, db, factory);
        int driven = 0;
        foreach (var slot in def.Slots)
        {
            if (stock.PartIn(slot.Id) is not { } original) continue;
            foreach (var (part, what) in Variants(original.Definition))
            {
                var chassis = VehicleAssembly.CreateStock(def, db, factory);
                Assert.True(chassis.Remove(slot.Id, out _).Ok);
                if (!chassis.Install(slot.Id, factory.Create(part)).Ok) continue;
                var engine = SimFactory.Assembly();
                var sim = new VehicleSimulation(new VehicleConfiguration(def, chassis, engine, db), SimFactory.Create(engine));
                sim.StartIdling();
                var input = new VehicleInputs { ShiftUp = true };
                var t = sim.Step(Car.Dt, input);
                input.ShiftUp = false;
                input.Throttle = 1;
                double maxLateral = 0;
                for (int i = 0; i < 900; i++)
                {
                    if (i == 450) input.Steer = 0.3;
                    if (i == 700) { input.Throttle = 0; input.Brake = 1; }
                    t = sim.Step(Car.Dt, input);
                    maxLateral = Math.Max(maxLateral, Math.Abs(t.LateralG));
                }
                Assert.True(double.IsFinite(t.Speed) && double.IsFinite(t.X) && double.IsFinite(t.Y) && double.IsFinite(t.EngineRpm), what);
                Assert.True(maxLateral < 4.0, $"{what}: {maxLateral:F1} g");
                driven++;
            }
        }
        Assert.True(driven > 100, $"only {driven} chassis variants driven");
    }
}
