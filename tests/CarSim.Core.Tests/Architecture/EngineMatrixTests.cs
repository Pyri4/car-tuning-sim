using CarSim.Core.Common;
using CarSim.Core.Content;
using CarSim.Core.Damage;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;
using CarSim.Core.Vehicles;
using CarSim.Gameplay;

namespace CarSim.Core.Tests.Architecture;

/// <summary>
/// The synthetic engine matrix (content/test/engine-matrix): content-only engines of every architecture the milestone
/// asks for, each taken through the same pipeline as the shipped families — load, validate, strip and rebuild,
/// start, idle, dyno, tune, save and load, wear, fail, inspect, drive. No test here names a family: every check runs
/// over whatever the matrix layer contains, and the coverage test says which architectures it must contain.
/// </summary>
public class EngineMatrixTests
{
    private static ContentDatabase Db => TestContent.Matrix;

    public static IEnumerable<object[]> Families => TestContent.MatrixFamilies.Select(f => new object[] { f });

    /// <summary>The fuel each family was calibrated on (its bench scenario's).</summary>
    private static string FuelOf(string family) => Db.Scenarios.Values.Single(s => s.Engine == family && s.Vehicle.Length == 0).Fuel;

    private static EngineAssembly Stock(string family, PartInstanceFactory? factory = null) =>
        EngineAssembly.CreateStock(Db.GetEngine(family), Db, factory ?? new PartInstanceFactory());

    private static EcuTune TuneOf(string family) => EcuTune.FromDocument(Db.GetTune(Db.GetEngine(family).StockTune));

