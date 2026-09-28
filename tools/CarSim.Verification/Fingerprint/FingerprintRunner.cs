using System.Globalization;
using System.Text;

namespace CarSim.Verification.Fingerprint;

/// <summary>Runs fingerprint cases (in parallel: each case builds its own simulations) and compares full dumps.</summary>
public static class FingerprintRunner
{
    /// <param name="cases">Cases to run, in the order results are returned.</param>
    /// <param name="schema">Channels to digest (a baseline's schema); null digests every channel.</param>
    /// <param name="dumpDirectory">When set, every case writes its full-precision values to <c>&lt;dir&gt;/&lt;case&gt;.txt</c>.</param>
    /// <param name="repoRoot">Repository whose content is used; defaults to <see cref="RepoPaths.Root"/>.</param>
    public static IReadOnlyList<CaseRecord> Run(IReadOnlyList<FingerprintCase> cases, IReadOnlySet<string>? schema = null,
        string? dumpDirectory = null, string? repoRoot = null, int? parallelism = null)
    {
        var content = new FingerprintContent(repoRoot ?? RepoPaths.Root);
        if (dumpDirectory != null) Directory.CreateDirectory(dumpDirectory);
        var results = new CaseRecord[cases.Count];
        Parallel.For(0, cases.Count, new ParallelOptions { MaxDegreeOfParallelism = parallelism ?? Environment.ProcessorCount }, i =>
            results[i] = RunCase(cases[i], content, schema, dumpDirectory));
        return results;
    }

    public static CaseRecord RunCase(FingerprintCase c, FingerprintContent content, IReadOnlySet<string>? schema, string? dumpDirectory = null)
    {
        using var dump = dumpDirectory == null ? null : new StreamWriter(Path.Combine(dumpDirectory, c.Id + ".txt"), false, new UTF8Encoding(false));
        var recorder = new FingerprintRecorder(c.Id, schema, dump);
        c.Run(content, recorder);
        return recorder.Finish();
    }

    /// <summary>
    /// Key-by-key comparison of two dump directories (two commits' <c>carsim fingerprint --dump</c>): values of keys both
    /// have must be equal; keys only one side has are listed by channel. The deep-diagnosis companion of the baseline.
    /// </summary>
    public static string DiffDumps(string before, string after, int maxShown = 40)
    {
        var sb = new StringBuilder();
        long compared = 0, differing = 0;
        var onlyBefore = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var onlyAfter = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(before, "*.txt").Select(Path.GetFileName)
            .Union(Directory.EnumerateFiles(after, "*.txt").Select(Path.GetFileName)).OfType<string>().OrderBy(f => f, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var a = Load(Path.Combine(before, file));
            var b = Load(Path.Combine(after, file));
            int shown = 0, fileDiffs = 0;
            foreach (var (key, value) in a)
            {
                if (!b.TryGetValue(key, out var other)) { Count(onlyBefore, key); continue; }
                compared++;
                if (other == value) continue;
                differing++;
                fileDiffs++;
                if (shown++ < maxShown) sb.Append("  ").Append(file).Append(' ').Append(key).Append(": ").Append(value).Append(" → ").AppendLine(other);
            }
            foreach (var key in b.Keys.Where(k => !a.ContainsKey(k))) Count(onlyAfter, key);
            if (fileDiffs > shown) sb.AppendLine(CultureInfo.InvariantCulture, $"  {file}: … {fileDiffs - shown} more differing values");
        }
        var head = new StringBuilder();
        head.AppendLine(CultureInfo.InvariantCulture, $"{compared} values compared, {differing} differ.");
        if (onlyBefore.Count > 0) head.AppendLine("Channels only before: " + string.Join(", ", onlyBefore.Select(kv => $"{kv.Key} ({kv.Value})")));
        if (onlyAfter.Count > 0) head.AppendLine("Channels only after: " + string.Join(", ", onlyAfter.Select(kv => $"{kv.Key} ({kv.Value})")));
        return head.Append(sb).ToString();

        static void Count(SortedDictionary<string, int> into, string key)
        {
            string channel = TelemetryFlattener.Channel(key[(key.IndexOf('.') + 1)..]);
            into[channel] = into.GetValueOrDefault(channel) + 1;
        }
    }

    private static Dictionary<string, string> Load(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq];
            int n = occurrences[key] = occurrences.GetValueOrDefault(key) + 1;
            map[n == 1 ? key : $"{key}#{n}"] = line[(eq + 1)..];
        }
        return map;
    }
}
