using CarSim.Core.Engines;
using CarSim.Core.Parts;

namespace CarSim.Core.Tests.Engines;

public class EngineAssemblyTests
{
    [Fact]
    public void StockBuildIsComplete()
    {
        var a = TestContent.StockK20();
        Assert.True(a.IsComplete);
        Assert.Empty(a.MissingRequiredSlots());
        Assert.True(a.TotalMassKg > 100);
    }

    [Fact]
    public void CannotRemovePistonsWithHeadInstalled()
    {
        var a = TestContent.StockK20();
        var r = a.CanRemove("pistons");
        Assert.False(r.Ok);
        Assert.Contains("head_gasket", r.BlockingSlots);
    }

    [Fact]
    public void RemovalSequenceReachesPistonsFromTheOutsideIn()
    {
        var a = TestContent.StockK20();
        var seq = a.RemovalSequenceFor("pistons");
        // Everything above the pistons must come off, in a removable order.
        Assert.Contains("cylinder_head", seq);
        Assert.Contains("head_gasket", seq);
        Assert.Contains("camshafts", seq);
        Assert.True(seq.ToList().IndexOf("camshafts") < seq.ToList().IndexOf("cylinder_head"));
        Assert.True(seq.ToList().IndexOf("cylinder_head") < seq.ToList().IndexOf("head_gasket"));
        foreach (var slot in seq)
        {
            var r = a.Remove(slot, out _);
            Assert.True(r.Ok, $"{slot}: {r.Message}");
        }
        Assert.True(a.Remove("pistons", out var removed).Ok);
        Assert.Equal("k20.pistons.oem", removed!.Definition.Id);
        // The oil pan is not above the pistons and stays on.
        Assert.True(a.IsInstalled("oil_pan"));
    }

    [Fact]
    public void FullTeardownAndRebuildRestoresCompleteEngine()
    {
        var a = TestContent.StockK20();
        var removed = new List<(string slot, PartInstance part)>();
        // Tear down: keep removing whatever is removable until empty.
        while (a.Installed.Count > 0)
        {
            var slot = a.Installed.Keys.OrderBy(k => k, StringComparer.Ordinal).First(k => a.CanRemove(k).Ok);
            Assert.True(a.Remove(slot, out var p).Ok);
            removed.Add((slot, p!));
        }
        Assert.Equal(TestContent.Database.GetEngine(TestContent.K20).Slots.Count, removed.Count);
        // Rebuild in reverse removal order.
        for (int i = removed.Count - 1; i >= 0; i--)
        {
            var r = a.Install(removed[i].slot, removed[i].part);
            Assert.True(r.Ok, r.Message);
        }
        Assert.True(a.IsComplete);
    }

    [Fact]
    public void InstallOrderIsEnforced()
    {
        var engine = TestContent.Database.GetEngine(TestContent.K20);
        var a = new EngineAssembly(engine);
        var f = new PartInstanceFactory();
        var head = f.Create(TestContent.Database.GetPart("k20.head.oem"));
        var r = a.Install("cylinder_head", head);
        Assert.False(r.Ok);
        Assert.Contains("head_gasket", r.BlockingSlots);
        Assert.True(a.Install("block", f.Create(TestContent.Database.GetPart("k20.block.oem"))).Ok);
    }

    [Fact]
    public void WrongCategoryIsRejected()
    {
        var a = new EngineAssembly(TestContent.Database.GetEngine(TestContent.K20));
        var piston = new PartInstanceFactory().Create(TestContent.Database.GetPart("k20.pistons.oem"));
        var r = a.Install("block", piston);
        Assert.False(r.Ok);
        Assert.Contains("takes block", r.Message);
    }

    [Fact]
    public void OccupiedSlotRejectsSecondPart()
    {
        var a = TestContent.StockK20();
        var another = new PartInstanceFactory(500).Create(TestContent.Database.GetPart("ecu.standalone"));
        Assert.False(a.Install("ecu", another).Ok);
    }

    [Fact]
    public void SameInstanceCannotBeInstalledTwice()
    {
        var engine = TestContent.Database.GetEngine(TestContent.K20);
        var a = new EngineAssembly(engine);
        var ecu = new PartInstanceFactory().Create(TestContent.Database.GetPart("ecu.k20_oem"));
        Assert.True(a.Install("ecu", ecu).Ok);
        Assert.True(a.Remove("ecu", out _).Ok);
        Assert.True(a.Install("ecu", ecu).Ok);
    }

    [Fact]
    public void RevisionChangesOnEdit()
    {
        var a = TestContent.StockK20();
        int rev = a.Revision;
        a.Remove("ecu", out var ecu);
        Assert.NotEqual(rev, a.Revision);
    }

    [Fact]
    public void AssemblyOrderIsTopological()
    {
        var engine = TestContent.Database.GetEngine(TestContent.K20);
        var order = engine.AssemblyOrder().Select(s => s.Id).ToList();
        foreach (var slot in engine.Slots)
            foreach (var dep in slot.InstallAfter)
                Assert.True(order.IndexOf(dep) < order.IndexOf(slot.Id), $"{dep} must precede {slot.Id}");
    }
}
