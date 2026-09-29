using CarSim.Core.Content;
using CarSim.Verification;
using CarSim.Verification.Calibration;

namespace CarSim.Cli;

/// <summary>
/// <c>carsim generate-tune</c> (Engine Authoring Factory 1.0, Phase 3): a deterministic baseline tune for an engine's stock
/// build from the existing calibrators (<see cref="TuneGenerator"/>). The report goes to stdout and is deterministic; the
/// elapsed time goes to stderr.
/// </summary>
internal static class GenerateTuneCommand
{
    public const string Usage = """
          carsim generate-tune <engine-id> --fuel <fuel-id> [--policy <tune-id>] [--id <tune-id>] [--out <file.json>] [--check 1]
                               [--strict 1] [--verbose 1] [--manifest <file>]
                                                        Deterministic baseline tune for the engine's resolved stock build: runs
                                                        check-engine (errors refuse; --strict: warnings too), sets the ECU's
                                                        beliefs from the build and the explicit fuel, keeps the hand-authored
                                                        policy (λ, rev limit, idle, axes, boost) of --policy (default: the stock
                                                        tune), runs the manifest's recipe for it (else one derived from the
                                                        hardware) with the dev calibrators until it settles, validates the result.
                                                        --out writes the tune (content format) and a .recipe.jsonc record beside
                                                        it, never over a file it did not write nor over another engine's, fuel's,
                                                        tune id's or policy's output; --check 1 compares instead. --manifest (or
                                                        the repository's) must load; with none found the report warns and every
                                                        calibratable table is calibrated.
                                                        Exit 0 = generated (written or unchanged); 1 = usage or manifest error;
                                                        2 = refused; 4 = --check differs.
        """;

    public static int Run(CliOptions o, string modsDir)
    {
        if (o.Positional.FirstOrDefault() is not { } engineId)
        {
            Console.Error.WriteLine("generate-tune needs an engine id (carsim generate-tune <engine-id> --fuel <fuel-id>).");
            return 1;
        }
        bool check = o.Named.GetValueOrDefault("check") is "1" or "true";
        string? outPath = o.Named.GetValueOrDefault("out");
        if (check && outPath == null)
        {
            Console.Error.WriteLine("--check 1 compares with an existing --out <file.json>; name one.");
            return 1;
        }
        var load = ContentLoader.LoadWithMods(o.ContentDir, modsDir);
        if (!o.Named.TryGetValue("fuel", out var fuel))
        {
            Console.Error.WriteLine("generate-tune needs --fuel <fuel-id>: the calibration fuel is never chosen by default. Loaded: " +
                                    string.Join(", ", load.Database.Fuels.Keys.OrderBy(k => k, StringComparer.Ordinal)) + ".");
            return 1;
        }
        // The manifest says which tables are hand-authored: one named with --manifest (or found in the repository) must load,
        // or nothing is generated; none found is reported (manifest: none) and warned about.
        var lookup = TuneGenerator.FindManifest(o.Named.GetValueOrDefault("manifest"), DefaultManifest());
        if (lookup.Error != null)
        {
            Console.Error.WriteLine(lookup.Error);
            return 1;
        }
        Console.Error.WriteLine(lookup.Path != null ? $"manifest: {lookup.Path}" : "manifest: none found");
        var manifest = lookup.Manifest;
        var request = new GenerationRequest(engineId, fuel, o.Named.GetValueOrDefault("policy"), o.Named.GetValueOrDefault("id"),
            Strict: o.Named.GetValueOrDefault("strict") is "1" or "true");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var generation = TuneGenerator.Generate(load, request, manifest);
        Console.Write(generation.ToText(verbose: o.Named.GetValueOrDefault("verbose") is "1" or "true"));
        Console.Error.WriteLine($"({watch.Elapsed.TotalSeconds:F0} s)");
        if (generation.Refused) return 2;
        if (outPath == null)
        {
            Console.WriteLine("OUTPUT  not written (dry run): --out <file.json> writes the tune and its record.");
            return 0;
        }
        var outcome = TuneGenerator.Write(generation, outPath, check);
        Console.WriteLine("OUTPUT  " + outcome.Message);
        return outcome.ExitCode;
    }

    /// <summary>The repository's tune manifest path when the CLI runs inside the repository; none elsewhere.</summary>
    private static string? DefaultManifest()
    {
        try { return RepoPaths.TuneManifest; }
        catch (InvalidOperationException) { return null; }
    }
}
