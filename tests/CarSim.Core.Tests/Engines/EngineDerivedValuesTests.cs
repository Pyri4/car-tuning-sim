using CarSim.Core.Common;
using CarSim.Core.Content;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;
using CarSim.Core.Tests.Verification;
using Keys = CarSim.Core.Engines.EngineDerivedValues.Keys;

namespace CarSim.Core.Tests.Engines;

/// <summary>
/// Derived values (Engine Authoring Factory 1.0, Phase 3): one source of truth per quantity, read from the classes the
/// simulation itself builds, on the resolved assembly; deterministic; unavailable when the content cannot give them; and
/// every authored claim about a derived property validated against it, never kept as a second source.
/// </summary>
public class EngineDerivedValuesTests
{
    private static readonly ContentDatabase Db = GeneratorFixtures.Load(GeneratorFixtures.Variants).GetOrThrow();

    public static IEnumerable<object[]> Engines => TestContent.Matrix.Engines.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

    private static EngineAssembly Stock(string engineId) => EngineAssembly.CreateStock(Db.GetEngine(engineId), Db, new PartInstanceFactory());

    private static EngineDerivedValues Derived(string engineId) =>
        EngineDerivedValues.Of(Stock(engineId), Db.GetTune(Db.GetEngine(engineId).StockTune).RevLimitRpm, "stock tune");

    private static readonly PartInstanceFactory SwapFactory = new(2_000_000);

    /// <summary>Replaces the part in <paramref name="slot"/> with a fixture part, removing and re-installing what is in the way.</summary>
    private static void Swap(EngineAssembly a, string slot, string partId)
    {
        var removed = new Stack<(string Slot, PartInstance Part)>();
        foreach (var s in a.RemovalSequenceFor(slot))
        {
            Assert.True(a.Remove(s, out var p).Ok);
            removed.Push((s, p!));
        }
        Assert.True(a.Remove(slot, out _).Ok);
        var installed = a.Install(slot, SwapFactory.Create(Db.GetPart(partId)));
        Assert.True(installed.Ok, installed.Message);
        while (removed.Count > 0)
        {
            var (s, p) = removed.Pop();
            Assert.True(a.Install(s, p).Ok);
        }
    }

    private static double Value(EngineDerivedValues d, string key, int? bank = null, int? stage = null) =>
        d.Find(key, bank, stage)?.Value ?? throw new Xunit.Sdk.XunitException($"No value for {key}:\n{string.Join("\n", d.Lines())}");

