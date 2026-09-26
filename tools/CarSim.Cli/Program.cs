using CarSim.Core.Common;
using CarSim.Core.Content;
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
