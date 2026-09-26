using CarSim.Core.Content;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Engines;

/// <summary>
/// Content that loads must never crash the engine build (an engine family with an optional radiator used to
/// pass validation and then throw "No radiator installed" from EngineConfiguration.Build).
/// </summary>
public class EngineTopologyTests
{
    private static EngineSlotDefinition Copy(EngineSlotDefinition s, string? id = null, bool? required = null) => new()
    {
        Id = id ?? s.Id, Category = s.Category, DisplayName = s.DisplayName, Required = required ?? s.Required,
        InstallAfter = s.InstallAfter, AccessibleInVehicle = s.AccessibleInVehicle,
    };

    private static EngineDefinition K20Variant(Func<EngineSlotDefinition, EngineSlotDefinition> map, string layout = "inline",
        params EngineSlotDefinition[] extra)
    {
        var k20 = TestContent.Database.GetEngine(TestContent.K20);
        return new EngineDefinition
        {
            Id = "variant", Name = "Variant", Cylinders = k20.Cylinders, Layout = layout,
            Slots = k20.Slots.Select(map).Concat(extra).ToList(), StockParts = k20.StockParts, StockTune = k20.StockTune,
        };
    }

    private static EngineAssembly StockOf(EngineDefinition def, params string[] leaveEmpty)
    {
        var a = new EngineAssembly(def);
        var factory = new PartInstanceFactory(7_000_000);
        foreach (var slot in def.AssemblyOrder())
            if (!leaveEmpty.Contains(slot.Id) && def.StockParts.TryGetValue(slot.Id, out var id))
                Assert.True(a.Install(slot.Id, factory.Create(TestContent.Database.GetPart(id))).Ok);
        return a;
    }

    [Fact]
    public void AnOptionalRadiatorCannotRunButNeverThrows()
    {
        var def = K20Variant(s => s.Category == PartCategory.Radiator ? Copy(s, required: false) : s);
        Assert.Contains(EngineTopology.CheckFamily(def), p => p.Contains("radiator") && p.Contains("cannot be optional"));

        var a = StockOf(def, "radiator");
        var report = AssemblyValidator.Validate(a);
        Assert.False(report.CanRun);
        Assert.True(report.Has("missing_category"));
        var result = EngineConfiguration.Build(a, TestContent.Database.GetFuel("gasoline_95"));
        Assert.False(result.Success);
    }

    [Fact]
    public void TheContentLoaderRejectsFamiliesTheModelCannotRepresent()
    {
        string engines = File.ReadAllText(Path.Combine(TestContent.BaseContentPath, "engines", "kestrel_k20.json"));
        const string radiatorSlot = "\"display_name\": \"Radiator\",";
        Assert.Contains(radiatorSlot, engines);
        var baseLayer = Directory.GetFiles(TestContent.BaseContentPath, "*.json", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(TestContent.BaseContentPath, f), File.ReadAllText(f))).ToList();

        var aircooled = ContentLoader.LoadFromLayers(new List<(string, string)>[]
        {
            baseLayer, new() { ("mods/aircooled/engines.json", engines.Replace(radiatorSlot, radiatorSlot + " \"required\": false,")) },
        });
        Assert.Contains(aircooled.Errors, e => e.Message.Contains("cannot be optional") && e.Message.Contains("radiator"));

        var vtwin = ContentLoader.LoadFromLayers(new List<(string, string)>[]
        {
            baseLayer, new() { ("mods/v/engines.json", engines.Replace("\"layout\": \"inline\"", "\"layout\": \"w\"")) },
        });
        Assert.Contains(vtwin.Errors, e => e.Message.Contains("Unknown layout 'w'"));
    }

    [Fact]
    public void TwoSlotsForAPartTheModelReadsOnceAreRejected()
    {
        // Two heads for the one bank of an inline engine: the model would have to ignore one of them. (A slot per bank
        // on an engine with two banks is fine: see ArchitectureTests.)
        var head = TestContent.Database.GetEngine(TestContent.K20).FindSlot("cylinder_head")!;
        var def = K20Variant(s => s, extra: Copy(head, id: "cylinder_head_b"));
        Assert.Contains(EngineTopology.CheckFamily(def), p => p.Contains("'cylinder_head_b'") && p.Contains("each bank takes one"));
        var report = AssemblyValidator.Validate(StockOf(def));
        Assert.True(report.Has("unsupported_topology"));
        Assert.False(EngineConfiguration.Build(StockOf(def), TestContent.Database.GetFuel("gasoline_95")).Success);
    }

    [Fact]
    public void EveryShippedFamilyFitsTheModel()
    {
        foreach (var engine in TestContent.Database.Engines.Values)
            Assert.Empty(EngineTopology.CheckFamily(engine));
    }

    [Theory]
    [InlineData(TestContent.K20, 40)]
    [InlineData(TestContent.M54, 30)] // most parts in the database are K20-mounted, which the M54 refuses
    public void EveryPartInEverySlotBuildsOrExplainsWhyNot(string engineId, int minBuilt)
    {
        // Property test: any assembly the workshop allows either builds and runs to finite numbers, or is
        // refused with reasons — never an exception.
        var db = TestContent.Database;
        var fuel = db.GetFuel("gasoline_98");
        var factory = new PartInstanceFactory(8_000_000);
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 5000, CoolantTemperatureOverride = 363.15 };
        int built = 0, refused = 0;
        void Check(EngineAssembly a, string what)
        {
            var tune = SimFactory.StockTuneOf(engineId);
            var result = EngineConfiguration.Build(a, fuel, new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa));
            if (!result.Success)
            {
                Assert.NotEmpty(result.Report.Errors);
                refused++;
                return;
            }
            built++;
            var sim = new EngineSimulation(result.Configuration!, tune, EngineState.Warm());
            EngineTelemetry t = null!;
            for (int i = 0; i < 100; i++) t = sim.Step(0.005, input);
            Assert.True(double.IsFinite(t.Torque) && double.IsFinite(t.AirMassFlow) && double.IsFinite(t.ExhaustGasTemperature), what);
        }

        var stock = TestContent.Stock(engineId);
        foreach (var slot in stock.Definition.Slots)
        {
            foreach (var part in db.Parts.Values.Where(p => p.Category == slot.Category))
            {
                var a = TestContent.Stock(engineId);
                var removed = new Stack<(string, PartInstance)>();
                foreach (var s in a.RemovalSequenceFor(slot.Id)) { Assert.True(a.Remove(s, out var p).Ok); removed.Push((s, p!)); }
                if (a.Installed.ContainsKey(slot.Id)) Assert.True(a.Remove(slot.Id, out _).Ok);
                if (!a.Install(slot.Id, factory.Create(part)).Ok) continue;
                while (removed.Count > 0) { var (s, p) = removed.Pop(); Assert.True(a.Install(s, p).Ok); }
                Check(a, $"{part.Id} in {slot.Id}");
            }
            // And with the slot empty.
            var without = TestContent.Stock(engineId);
            foreach (var s in without.RemovalSequenceFor(slot.Id).Append(slot.Id).Where(without.Installed.ContainsKey))
                Assert.True(without.Remove(s, out _).Ok);
            Check(without, $"without {slot.Id}");
        }
        Assert.True(built > minBuilt && refused > 20, $"built {built}, refused {refused}");
    }
}
