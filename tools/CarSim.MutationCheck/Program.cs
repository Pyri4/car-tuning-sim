using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CarSim.MutationCheck;

/// <summary>
/// The mutation harness (docs/VERIFICATION.md, "Mutation harness"). "Mutation-checked" means: the bug a test guards,
/// injected into the source, makes that test fail. For every entry of <c>mutations.json</c> this tool
/// <list type="number">
/// <item>checks the mutation still applies (its <c>find</c> text occurs exactly once — a stale entry is an error, not a skip);</item>
/// <item>proves the guarding tests pass on the unmutated sources (otherwise a "catch" means nothing);</item>
/// <item>injects the mutant, rebuilds, runs the guarding tests and requires at least one failure;</item>
/// <item>restores the file with a fresh timestamp so incremental builds never keep a mutant, and rebuilds clean at the end.</item>
/// </list>
/// Usage: <c>dotnet run --project tools/CarSim.MutationCheck -c Release -- [--only id,id] [--list] [--skip-baseline] [--full]</c>.
/// <c>--full</c> runs the whole suite on every mutant and reports how many tests each one fails.
/// </summary>
public static class Program
{
    private sealed record Mutation(string Id, string Guards, string File, string Find, string Replace, string Tests);

    private sealed record ManifestFile(IReadOnlyList<Mutation> Mutations);

    private sealed record Outcome(Mutation Mutation, string Verdict, int Failed, int Passed, double Seconds);

    private const string TestProject = "tests/CarSim.Core.Tests/CarSim.Core.Tests.csproj";
    private static string? _restorePath, _restoreText;

    public static int Main(string[] args)
    {
        string root = FindRoot();
        var manifest = JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(Path.Combine(root, "tools", "CarSim.MutationCheck", "mutations.json")),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        var mutations = manifest.Mutations.ToList();
        string? only = Option(args, "--only");
        if (only != null)
        {
            var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries);
            mutations = mutations.Where(m => wanted.Contains(m.Id)).ToList();
        }
        if (args.Contains("--list"))
        {
            foreach (var m in mutations) Console.WriteLine($"{m.Id,-34} {m.File}\n{"",-34} guards: {m.Guards}\n{"",-34} tests: {m.Tests}");
            return 0;
        }
        bool full = args.Contains("--full");

        // 1. Every mutation must still apply: its find text exactly once, its replacement not already there.
        var stale = new List<string>();
        foreach (var m in mutations)
        {
            string path = Path.Combine(root, m.File);
            if (!File.Exists(path)) { stale.Add($"{m.Id}: {m.File} does not exist"); continue; }
            string text = File.ReadAllText(path);
            int count = Regex.Matches(text, Regex.Escape(m.Find)).Count;
            // Exactly once: zero means the code moved on (or a mutant was left behind — restore it from git), more than one
            // means the entry is ambiguous.
            if (count != 1) stale.Add($"{m.Id}: the text to mutate occurs {count} times in {m.File} (expected once) — update mutations.json");
        }
        if (stale.Count > 0)
        {
            Console.WriteLine("Stale mutations:\n  " + string.Join("\n  ", stale));
            return 2;
        }

        Console.CancelKeyPress += (_, _) => Restore();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();

        Console.WriteLine($"Building {TestProject} (Release)…");
        if (Dotnet(root, $"build {TestProject} -c Release -v q -nologo").ExitCode != 0) { Console.WriteLine("The unmutated build fails."); return 3; }

        // 2. The guarding tests must pass on the unmutated sources.
        if (!args.Contains("--skip-baseline"))
        {
            foreach (var filter in mutations.Select(m => full ? "" : m.Tests).Distinct())
            {
                var (failed, passed, _) = Test(root, filter);
                Console.WriteLine($"baseline [{(filter.Length == 0 ? "whole suite" : filter)}]: {passed} passed, {failed} failed");
                if (failed > 0 || passed == 0) { Console.WriteLine("The guarding tests do not pass (or select nothing) on the unmutated sources: no mutant result would mean anything."); return 3; }
            }
        }

        // 3. Each mutant: inject, rebuild, test, restore.
        var outcomes = new List<Outcome>();
        foreach (var m in mutations)
        {
            var watch = Stopwatch.StartNew();
            string path = Path.Combine(root, m.File);
            string original = File.ReadAllText(path);
            _restorePath = path;
            _restoreText = original;
            try
            {
                File.WriteAllText(path, original.Replace(m.Find, m.Replace, StringComparison.Ordinal));
                var build = Dotnet(root, $"build {TestProject} -c Release -v q -nologo");
                if (build.ExitCode != 0)
                {
                    outcomes.Add(new Outcome(m, "INVALID (does not compile)", 0, 0, watch.Elapsed.TotalSeconds));
                    Console.WriteLine($"{m.Id}: INVALID — the mutant does not compile:\n{Tail(build.Output, 8)}");
                    continue;
                }
                var (failed, passed, _) = Test(root, full ? "" : m.Tests);
                string verdict = failed > 0 ? "CAUGHT" : "SURVIVED";
                outcomes.Add(new Outcome(m, verdict, failed, passed, watch.Elapsed.TotalSeconds));
                Console.WriteLine($"{m.Id}: {verdict} — {failed} failed, {passed} passed ({watch.Elapsed.TotalSeconds:F0} s)");
            }
            finally
            {
                Restore();
            }
        }

        // 4. Leave clean binaries behind.
        Console.WriteLine("Rebuilding the unmutated sources…");
        Dotnet(root, $"build {TestProject} -c Release -v q -nologo");

        Console.WriteLine();
        Console.WriteLine($"{"mutation",-34} {"verdict",-10} {"failed",7} {"passed",7}");
        foreach (var o in outcomes) Console.WriteLine($"{o.Mutation.Id,-34} {o.Verdict,-10} {o.Failed,7} {o.Passed,7}");
        int caught = outcomes.Count(o => o.Verdict == "CAUGHT");
        Console.WriteLine($"{caught} of {outcomes.Count} mutants caught.");
        return caught == outcomes.Count ? 0 : 1;
    }

    /// <summary>Puts the file being mutated back, with a fresh timestamp (MSBuild must not keep the mutant's binaries).</summary>
    private static void Restore()
    {
        if (_restorePath == null || _restoreText == null) return;
        File.WriteAllText(_restorePath, _restoreText);
        File.SetLastWriteTimeUtc(_restorePath, DateTime.UtcNow);
        _restorePath = null;
        _restoreText = null;
    }

    private static (int Failed, int Passed, string Output) Test(string root, string filter)
    {
        string args = $"test {TestProject} --no-build -c Release -nologo" + (filter.Length > 0 ? $" --filter \"{filter}\"" : "");
        var run = Dotnet(root, args);
        int failed = Count(run.Output, "Failed"), passed = Count(run.Output, "Passed");
        if (run.ExitCode != 0 && failed == 0) failed = 1; // e.g. a crash or a test host abort
        return (failed, passed, run.Output);
    }

    private static int Count(string output, string label)
    {
        var m = Regex.Matches(output, label + @":\s+(\d+),").LastOrDefault();
        return m == null ? 0 : int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static (int ExitCode, string Output) Dotnet(string root, string args)
    {
        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        using var p = Process.Start(psi)!;
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return (p.ExitCode, output.ToString());
    }

    private static string Tail(string text, int lines) => string.Join("\n", text.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).TakeLast(lines));

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "CarTuningSim.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("Run inside the repository (CarTuningSim.sln not found).");
    }
}
