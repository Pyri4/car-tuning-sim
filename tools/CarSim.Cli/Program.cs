using CarSim.Core.Common;
using CarSim.Core.Content;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Simulation;
using CarSim.Core.Engines;
using CarSim.Core.Parts;

namespace CarSim.Cli;

public static class Program
{
    private const string Usage = """
        carsim — Car Tuning Simulator command-line tools

        Usage:
          carsim validate [--content <dir>]            Load and validate all content.
          carsim inspect [<engine-id>] [--content <dir>]
                                                        Show the stock build, derived geometry and compatibility report.
          carsim sweep [<engine-id>] [--swap slot=part,...] [--fuel <id>] [--tune <id>] [--from 1000] [--to 8000] [--step 500]
                                                        Steady-state full-throttle dyno sweep of the stock build (with swaps).
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            var options = CliOptions.Parse(args.Skip(1).ToArray());
            return args[0] switch
            {
                "validate" => Validate(options),
                "inspect" => Inspect(options),
                "sweep" => Sweep(options),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or KeyNotFoundException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int Validate(CliOptions o)
    {
        var result = ContentLoader.LoadDirectory(o.ContentDir);
        var db = result.Database;
        Console.WriteLine($"Content: {o.ContentDir}");
        Console.WriteLine($"  {db.Parts.Count} parts, {db.Engines.Count} engines, {db.Fuels.Count} fuels, {db.Tunes.Count} tunes");
        if (result.Success)
        {
            Console.WriteLine("  OK — no errors.");
            return 0;
        }
        Console.WriteLine($"  {result.Errors.Count} error(s):");
        foreach (var e in result.Errors) Console.WriteLine($"    {e}");
        return 2;
    }

    private static int Sweep(CliOptions o)
    {
        var db = ContentLoader.LoadDirectory(o.ContentDir).GetOrThrow();
        var engine = db.GetEngine(o.Positional.FirstOrDefault() ?? db.Engines.Keys.First());
        var factory = new PartInstanceFactory();
        var assembly = EngineAssembly.CreateStock(engine, db, factory);
        if (o.Named.TryGetValue("swap", out var swaps))
        {
            foreach (var pair in swaps.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length != 2) throw new ArgumentException($"Bad --swap entry '{pair}', expected slot=part.");
                SwapPart(assembly, kv[0], db.GetPart(kv[1]), factory);
            }
        }
        if (o.Named.TryGetValue("add", out var adds))
        {
            foreach (var pair in adds.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length != 2) throw new ArgumentException($"Bad --add entry '{pair}', expected slot=part.");
                var r = assembly.Install(kv[0], factory.Create(db.GetPart(kv[1])));
                if (!r.Ok) throw new ArgumentException(r.Message);
            }
        }
        var fuel = db.GetFuel(o.Named.GetValueOrDefault("fuel", "gasoline_95"));
        var tune = EcuTune.FromDocument(db.GetTune(o.Named.GetValueOrDefault("tune", engine.StockTune)));
        var config = EngineConfiguration.Build(assembly, fuel, new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa));
        foreach (var issue in config.Report.Issues.Where(i => i.Severity != IssueSeverity.Info)) Console.WriteLine(issue);
        var sim = new EngineSimulation(config.GetOrThrow(), tune, EngineState.Warm());
        double from = double.Parse(o.Named.GetValueOrDefault("from", "1000"), System.Globalization.CultureInfo.InvariantCulture);
        double to = double.Parse(o.Named.GetValueOrDefault("to", "8000"), System.Globalization.CultureInfo.InvariantCulture);
        double step = double.Parse(o.Named.GetValueOrDefault("step", "500"), System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"{"rpm",6} {"Nm",6} {"kW",6} {"hp",6} {"MAPkPa",7} {"VE",5} {"λ",5} {"duty",5} {"adv",5} {"MBT",5} {"KLSA",5} {"knk",4} {"PCPbar",6} {"EGT°C",6} {"oil bar",7} {"FMEPbar",7} {"limit",6} {"port",6} {"exhBP",6} {"VEdyn",5} {"resid",5} {"PMEP",5} {"turbo krpm",10} {"PR",5} {"cEff",5} {"choke",5} {"WG",4} {"IAT°C",6}");
        foreach (var t in SteadyStateSweep.Run(sim, from, to, step))
        {
            Console.WriteLine($"{t.Rpm,6:F0} {t.Torque,6:F1} {t.PowerKw,6:F1} {t.PowerHp,6:F1} {t.MapKpa,7:F1} {t.VolumetricEfficiency,5:F2} {t.Lambda,5:F2} {t.InjectorDuty,5:F2} {t.IgnitionAdvance,5:F1} {t.MbtAdvance,5:F1} {t.KnockLimitAdvance,5:F1} {t.KnockIntensity,4:F1} {t.PeakCylinderPressureBar,6:F1} {t.EgtC,6:F0} {t.OilPressureBar,7:F2} {Units.PaToBar(t.Fmep),7:F2} {t.FuelLimit,6} {Units.PaToKpa(t.PortPressure),6:F1} {Units.PaToKpa(t.ExhaustBackPressure),6:F1} {t.VeDynamic,5:F2} {t.ResidualFactor,5:F3} {Units.PaToBar(t.Pmep),5:F2} {t.TurboRpm / 1000,10:F1} {t.CompressorPressureRatio,5:F2} {t.CompressorEfficiency,5:F2} {t.CompressorChokeRatio,5:F2} {t.WastegateOpening,4:F2} {Units.KToC(t.ChargeTemperature),6:F0}");
        }
        return 0;
    }

    private static void SwapPart(EngineAssembly a, string slot, PartDefinition part, PartInstanceFactory factory)
    {
        var removed = new Stack<(string, PartInstance)>();
        foreach (var s in a.RemovalSequenceFor(slot))
        {
            var r = a.Remove(s, out var p);
            if (!r.Ok) throw new ArgumentException(r.Message);
            removed.Push((s, p!));
        }
        if (a.IsInstalled(slot)) a.Remove(slot, out _);
        var ir = a.Install(slot, factory.Create(part));
        if (!ir.Ok) throw new ArgumentException(ir.Message);
        while (removed.Count > 0) { var (s, p) = removed.Pop(); a.Install(s, p); }
    }

    private static int Inspect(CliOptions o)
    {
        var db = ContentLoader.LoadDirectory(o.ContentDir).GetOrThrow();
        var engineId = o.Positional.FirstOrDefault() ?? db.Engines.Keys.First();
        var engine = db.GetEngine(engineId);
        var assembly = EngineAssembly.CreateStock(engine, db, new PartInstanceFactory());

        Console.WriteLine($"{engine.Name} [{engine.Id}]");
        Console.WriteLine();
        Console.WriteLine("Stock build (assembly order):");
        foreach (var slot in engine.AssemblyOrder())
        {
            var part = assembly.PartIn(slot.Id);
            Console.WriteLine($"  {slot.Label,-18} {(part == null ? "(empty)" : part.Definition.Name)}");
        }

        var g = EngineGeometry.TryCreate(assembly, out _);
        if (g != null)
        {
            Console.WriteLine();
            Console.WriteLine("Geometry:");
            Console.WriteLine($"  Displacement        {Units.M3ToCc(g.Displacement),8:F1} cc");
            Console.WriteLine($"  Bore × stroke       {Units.MToMm(g.Bore):F1} × {Units.MToMm(g.Stroke):F1} mm");
            Console.WriteLine($"  Rod ratio           {g.RodRatio,8:F3}");
            Console.WriteLine($"  Compression ratio   {g.CompressionRatio,8:F2} :1");
            Console.WriteLine($"  Deck clearance      {Math.Round(Units.MToMm(g.DeckClearance), 2) + 0.0,8:F2} mm");
            Console.WriteLine($"  Piston-to-head      {Units.MToMm(g.PistonToHeadClearance),8:F2} mm");
            Console.WriteLine($"  Recip. mass / cyl   {g.ReciprocatingMass * 1000,8:F0} g");
        }

        var report = AssemblyValidator.Validate(assembly);
        Console.WriteLine();
        Console.WriteLine($"Compatibility: {(report.CanRun ? "can run" : "CANNOT RUN")}");
        foreach (var issue in report.Issues) Console.WriteLine($"  {issue}");
        Console.WriteLine();
        Console.WriteLine($"Total engine mass: {assembly.TotalMassKg:F1} kg");
        return 0;
    }
}

/// <summary>Minimal argument parsing: <c>--content dir</c> plus positional arguments.</summary>
public sealed class CliOptions
{
    public string ContentDir { get; private set; } = DefaultContentDir();
    public List<string> Positional { get; } = new();
    public Dictionary<string, string> Named { get; } = new(StringComparer.Ordinal);

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Option {args[i]} needs a value.");
                string key = args[i][2..];
                string value = args[++i];
                if (key == "content") o.ContentDir = value;
                else o.Named[key] = value;
            }
            else o.Positional.Add(args[i]);
        }
        return o;
    }

    /// <summary>Finds content/base by walking up from the working directory, then from the executable.</summary>
    private static string DefaultContentDir()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "content", "base");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "content", "base");
    }
}