    [Theory]
    [MemberData(nameof(Engines))]
    public void DisplacementIsTheGeometrysAndFollowsFromBoreStrokeAndCylinders(string engineId)
    {
        var a = Stock(engineId);
        var d = EngineDerivedValues.Of(a);
        // One source: exactly the number the simulation's geometry holds…
        Assert.Equal(Units.M3ToCc(EngineGeometry.TryCreate(a, 0, out _)!.Displacement), d.DisplacementCc);
        // …which is π/4 · bore² · stroke × cylinders of the installed block and crankshaft.
        var block = a.SpecOf<BlockSpec>(PartCategory.Block)!;
        var crank = a.SpecOf<CrankshaftSpec>(PartCategory.Crankshaft)!;
        double expected = Math.PI / 4 * Math.Pow(block.BoreMm / 10, 2) * (crank.StrokeMm / 10) * block.Cylinders;
        Assert.Equal(expected, d.DisplacementCc!.Value, 9);
        Assert.Equal(DerivedValueStatus.Derived, d.Find(Keys.Displacement)!.Status);
        Assert.Equal((DerivedValueStatus.Authored, (double)block.Cylinders), (d.Find(Keys.Cylinders)!.Status, Value(d, Keys.Cylinders)));
        Assert.Equal((DerivedValueStatus.Authored, block.BoreMm), (d.Find(Keys.Bore)!.Status, Value(d, Keys.Bore)));
        Assert.Equal((DerivedValueStatus.Authored, crank.StrokeMm), (d.Find(Keys.Stroke)!.Status, Value(d, Keys.Stroke)));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void VolumesRatiosAndTheValuesAtTheLimitAreConsistent(string engineId)
    {
        var d = Derived(engineId);
        var def = Db.GetEngine(engineId);
        double swept = Value(d, Keys.SweptVolume);
        Assert.Equal(d.DisplacementCc!.Value, swept * Value(d, Keys.Cylinders), 9);
        for (int b = 0; b < def.Banks.Count; b++)
        {
            double clearance = Value(d, Keys.ClearanceVolume, b), cylinder = Value(d, Keys.CylinderVolume, b);
            Assert.Equal(swept + clearance, cylinder, 9);
            Assert.Equal(cylinder / clearance, Value(d, Keys.CompressionRatio, b), 9);
            Assert.Equal(EngineGeometry.TryCreate(Stock(engineId), b, out _)!.CompressionRatio, Value(d, Keys.CompressionRatio, b));
        }
        double limit = Value(d, Keys.RevLimit);
        Assert.Equal(Db.GetTune(def.StockTune).RevLimitRpm, limit);
        Assert.Equal(2 * Value(d, Keys.Stroke) / 1000 * limit / 60, Value(d, Keys.MeanPistonSpeed), 9);
        // Four-stroke: the displacement once every two revolutions; the air mass at the model's reference ambient.
        double litresPerSecond = d.DisplacementCc.Value / 1000 * limit / 120;
        Assert.Equal(litresPerSecond, Value(d, Keys.Airflow), 9);
        double density = PhysicalConstants.StandardPressure / (PhysicalConstants.AirGasConstant * PhysicalConstants.StandardTemperature);
        Assert.Equal(litresPerSecond / 1000 * density, Value(d, Keys.AirMassFlow), 12);
        Assert.Equal(720.0 / def.Cylinders, Value(d, Keys.FiringInterval));
        Assert.Equal(Value(d, Keys.Bore) / Value(d, Keys.Stroke), Value(d, Keys.BoreStroke), 12);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void RunnerTunedSpeedsAreTheSimulationsOwnStages(string engineId)
    {
        var a = Stock(engineId);
        var d = EngineDerivedValues.Of(a);
        var config = EngineConfiguration.Build(a, Db.Fuels.Values.First()).GetOrThrow();
        foreach (var bank in config.Banks)
            for (int s = 0; s < bank.RunnerStages.Count; s++)
            {
                Assert.Equal(bank.RunnerStages[s].TunedRpmAtReference, Value(d, Keys.RunnerTunedSpeed, bank.Index, s));
                var diameter = d.Find(Keys.RunnerDiameter, bank.Index, s);
                Assert.Equal(bank.RunnerStages[s].DefaultDiameter, diameter != null);
                if (diameter != null) Assert.Equal(DerivedValueStatus.Defaulted, diameter.Status);
            }
        Assert.Equal(config.Banks.Sum(b => b.RunnerStages.Count), d.Values.Count(v => v.Key == Keys.RunnerTunedSpeed));
    }

    [Fact]
    public void DerivedValuesAreDeterministic()
    {
        foreach (var engineId in TestContent.Matrix.Engines.Keys)
            Assert.Equal(Derived(engineId).Lines(), Derived(engineId).Lines());
    }

    [Fact]
    public void AVariantsValuesAreThoseOfItsResolvedParts()
    {
        // The stroker variant, and the parent with the same parts swapped in by hand, derive the same values; the parent's
        // own values differ where the parts do.
        var variant = EngineDerivedValues.Of(Stock("fixture_v8_stroker"), 5800);
        var byHand = Stock("syn_v8_ohv");
        Swap(byHand, "crankshaft", "fixture.v8.crankshaft.stroker");
        Swap(byHand, "connecting_rods", "fixture.v8.rods.short");
        Swap(byHand, "injectors", "fixture.v8.injectors.320cc");
        var swapped = EngineDerivedValues.Of(byHand, 5800);
        Assert.Equal(swapped.Values.Select(v => (v.Key, v.Value)), variant.Values.Select(v => (v.Key, v.Value)));

        var parent = EngineDerivedValues.Of(Stock("syn_v8_ohv"), 5800);
        Assert.Equal(75.0, Value(variant, Keys.Stroke));
        Assert.Equal(71.0, Value(parent, Keys.Stroke));
        Assert.Equal(parent.DisplacementCc!.Value * 75 / 71, variant.DisplacementCc!.Value, 9);
        Assert.Contains("fixture.v8.crankshaft.stroker", variant.Find(Keys.Stroke)!.Basis);
        // The shorter rods keep the deck clearance: the clearance volume is unchanged, so the compression ratio rises.
        Assert.Equal(Value(parent, Keys.ClearanceVolume, 0), Value(variant, Keys.ClearanceVolume, 0), 9);
        Assert.True(Value(variant, Keys.CompressionRatio, 0) > Value(parent, Keys.CompressionRatio, 0));
        Assert.NotEqual(Value(parent, Keys.RodRatio), Value(variant, Keys.RodRatio));
    }

    [Fact]
    public void APartOnOneBankChangesOnlyThatBanksValues()
    {
        var a = Stock("syn_v8_ohv");
        var before = EngineDerivedValues.Of(a, 5800);
        Swap(a, "head_gasket_left", "fixture.v8.head_gasket.thick");
        var after = EngineDerivedValues.Of(a, 5800);
        Assert.True(Value(after, Keys.ClearanceVolume, 0) > Value(before, Keys.ClearanceVolume, 0));
        Assert.True(Value(after, Keys.CompressionRatio, 0) < Value(before, Keys.CompressionRatio, 0));
        Assert.Equal(Value(before, Keys.ClearanceVolume, 1), Value(after, Keys.ClearanceVolume, 1));
        Assert.Equal(Value(before, Keys.CompressionRatio, 1), Value(after, Keys.CompressionRatio, 1));
        Assert.Equal(before.DisplacementCc, after.DisplacementCc); // the gasket is not a displacement input
    }

    [Fact]
    public void WhatTheContentCannotGiveIsUnavailableWithItsReason()
    {
        // No parts at all: no geometry, so nothing derived from it.
        var empty = EngineDerivedValues.Of(new EngineAssembly(Db.GetEngine("syn_v8_ohv")));
        Assert.Null(empty.DisplacementCc);
        foreach (var key in new[] { Keys.Displacement, Keys.SweptVolume, Keys.RodRatio, Keys.ClearanceVolume, Keys.CompressionRatio, Keys.MeanPistonSpeed })
        {
            var v = empty.Find(key)!;
            Assert.Equal(DerivedValueStatus.Unavailable, v.Status);
            Assert.Null(v.Value);
        }
        Assert.Contains("crankshaft", empty.Find(Keys.Displacement)!.Basis);
        Assert.Contains("no intake manifold", empty.Find(Keys.RunnerTunedSpeed)!.Basis);

        // A complete build without a rev limit: the values at the limit are unavailable, the rest derived.
        var noLimit = EngineDerivedValues.Of(Stock("syn_v8_ohv"));
        Assert.NotNull(noLimit.DisplacementCc);
        Assert.Equal(DerivedValueStatus.Unavailable, noLimit.Find(Keys.MeanPistonSpeed)!.Status);
        Assert.Equal(DerivedValueStatus.Unavailable, noLimit.Find(Keys.Airflow)!.Status);
        Assert.Equal(DerivedValueStatus.Unavailable, noLimit.Find(Keys.RevLimit)!.Status);

        // Never derivable today: the crank-pin phasing is not in the schema.
        Assert.Equal(DerivedValueStatus.Unavailable, Derived("syn_v8_ohv").Find(Keys.FiringPhasing)!.Status);
    }

    [Fact]
    public void CheckEngineValidatesTheTunesDisplacementBeliefAgainstTheDerivedValue()
    {
        // Every shipped and synthetic stock tune's belief agrees with its build: validated, not a second source.
        foreach (var engineId in TestContent.Matrix.Engines.Keys)
        {
            var report = EngineCheck.Run(GeneratorFixtures.Load(), engineId);
            var belief = report.Derived!.Find("tune_displacement_cc")!;
            Assert.Equal(DerivedValueStatus.Validated, belief.Status);
            Assert.Contains(report.Facts.Select(f => f.Section).Append(CheckSection.Derived), s => s == CheckSection.Derived);
            Assert.Contains("DERIVED  ok", report.ToText());
        }
        // The stroker variant still runs its parent's tune, which believes the parent's displacement: a mismatch, the
        // existing tune_displacement_mismatch warning, and the derived value stays the build's.
        var stroker = EngineCheck.Run(GeneratorFixtures.Load(GeneratorFixtures.Variants), "fixture_v8_stroker");
        var mismatch = stroker.Derived!.Find("tune_displacement_cc")!;
        Assert.Equal(DerivedValueStatus.Mismatch, mismatch.Status);
        Assert.Equal(3941.8, mismatch.Value);
        Assert.Equal(Derived("fixture_v8_stroker").DisplacementCc, stroker.Derived.DisplacementCc);
        Assert.True(stroker.Has("tune_displacement_mismatch"));
        Assert.Contains("mismatch", string.Join("\n", stroker.Derived.Summary()));
    }

    [Fact]
    public void TheCheckReportListsEveryDerivedValueWhenVerbose()
    {
        var report = EngineCheck.Run(GeneratorFixtures.Load(), "syn_v8_ohv");
        string verbose = report.ToText(verbose: true), brief = report.ToText();
        Assert.All(report.Derived!.Lines(), line => Assert.Contains(line, verbose));
        Assert.Contains("compression ratio (bank 'right')", brief);
        Assert.Contains("firing phasing of the banks: unavailable", brief);
        Assert.DoesNotContain("— authored:", brief); // inputs are listed with --verbose only
        Assert.Contains("— authored:", verbose);
    }
}