    private static EngineSimulation Sim(EngineAssembly a, string family, EcuTune? tune = null, EngineState? state = null)
    {
        tune ??= TuneOf(family);
        var config = EngineConfiguration.Build(a, Db.GetFuel(FuelOf(family)), new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa)).GetOrThrow();
        return new EngineSimulation(config, tune, state ?? EngineState.Warm());
    }

    private static EngineCapabilities Capabilities(string family) => EngineCapabilities.Resolve(Stock(family));

    // ---- What the matrix must contain ------------------------------------------------------------------------------

    [Fact]
    public void TheMatrixCoversEveryRequiredArchitecture()
    {
        var caps = TestContent.MatrixFamilies.ToDictionary(f => f, Capabilities);
        void Covered(string what, Func<EngineCapabilities, bool> test) =>
            Assert.True(caps.Values.Any(test), $"No synthetic engine is a {what}.");
        Covered("naturally aspirated inline-4", c => c is { Layout: "inline", Cylinders: 4, ForcedInduction: false });
        Covered("naturally aspirated inline-6", c => c is { Layout: "inline", Cylinders: 6, ForcedInduction: false });
        Covered("naturally aspirated V6", c => c is { Layout: "v", Cylinders: 6, ForcedInduction: false });
        Covered("naturally aspirated V8", c => c is { Layout: "v", Cylinders: 8, ForcedInduction: false });
        Covered("turbo inline-4", c => c is { Layout: "inline", Cylinders: 4, ForcedInduction: true });
        Covered("turbo V6", c => c is { Layout: "v", Cylinders: 6, ForcedInduction: true });
        Covered("pushrod V8", c => c is { Layout: "v", Cylinders: 8 } && c.Valvetrains.Contains(Parts.Specs.ValvetrainTypes.Ohv));
        Covered("DOHC V8", c => c is { Layout: "v", Cylinders: 8 } && c.Valvetrains.Contains(Parts.Specs.ValvetrainTypes.Dohc));
        Covered("engine with variable valve timing", c => c.IntakeCamPhasing);
        Covered("engine with variable valve lift", c => c.VariableValveLift);
        Covered("engine with a variable intake runner", c => c.VariableIntakeRunner);
        Covered("multi-bank engine", c => c.Banks > 1);
        Covered("engine with several air paths", c => c.IntakePaths > 1 || c.ExhaustPaths > 1);
        Covered("engine with several turbochargers", c => c.Turbochargers > 1);
        Covered("engine with one turbocharger for several banks", c => c is { Turbochargers: 1, Banks: > 1 });
        Covered("SOHC engine", c => c.Valvetrains.Contains(Parts.Specs.ValvetrainTypes.Sohc));
        Covered("flat engine", c => c.Layout == "flat");
        Covered("odd-cylinder engine", c => c.Cylinders % 2 == 1);
    }

    [Fact]
    public void TheMatrixLoadsAsAContentLayerWithoutErrors()
    {
        var result = ContentLoader.LoadWithMods(TestContent.BaseContentPath, TestContent.MatrixLayersPath);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Overrides); // it adds families; it redefines nothing of the base game
        Assert.True(TestContent.MatrixFamilies.Count >= 9);
    }

    // ---- The same pipeline for every family ------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Families))]
    public void ValidatesCleanlyAndResolvesItsArchitectureFromData(string family)
    {
        var def = Db.GetEngine(family);
        Assert.Empty(EngineTopology.CheckFamily(def));
        var a = Stock(family);
        var report = AssemblyValidator.Validate(a, new ValidationContext(TuneOf(family).RevLimitRpm, TuneOf(family).MaxBoostTargetKpa));
        Assert.True(report.CanRun, string.Join("\n", report.Errors));
        Assert.Empty(report.Warnings); // a stock build of shipped-quality content has nothing to warn about
        var caps = EngineCapabilities.Resolve(a);
        Assert.Equal(def.Cylinders, caps.Cylinders);
        Assert.Equal(def.Banks.Count, caps.Banks);
        var config = Sim(a, family).Config;
        Assert.Equal(def.Banks.Count, config.Banks.Count);
        Assert.Equal(def.Cylinders, config.Banks.Sum(b => b.Cylinders));
        Assert.Equal(caps.Turbochargers, config.Turbos.Count);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void StripsToTheBlockAndRebuildsToTheSameEngine(string family)
    {
        var a = Stock(family);
        var removed = new List<(string Slot, PartInstance Part)>();
        // Take off whatever can come off, outermost first, until nothing is left (the slot graph decides the order).
        while (a.Installed.Count > 0)
        {
            var slot = a.Definition.Slots.Last(s => a.IsInstalled(s.Id) && a.CanRemove(s.Id).Ok);
            Assert.True(a.Remove(slot.Id, out var p).Ok);
            removed.Add((slot.Id, p!));
        }
        Assert.Equal(a.Definition.Slots.Count(s => a.Definition.StockParts.ContainsKey(s.Id)), removed.Count);
        Assert.False(AssemblyValidator.Validate(a).CanRun);
        foreach (var slot in a.Definition.AssemblyOrder())
        {
            var part = removed.FirstOrDefault(r => r.Slot == slot.Id).Part;
            if (part != null) Assert.True(a.Install(slot.Id, part).Ok, slot.Id);
        }
        var rebuilt = SimFactory.At(Sim(a, family), 4000);
        var fresh = SimFactory.At(Sim(Stock(family), family), 4000);
        Assert.Equal(fresh.Torque, rebuilt.Torque);
        Assert.Equal(fresh.AirMassFlow, rebuilt.AirMassFlow);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void StartsIdlesAndPullsOnTheDyno(string family)
    {
        var tune = TuneOf(family);
        // A cold start on the starter motor, then idle on the ECU's idle control.
        var sim = Sim(Stock(family), family, tune, new EngineState());
        EngineTelemetry t = null!;
        for (int i = 0; i < 4000; i++) t = sim.Step(0.002, new EngineInputs { Starter = i < 750, Throttle = 0 });
        Assert.True(t.Running, $"{family} did not start");
        Assert.InRange(t.Rpm, 0.75 * tune.IdleRpm, 1.5 * tune.IdleRpm);

        var run = new DynoRunner(Sim(Stock(family), family), new DynoSettings()).RunToCompletion();
        Assert.Empty(run.Failures);
        var peakP = run.PeakPower!;
        var peakT = run.PeakTorque!;
        Assert.True(double.IsFinite(peakP.Power) && peakP.Power > 0);
        // Brake mean effective pressure of a road engine: 9–25 bar (naturally aspirated to boosted).
        Assert.InRange(Units.PaToBar(peakT.Bmep), 9, 25);
        // The calibrated VE table fuels the full-load curve on target.
        foreach (var s in run.Samples.Where(s => s.Firing && s.Throttle > 0.99 && s.Rpm > 2000))
            Assert.InRange(s.Lambda - s.TargetLambda, -0.06, 0.06);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void RespondsToItsTune(string family)
    {
        double rpm = 3000;
        var stock = SimFactory.At(Sim(Stock(family), family), rpm);
        var retarded = TuneOf(family);
        retarded.OffsetIgnition(-8);
        Assert.True(SimFactory.At(Sim(Stock(family), family, retarded), rpm).Torque < stock.Torque - 1, "retarding the spark map costs torque");
        var richer = TuneOf(family);
        for (int r = 0; r < richer.VolumetricEfficiency.Rows; r++)
            for (int c = 0; c < richer.VolumetricEfficiency.Columns; c++) richer.VolumetricEfficiency[r, c] *= 1.10;
        Assert.InRange(SimFactory.At(Sim(Stock(family), family, richer), rpm).Lambda / stock.Lambda, 1 / 1.10 - 0.01, 1 / 1.10 + 0.01);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void SavesAndLoadsWithItsBankParts(string family)
    {
        var scenario = Db.Scenarios.Values.Single(s => s.Engine == family && s.Vehicle.Length == 0).Id;
        var g = Garage.NewGame(Db, scenario);
        // Wear the last bank's head and change the tune: both must survive the round trip.
        var lastBank = g.Engine.Definition.Banks.Count - 1;
        var head = g.Engine.SlotFor(PartCategory.CylinderHead, lastBank)!;
        g.Engine.PartIn(head.Id)!.Wear = 0.37;
        g.Tune.OffsetIgnition(-2);
        var loaded = SaveSystem.Deserialize(SaveSystem.Serialize(g), Db);
        Assert.Equal(0.37, loaded.Engine.PartIn(head.Id)!.Wear);
        Assert.Equal(g.Tune.IgnitionAdvance.ToRows(), loaded.Tune.IgnitionAdvance.ToRows());
        Assert.Equal(g.Tune.ValveLiftSwitchRpm, loaded.Tune.ValveLiftSwitchRpm);
        Assert.Equal(g.Tune.IntakeRunnerSwitchRpm, loaded.Tune.IntakeRunnerSwitchRpm);
        var (a, _) = g.CreateSimulation();
        var (b, _) = loaded.CreateSimulation();
        Assert.Equal(SimFactory.At(a!, 3000).Torque, SimFactory.At(b!, 3000).Torque);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void WearsAndFailsLikeAnyEngineAndSaysWhere(string family)
    {
        var a = Stock(family);
        var sim = Sim(a, family);
        // Hard running wears the rings and bearings.
        SimFactory.At(sim, TuneOf(family).RevLimitRpm - 300, 1.0, 5.0);
        Assert.True(a.FindByCategory(PartCategory.Pistons)!.Wear > 0 && a.FindByCategory(PartCategory.RodBearings)!.Wear > 0);

        // Forced past valve float (a dyno motoring it beyond the limiter): the valvetrain gives up, and the report names the part.
        double floatRpm = sim.Config.Banks.Min(b => b.ValveFloatRpm());
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 1.25 * floatRpm, CoolantTemperatureOverride = 363.15 };
        for (int i = 0; i < 10_000 && sim.Damage.Failures.Count == 0; i++) sim.Step(0.002, input);
        var failure = Assert.Single(sim.Damage.Failures.Take(1));
        Assert.False(string.IsNullOrWhiteSpace(failure.ToText()));
        Assert.Contains(a.Definition.Slots, s => s.Id == failure.Slot || s.Label == failure.Slot);
        Assert.True(EngineDiagnostics.CompressionTestByBank(a).Count == a.Definition.Banks.Count);
        foreach (var part in a.AllParts) Assert.NotEmpty(PartInspector.Inspect(part).Select(f => f.Text).Append(PartInspector.WearText(part)));
    }

    // ---- Cars: any engine that fits ----------------------------------------------------------------------------------

    public static IEnumerable<object[]> CarScenarios =>
        TestContent.Matrix.Scenarios.Values.Where(s => s.Vehicle.Length > 0 && !TestContent.Database.Scenarios.ContainsKey(s.Id))
            .OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => new object[] { s.Id });

    [Theory]
    [MemberData(nameof(CarScenarios))]
    public void DrivesInACarItWasNotBuiltFor(string scenario)
    {
        var g = Garage.NewGame(Db, scenario);
        Assert.NotEqual(g.Chassis!.Definition.Engine, g.Engine.Definition.Id); // the car shipped with another engine
        var (car, problem) = g.CreateVehicleSimulation();
        Assert.True(car != null, problem);
        var session = new DrivingSession(car!, TrackLayout.TestFacility()) { AutopilotEnabled = true };
        while (session.Timer.Laps < 1 && car!.State.Time < 150) session.Advance(0.1, default);
        Assert.Equal(1, session.Timer.Laps);
        Assert.InRange(session.Timer.LastLap!.Value, 30, 120);
    }
}
