using CarSim.Core.Content;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Tests.Engines;

public class AssemblyValidatorTests
{
    [Fact]
    public void StockEngineHasNoErrorsOrWarnings()
    {
        var report = AssemblyValidator.Validate(TestContent.StockK20(), new ValidationContext(RevLimitRpm: 7600));
        Assert.True(report.CanRun, string.Join("\n", report.Issues));
        Assert.Empty(report.Warnings);
        Assert.True(report.Has("compression_ratio"));
    }

    [Fact]
    public void RaceCamsOnOemSpringsCoilBind()
    {
        var a = TestContent.StockK20();
        TestContent.Swap(a, "camshafts", "k20.cams.race");
        var report = AssemblyValidator.Validate(a);
        Assert.False(report.CanRun);
        Assert.True(report.Has("coil_bind"));
        TestContent.Swap(a, "valve_springs", "k20.valve_springs.performance");
        Assert.False(AssemblyValidator.Validate(a).Has("coil_bind"));
    }

    [Fact]
    public void StrokerCrankWithStockPistonsHitsHead()
    {
        var a = TestContent.StockK20();
        TestContent.Swap(a, "crankshaft", "k20.crankshaft.stroker90");
        var report = AssemblyValidator.Validate(a);
        Assert.True(report.Has("piston_head_contact"));
        Assert.False(report.CanRun);
    }

    [Fact]
    public void StrokerPistonsOnStockCrankWarnPoorQuench()
    {
        var a = TestContent.StockK20();
        TestContent.Swap(a, "pistons", "k20.pistons.stroker_lc");
        var report = AssemblyValidator.Validate(a);
        Assert.True(report.CanRun);
        Assert.True(report.Has("poor_quench"));
    }

    [Fact]
    public void MissingPartIsAnError()
    {
        var a = TestContent.StockK20();
        a.Remove("ecu", out _);
        var report = AssemblyValidator.Validate(a);
        Assert.False(report.CanRun);
        Assert.Contains(report.Errors, i => i.Code == "missing_part" && i.Slots.Contains("ecu"));
    }

    [Fact]
    public void HighRevLimitWarnsAboutValveFloatCrankAndFlywheel()
    {
        var report = AssemblyValidator.Validate(TestContent.StockK20(), new ValidationContext(RevLimitRpm: 9000));
        Assert.True(report.Has("valve_float"));
        Assert.True(report.Has("crank_overspeed"));
        Assert.True(report.Has("flywheel_overspeed"));
        Assert.True(report.CanRun);
    }

    [Fact]
    public void MissingInterfaceIsReported()
    {
        // A head requiring a deck interface that no block provides.
        var content = ContentLoader.LoadFromStrings(new[] { ("t.json", """
            { "parts": [ { "id": "alien.head", "name": "Alien head", "category": "cylinder_head", "requires": ["alien.deck"],
                "spec": { "chamber_volume_cc": 50, "valve_moving_mass_g": 90,
                          "intake_port_flow_cfm": [[1, 30], [10, 200]], "exhaust_port_flow_cfm": [[1, 25], [10, 150]] } } ] }
            """) }).GetOrThrow();
        var a = TestContent.StockK20();
        var f = new PartInstanceFactory(900);
        var removed = new Stack<(string, PartInstance)>();
        foreach (var s in a.RemovalSequenceFor("cylinder_head")) { a.Remove(s, out var p); removed.Push((s, p!)); }
        a.Remove("cylinder_head", out _);
        Assert.True(a.Install("cylinder_head", f.Create(content.GetPart("alien.head"))).Ok);
        while (removed.Count > 0) { var (s, p) = removed.Pop(); a.Install(s, p); }
        var report = AssemblyValidator.Validate(a);
        Assert.Contains(report.Errors, i => i.Code == "missing_interface" && i.Message.Contains("alien.deck"));
        // Parts that needed the stock head's interfaces are now unsupported too.
        Assert.Contains(report.Errors, i => i.Code == "missing_interface" && i.Message.Contains("k20.cam_carrier"));
    }

    private static PartDefinition Custom(string category, PartSpec spec) => new()
    {
        Id = "custom." + category, Name = "Custom " + category, Category = category, Spec = spec,
    };

    private static void Replace(EngineAssembly a, string slot, PartDefinition def)
    {
        var removed = new Stack<(string, PartInstance)>();
        foreach (var s in a.RemovalSequenceFor(slot)) { Assert.True(a.Remove(s, out var p).Ok); removed.Push((s, p!)); }
        Assert.True(a.Remove(slot, out _).Ok);
        Assert.True(a.Install(slot, new PartInstanceFactory(7000).Create(def)).Ok);
        while (removed.Count > 0) { var (s, p) = removed.Pop(); Assert.True(a.Install(s, p).Ok); }
    }

    [Fact]
    public void OversizePistonsInStandardBoreAreRejected()
    {
        var a = TestContent.StockK20();
        var oem = TestContent.Database.GetPart("k20.pistons.oem").GetSpec<PistonSpec>();
        var oversize = new PistonSpec
        {
            Count = 4, BoreMm = 86.5, PinDiameterMm = oem.PinDiameterMm, CompressionHeightMm = oem.CompressionHeightMm,
            DishVolumeCc = oem.DishVolumeCc, MassEachG = oem.MassEachG, MaxCylinderPressureBar = oem.MaxCylinderPressureBar,
            MaxCrownTemperatureC = oem.MaxCrownTemperatureC,
        };
        Replace(a, "pistons", Custom("pistons", oversize));
        var report = AssemblyValidator.Validate(a);
        Assert.Contains(report.Errors, i => i.Code == "piston_bore_mismatch" && i.Message.Contains("bored"));
    }

    [Fact]
    public void RodJournalMismatchIsRejected()
    {
        var a = TestContent.StockK20();
        var oem = TestContent.Database.GetPart("k20.rods.oem").GetSpec<ConnectingRodSpec>();
        var rods = new ConnectingRodSpec
        {
            Count = 4, LengthMm = oem.LengthMm, BigEndDiameterMm = 50.0, PinDiameterMm = oem.PinDiameterMm, MassEachG = oem.MassEachG,
            MaxTensileLoadKn = oem.MaxTensileLoadKn, MaxCompressiveLoadKn = oem.MaxCompressiveLoadKn,
        };
        Replace(a, "connecting_rods", Custom("connecting_rods", rods));
        Assert.True(AssemblyValidator.Validate(a).Has("rod_journal_mismatch"));
    }

    [Fact]
    public void WrongInjectorCountIsRejected()
    {
        var a = TestContent.StockK20();
        Replace(a, "injectors", Custom("injectors", new InjectorSpec { Count = 6, FlowCcMin = 310 }));
        Assert.True(AssemblyValidator.Validate(a).Has("set_count"));
    }
}
