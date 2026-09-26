using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests;

/// <summary>Builds engine simulations from the base content with optional part swaps and tune edits.</summary>
public static class SimFactory
{
    public static EngineAssembly Assembly(params (string slot, string part)[] swaps)
    {
        var a = TestContent.StockK20();
        foreach (var (slot, part) in swaps) TestContent.Swap(a, slot, part);
        return a;
    }

    /// <summary>A stock assembly of <paramref name="engineId"/> with swaps applied.</summary>
    public static EngineAssembly AssemblyOf(string engineId, params (string slot, string part)[] swaps)
    {
        var a = TestContent.Stock(engineId);
        foreach (var (slot, part) in swaps) TestContent.Swap(a, slot, part);
        return a;
    }

    public static EcuTune StockTune() => StockTuneOf(TestContent.K20);

    /// <summary>The factory calibration of an engine family.</summary>
    public static EcuTune StockTuneOf(string engineId) =>
        EcuTune.FromDocument(TestContent.Database.GetTune(TestContent.Database.GetEngine(engineId).StockTune));

    /// <summary>A warm engine on the dyno; without a tune it runs its own family's factory calibration.</summary>
    public static EngineSimulation Create(EngineAssembly assembly, string fuel = "gasoline_95", EcuTune? tune = null, EngineState? state = null)
    {
        tune ??= StockTuneOf(assembly.Definition.Id);
        var config = EngineConfiguration.Build(assembly, TestContent.Database.GetFuel(fuel), new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa)).GetOrThrow();
        return new EngineSimulation(config, tune, state ?? EngineState.Warm());
    }

    public static EngineSimulation Create(params (string slot, string part)[] swaps) => Create(Assembly(swaps));

    public static EngineTelemetry At(EngineSimulation sim, double rpm, double throttle = 1.0, double seconds = 1.0) =>
        SteadyStateSweep.Settle(sim, rpm, throttle, seconds);

    public static IReadOnlyList<EngineTelemetry> Sweep(EngineSimulation sim, double from = 1000, double to = 7500, double step = 500) =>
        SteadyStateSweep.Run(sim, from, to, step, settleSeconds: 1.0);

    /// <summary>
    /// A copy of the tune with more rows on its load axis, each repeating the top row — what a tuner does
    /// before mapping more boost than the base calibration covers.
    /// </summary>
    public static EcuTune ExtendLoadAxis(EcuTune tune, params double[] extraKpa)
    {
        var d = tune.ToDocument();
        double[][] Extend(double[][] rows) => rows.Concat(extraKpa.Select(_ => rows[^1].ToArray())).ToArray();
        return EcuTune.FromDocument(new CarSim.Core.Content.TuneDocument
        {
            Id = d.Id, Name = d.Name, RpmAxis = d.RpmAxis, LoadAxisKpa = d.LoadAxisKpa.Concat(extraKpa).ToArray(),
            TargetLambda = Extend(d.TargetLambda), IgnitionAdvanceDeg = Extend(d.IgnitionAdvanceDeg),
            VolumetricEfficiency = Extend(d.VolumetricEfficiency!), DisplacementCc = d.DisplacementCc,
            BoostTargetKpa = d.BoostTargetKpa, IntakeCamAdvanceDeg = d.IntakeCamAdvanceDeg == null ? null : Extend(d.IntakeCamAdvanceDeg),
            RevLimitRpm = d.RevLimitRpm, IdleRpm = d.IdleRpm,
            KnockControlEnabled = d.KnockControlEnabled, InjectorFlowCcMin = d.InjectorFlowCcMin, FuelStoichAfr = d.FuelStoichAfr,
            InjectorDeadTimeMs = d.InjectorDeadTimeMs, FuelDensityKgL = d.FuelDensityKgL,
        });
    }

    /// <summary>The tune with its VE table measured on this engine (the dyno session after a build).</summary>
    public static EcuTune WithCalibratedVe(EngineAssembly assembly, string fuel, EcuTune tune, double holdSeconds = 1.0)
    {
        var table = VeCalibrator.Calibrate(Create(assembly, fuel, tune), holdSeconds);
        var calibrated = tune.Clone();
        for (int r = 0; r < table.Length; r++)
            for (int c = 0; c < table[r].Length; c++) calibrated.VolumetricEfficiency[r, c] = table[r][c];
        return calibrated;
    }

    public static EngineTelemetry PeakPower(IEnumerable<EngineTelemetry> points) => points.MaxBy(p => p.Power)!;
    public static EngineTelemetry PeakTorque(IEnumerable<EngineTelemetry> points) => points.MaxBy(p => p.Torque)!;
}
