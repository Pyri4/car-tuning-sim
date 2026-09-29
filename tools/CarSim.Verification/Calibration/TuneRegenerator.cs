using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CarSim.Core.Content;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;

namespace CarSim.Verification.Calibration;

/// <summary><c>tools/CarSim.Verification/tune-manifest.json</c>: the recipe of every shipped and test tune.</summary>
public sealed record TuneManifest(IReadOnlyList<TuneRecipe> Tunes)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static TuneManifest Load(string path) =>
        JsonSerializer.Deserialize<TuneManifest>(File.ReadAllText(path), Options) ?? throw new InvalidDataException($"Empty manifest {path}.");
}

/// <summary>
/// How a tune is (re)made. <paramref name="Generated"/> is set only on the record <c>carsim generate-tune</c> writes beside a
/// generated tune (<see cref="TuneGenerator"/>); the hand-maintained manifest's recipes have none.
/// </summary>
public sealed record TuneRecipe(string Tune, string Engine, string Layer, string File, string Fuel, BuildRecipe? Build,
    IReadOnlyList<CalibrationStep> Steps, IReadOnlyList<string> HandAuthored, string Provenance, GenerationRecord? Generated = null);

/// <summary>Parts swapped into or added to the engine's stock build, as <c>[slot, part]</c> pairs.</summary>
public sealed record BuildRecipe(IReadOnlyList<string[]>? Swap, IReadOnlyList<string[]>? Add);

public sealed record CalibrationStep(string Calibrator, double? StepDeg = null, double? HoldS = null, double? KnockMarginDeg = null,
    double? MbtMarginDeg = null);

/// <summary>
/// One regenerated field against what is checked in. <paramref name="Oscillating"/>: cells the recipe moves on one pass and
/// moves back on the next (a rounding 2-cycle between two recipe-consistent states), kept at their checked-in value and not
/// counted as differing. <paramref name="Unsettled"/>: cells that moved again, to a third value, on the second pass.
/// </summary>
public sealed record FieldRegeneration(string Field, double[][] CheckedIn, double[][] Regenerated, int Oscillating = 0, int Unsettled = 0)
{
    public int Cells => CheckedIn.Sum(r => r.Length);

    public int Differing => CheckedIn.Zip(Regenerated).Sum(rows => rows.First.Zip(rows.Second).Count(v => !v.First.Equals(v.Second)));

    public double MaxAbsDifference => CheckedIn.Zip(Regenerated)
        .SelectMany(rows => rows.First.Zip(rows.Second).Where(v => !v.First.Equals(v.Second)).Select(v => Math.Abs(v.First - v.Second)))
        .DefaultIfEmpty(0).Max();

    public bool Reproduced => Differing == 0;
}

/// <summary>A tune after its recipe ran on it (<see cref="TuneRegenerator.Settle"/>): the settled values, field by field.</summary>
public sealed record SettledTune(TuneDocument Document, IReadOnlyList<FieldRegeneration> Fields)
{
    /// <summary>Every calibrated value came back unchanged (or in a rounding 2-cycle): the starting tune is a fixed point.</summary>
    public bool Reproduced => Fields.All(f => f.Reproduced);
}

public sealed record TuneRegeneration(TuneRecipe Recipe, TuneDocument Regenerated, IReadOnlyList<FieldRegeneration> Fields,
    IReadOnlyList<string> Audit, bool FileRoundTrips, double Seconds)
{
    public bool Reproduced => Fields.All(f => f.Reproduced);
}

/// <summary>
/// The recalibration driver (docs/VERIFICATION.md, "Tune regeneration"): runs a tune's recipe with the existing dev
/// calibrators — each step on a fresh engine, with the tables the earlier steps produced, rounded through the tune file's
/// number format exactly as pasting <c>carsim calibrate-*</c> output does — and compares or writes the result. It never
/// touches a table the manifest lists as hand-authored; those are audited instead.
/// </summary>
public static class TuneRegenerator
{
    public const string VolumetricEfficiency = "volumetric_efficiency", IgnitionAdvance = "ignition_advance_deg",
        IntakeCamAdvance = "intake_cam_advance_deg", RunnerSwitch = "intake_runner_switch_rpm", LiftSwitch = "valve_lift_switch_rpm";

    /// <summary>Written with <see cref="RunnerSwitch"/>: the switch speeds of the third and later stages of an intake with more than two.</summary>
    public const string RunnerUpperSwitch = "intake_runner_upper_switch_rpm";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Loads the content a recipe needs: the base game, plus the test layers for synthetic engines.</summary>
    public static ContentDatabase Content(TuneRecipe recipe, string repoRoot)
    {
        string baseDir = Path.Combine(repoRoot, "content", "base");
        return recipe.Layer == "test"
            ? ContentLoader.LoadWithMods(baseDir, Path.Combine(repoRoot, "content", "test")).GetOrThrow()
            : ContentLoader.LoadDirectory(baseDir).GetOrThrow();
    }

    /// <summary>
    /// Runs the recipe on the checked-in tune. Where that changes anything, the recipe runs a second time on its own output:
    /// a cell that goes back to its checked-in value is a rounding 2-cycle (e.g. spark ↔ VE across a 0.5° step), whose two
    /// states are both what the recipe produces, so it keeps the checked-in one — a regeneration only writes values that
    /// settle.
    /// </summary>
    public static TuneRegeneration Run(TuneRecipe recipe, string repoRoot)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var db = Content(recipe, repoRoot);
        var settled = Settle(db, recipe, db.GetTune(recipe.Tune));
        var doc = settled.Document;

        var audit = new List<string>();
        if (recipe.HandAuthored.Contains(IgnitionAdvance)) audit.AddRange(AuditSpark(db, recipe, doc));

        return new TuneRegeneration(recipe, doc, settled.Fields, audit, FileRoundTrips(recipe, repoRoot, db), watch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// The driver's core, shared by <see cref="Run"/> and the tune generator: the recipe's steps on <paramref name="start"/>
    /// (with <paramref name="db"/>'s engine, the recipe's build and fuel); where that changes anything, a second pass on its own
    /// output, and every cell that returns to its starting value (a rounding 2-cycle) keeps it. <see cref="SettledTune.Reproduced"/>
    /// is what <c>carsim regenerate-tunes</c> reports: <paramref name="start"/> is a fixed point of its recipe.
    /// </summary>
    public static SettledTune Settle(ContentDatabase db, TuneRecipe recipe, TuneDocument start)
    {
        var names = CalibratedFields(recipe);
        var doc = RunSteps(db, recipe, start);
        var again = names.Any(f => !SameValues(Values(start, f), Values(doc, f))) ? RunSteps(db, recipe, doc) : doc;
        var fields = new List<FieldRegeneration>();
        foreach (var field in names)
        {
            var kept = SettleTwoCycles(Values(start, field), Values(doc, field), Values(again, field), out int oscillating, out int unsettled);
            doc = WithValues(doc, field, kept);
            fields.Add(new FieldRegeneration(field, Values(start, field), kept, oscillating, unsettled));
        }
        return new SettledTune(doc, fields);
    }

    /// <summary>The fields a recipe's steps write, in step order; a field also listed as hand-authored is a manifest error.</summary>
    public static IReadOnlyList<string> CalibratedFields(TuneRecipe recipe)
    {
        var names = recipe.Steps.Select(s => FieldOf(s.Calibrator)).Distinct().ToList();
        foreach (var field in names)
            if (recipe.HandAuthored.Contains(field)) throw new InvalidDataException($"{recipe.Tune}: '{field}' is listed as hand-authored and calibrated.");
        return names;
    }

    /// <summary>
    /// The first pass's values, except cells the second pass returned to their checked-in value (a 2-cycle), which keep
    /// it. <paramref name="unsettled"/> counts cells the second pass moved to a third value (they keep the first pass's).
    /// </summary>
    public static double[][] SettleTwoCycles(double[][] checkedIn, double[][] first, double[][] second, out int oscillating, out int unsettled)
    {
        int osc = 0, open = 0;
        var result = first.Select((row, r) => row.Select((v, c) =>
        {
            if (v.Equals(checkedIn[r][c])) return v;
            if (second[r][c].Equals(checkedIn[r][c])) { osc++; return checkedIn[r][c]; }
            if (!second[r][c].Equals(v)) open++;
            return v;
        }).ToArray()).ToArray();
        oscillating = osc;
        unsettled = open;
        return result;
    }

    private static bool SameValues(double[][] a, double[][] b) => a.Zip(b).All(rows => rows.First.SequenceEqual(rows.Second));

    private static TuneDocument WithValues(TuneDocument d, string field, double[][] v) => field switch
    {
        VolumetricEfficiency => d with { VolumetricEfficiency = v },
        IgnitionAdvance => d with { IgnitionAdvanceDeg = v },
        IntakeCamAdvance => d with { IntakeCamAdvanceDeg = v },
        RunnerSwitch => d with
        {
            IntakeRunnerSwitchRpm = double.IsNaN(v[0][0]) ? null : v[0][0],
            IntakeRunnerUpperSwitchRpm = v[0].Length > 1 ? v[0][1..] : d.IntakeRunnerUpperSwitchRpm,
        },
        LiftSwitch => d with { ValveLiftSwitchRpm = double.IsNaN(v[0][0]) ? null : v[0][0] },
        _ => throw new InvalidDataException($"Unknown field '{field}'."),
    };

    /// <summary>The recipe's steps in order, each on a fresh engine with the tables the earlier steps left.</summary>
    private static TuneDocument RunSteps(ContentDatabase db, TuneRecipe recipe, TuneDocument start)
    {
        var doc = start;
        foreach (var step in recipe.Steps)
        {
            switch (step.Calibrator)
            {
                case "cams":
                    doc = doc with { IntakeCamAdvanceDeg = ThroughFile(CamPhaseCalibrator.Calibrate(Sim(db, recipe, doc), step.StepDeg ?? 5.0, step.HoldS ?? 0.8), IntakeCamAdvance) };
                    break;
                case "ve":
                    doc = doc with { VolumetricEfficiency = ThroughFile(VeCalibrator.Calibrate(Sim(db, recipe, doc), step.HoldS ?? 1.0), VolumetricEfficiency) };
                    break;
                case "spark":
                    doc = doc with
                    {
                        IgnitionAdvanceDeg = ThroughFile(SparkCalibrator.Calibrate(Sim(db, recipe, doc), step.KnockMarginDeg ?? 1.5, step.MbtMarginDeg ?? 1.0,
                            step.HoldS ?? 1.0), IgnitionAdvance),
                    };
                    break;
                case "runner_switch":
                    doc = RunnerSwitchSpeeds(db, recipe, doc);
                    break;
                case "lift_switch":
                    doc = doc with { ValveLiftSwitchRpm = Crossover(db, recipe, doc, lift: true) };
                    break;
                default:
                    throw new InvalidDataException($"{recipe.Tune}: unknown calibrator '{step.Calibrator}'.");
            }
        }
        return doc;
    }

    /// <summary>Whether writing the checked-in values back leaves the tune file byte-identical (the writer keeps its format).</summary>
    public static bool FileRoundTrips(TuneRecipe recipe, string repoRoot, ContentDatabase? db = null)
    {
        db ??= Content(recipe, repoRoot);
        string text = File.ReadAllText(Path.Combine(repoRoot, recipe.File));
        return Write(text, recipe.Tune, db.GetTune(recipe.Tune), recipe.Steps.Select(s => FieldOf(s.Calibrator)).Distinct()) == text;
    }

    /// <summary>The field a calibrator writes.</summary>
    public static string FieldOf(string calibrator) => calibrator switch
    {
        "cams" => IntakeCamAdvance,
        "ve" => VolumetricEfficiency,
        "spark" => IgnitionAdvance,
        "runner_switch" => RunnerSwitch,
        "lift_switch" => LiftSwitch,
        _ => throw new InvalidDataException($"Unknown calibrator '{calibrator}'."),
    };

    /// <summary>The tune file's text with <paramref name="fieldNames"/> of tune <paramref name="tuneId"/> replaced by <paramref name="values"/>' values.</summary>
    public static string Write(string text, string tuneId, TuneDocument values, IEnumerable<string> fieldNames)
    {
        foreach (var field in fieldNames)
        {
            var (start, end) = TuneObject(text, tuneId);
            text = field is RunnerSwitch or LiftSwitch
                ? ReplaceScalar(text, start, end, field, Values(values, field)[0][0])
                : ReplaceTable(text, start, end, field, Values(values, field));
            if (field == RunnerSwitch && values.IntakeRunnerUpperSwitchRpm is { } upper)
            {
                (start, end) = TuneObject(text, tuneId);
                text = ReplaceList(text, start, end, RunnerUpperSwitch, upper);
            }
        }
        return text;
    }

    // ---- Calibration helpers ------------------------------------------------------------------------------------------

    /// <summary>The build a recipe calibrates: the engine's resolved stock assembly with the recipe's swaps and additions.</summary>
    public static EngineAssembly BuildAssembly(ContentDatabase db, TuneRecipe recipe)
    {
        var assembly = EngineAssembly.CreateStock(db.GetEngine(recipe.Engine), db, new PartInstanceFactory());
        var factory = new PartInstanceFactory(1_000_000);
        foreach (var pair in recipe.Build?.Swap ?? Array.Empty<string[]>()) Swap(assembly, pair[0], db.GetPart(pair[1]), factory);
        foreach (var pair in recipe.Build?.Add ?? Array.Empty<string[]>()) Check(assembly.Install(pair[0], factory.Create(db.GetPart(pair[1]))));
        return assembly;
    }

    private static EngineSimulation Sim(ContentDatabase db, TuneRecipe recipe, TuneDocument doc)
    {
        var assembly = BuildAssembly(db, recipe);
        var tune = EcuTune.FromDocument(doc);
        var config = EngineConfiguration.Build(assembly, db.GetFuel(recipe.Fuel), new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa)).GetOrThrow();
        return new EngineSimulation(config, tune, EngineState.Warm());
    }

    /// <summary>
    /// The switch speed of every stage of a variable intake: stage k's is the crossover of stages k − 1 and k, each held
    /// (<see cref="Crossover"/>). A two-stage intake has one (<c>intake_runner_switch_rpm</c>); the third and later stages'
    /// go to <c>intake_runner_upper_switch_rpm</c>, which the tune file must already have (a placeholder list is enough).
    /// </summary>
    public static TuneDocument RunnerSwitchSpeeds(ContentDatabase db, TuneRecipe recipe, TuneDocument doc)
    {
        int stages = Sim(db, recipe, doc).Config.Banks.Max(b => b.RunnerStages.Count);
        var speeds = new double?[stages];
        for (int k = 1; k < stages; k++) speeds[k] = Crossover(db, recipe, HoldingStage(doc, k - 1), HoldingStage(doc, k));
        if (stages <= 2) return doc with { IntakeRunnerSwitchRpm = stages == 2 ? speeds[1] : doc.IntakeRunnerSwitchRpm };
        if (doc.IntakeRunnerUpperSwitchRpm == null)
            throw new InvalidDataException($"{recipe.Tune}: the intake has {stages} stages; add intake_runner_upper_switch_rpm (a placeholder list) to the tune first.");
        // A stage that never wins against the one below it is never switched to, and nor is any above it.
        var upper = speeds[1] == null ? Array.Empty<double>() : speeds[2..].TakeWhile(v => v != null).Select(v => v!.Value).ToArray();
        return doc with { IntakeRunnerSwitchRpm = speeds[1], IntakeRunnerUpperSwitchRpm = upper };
    }

    /// <summary>The tune with its intake held on runner stage <paramref name="stage"/> at every speed a sweep visits.</summary>
    public static TuneDocument HoldingStage(TuneDocument doc, int stage) => doc with
    {
        IntakeRunnerSwitchRpm = stage >= 1 ? 500 : null,
        IntakeRunnerUpperSwitchRpm = stage >= 2 ? Enumerable.Repeat(500.0, stage - 1).ToArray() : null,
    };

    /// <summary>
    /// Speed at which the second stage (switched runner or high-lift profile) starts making more full-load torque than the
    /// first and keeps doing so up to the rev limit: both stages held, 100 rpm grid, zero crossing interpolated and rounded
    /// to 100 rpm. Null when the second stage never wins.
    /// </summary>
    internal static double? Crossover(ContentDatabase db, TuneRecipe recipe, TuneDocument doc, bool lift) => lift
        ? Crossover(db, recipe, doc with { ValveLiftSwitchRpm = null }, doc with { ValveLiftSwitchRpm = 500 })
        : Crossover(db, recipe, HoldingStage(doc, 0), HoldingStage(doc, 1));

    /// <summary>The full-load torque crossover of two held configurations (see <see cref="Crossover(ContentDatabase, TuneRecipe, TuneDocument, bool)"/>).</summary>
    private static double? Crossover(ContentDatabase db, TuneRecipe recipe, TuneDocument first, TuneDocument then)
    {
        var primary = Sweep(db, recipe, first);
        var second = Sweep(db, recipe, then);
        int last = -1;
        for (int i = 0; i < primary.Count; i++)
            if (second[i].Torque < primary[i].Torque) last = i;
        if (last == primary.Count - 1) return null;
        if (last < 0) return primary[0].Rpm;
        double d0 = second[last].Torque - primary[last].Torque, d1 = second[last + 1].Torque - primary[last + 1].Torque;
        double rpm = primary[last].Rpm + (primary[last + 1].Rpm - primary[last].Rpm) * d0 / (d0 - d1);
        return Math.Round(rpm / 100.0) * 100.0;
    }

    private static IReadOnlyList<EngineTelemetry> Sweep(ContentDatabase db, TuneRecipe recipe, TuneDocument doc)
    {
        var sim = Sim(db, recipe, doc);
        sim.DamageEnabled = false;
        return SteadyStateSweep.Run(sim, 1000, doc.RevLimitRpm - 100, 100, settleSeconds: 1.0);
    }

    /// <summary>Hand-authored spark cells more advanced than what the spark calibrator would write (knock/MBT ceiling).</summary>
    private static IEnumerable<string> AuditSpark(ContentDatabase db, TuneRecipe recipe, TuneDocument doc)
    {
        var ceiling = ThroughFile(SparkCalibrator.Calibrate(Sim(db, recipe, doc)), IgnitionAdvance);
        var hand = doc.IgnitionAdvanceDeg;
        var over = new List<(double Excess, string Cell)>();
        for (int r = 0; r < hand.Length; r++)
            for (int c = 0; c < hand[r].Length; c++)
                if (hand[r][c] > ceiling[r][c])
                    over.Add((hand[r][c] - ceiling[r][c], string.Create(Inv, $"{doc.LoadAxisKpa[r]:0} kPa/{doc.RpmAxis[c]:0} rpm {hand[r][c]:0.#}° vs {ceiling[r][c]:0.#}°")));
        if (over.Count == 0)
        {
            yield return $"hand-authored {IgnitionAdvance}: no cell above the calibrator's knock/MBT ceiling on {recipe.Fuel}";
            yield break;
        }
        string summary = string.Create(Inv,
            $"hand-authored {IgnitionAdvance}: {over.Count} of {hand.Sum(r => r.Length)} cells above the calibrator's knock/MBT ceiling on {recipe.Fuel} (max {over.Max(o => o.Excess):0.#}°): ");
        yield return summary + string.Join("; ", over.OrderByDescending(o => o.Excess).Take(6).Select(o => o.Cell)) + (over.Count > 6 ? "; …" : "");
    }

    private static void Swap(EngineAssembly a, string slot, PartDefinition part, PartInstanceFactory factory)
    {
        var removed = new Stack<(string, PartInstance)>();
        foreach (var s in a.RemovalSequenceFor(slot))
        {
            Check(a.Remove(s, out var p));
            removed.Push((s, p!));
        }
        if (a.IsInstalled(slot)) Check(a.Remove(slot, out _));
        Check(a.Install(slot, factory.Create(part)));
        while (removed.Count > 0)
        {
            var (s, p) = removed.Pop();
            Check(a.Install(s, p));
        }
    }

    private static void Check(AssemblyResult result)
    {
        if (!result.Ok) throw new InvalidOperationException(result.Message);
    }

    // ---- Fields and the file format -----------------------------------------------------------------------------------

    private static double[][] Values(TuneDocument d, string field) => field switch
    {
        VolumetricEfficiency => d.VolumetricEfficiency ?? throw new InvalidDataException($"{d.Id} has no {field}."),
        IgnitionAdvance => d.IgnitionAdvanceDeg,
        IntakeCamAdvance => d.IntakeCamAdvanceDeg ?? throw new InvalidDataException($"{d.Id} has no {field} (add a placeholder table first)."),
        RunnerSwitch => new[] { new[] { d.IntakeRunnerSwitchRpm ?? double.NaN }.Concat(d.IntakeRunnerUpperSwitchRpm ?? Array.Empty<double>()).ToArray() },
        LiftSwitch => new[] { new[] { d.ValveLiftSwitchRpm ?? double.NaN } },
        _ => throw new InvalidDataException($"Unknown field '{field}'."),
    };

    /// <summary>The number format each field is written in (the calibrate commands' output format).</summary>
    public static string Format(string field, double v) => field switch
    {
        VolumetricEfficiency => v.ToString("0.000", Inv),
        RunnerSwitch or RunnerUpperSwitch or LiftSwitch => v.ToString("0", Inv),
        _ => v.ToString("0.#", Inv),
    };

    /// <summary>A table as it reads back after being written into a tune file.</summary>
    private static double[][] ThroughFile(double[][] table, string field) =>
        table.Select(row => row.Select(v => double.Parse(Format(field, v), Inv)).ToArray()).ToArray();

    /// <summary>Start and end (exclusive) of the tune object whose id is <paramref name="tuneId"/>.</summary>
    private static (int Start, int End) TuneObject(string text, string tuneId)
    {
        var match = Regex.Match(text, "\"id\"\\s*:\\s*\"" + Regex.Escape(tuneId) + "\"");
        if (!match.Success) throw new InvalidDataException($"Tune '{tuneId}' not found.");
        int depth = 0, start = -1;
        for (int i = match.Index; i >= 0; i--)
        {
            if (text[i] == '}') depth++;
            else if (text[i] == '{' && depth-- == 0) { start = i; break; }
        }
        if (start < 0) throw new InvalidDataException($"Tune '{tuneId}': no enclosing object.");
        int end = Matching(text, start, '{', '}');
        return (start, end + 1);
    }

    /// <summary>Index of the bracket closing the one at <paramref name="open"/>, skipping strings and comments.</summary>
    private static int Matching(string text, int open, char opening, char closing)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '"')
            {
                for (i++; i < text.Length && text[i] != '"'; i++) if (text[i] == '\\') i++;
            }
            else if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (ch == opening) depth++;
            else if (ch == closing && --depth == 0) return i;
        }
        throw new InvalidDataException("Unbalanced brackets.");
    }

    private static string ReplaceTable(string text, int start, int end, string field, double[][] rows)
    {
        var key = new Regex("\"" + Regex.Escape(field) + "\"\\s*:\\s*\\[");
        var match = key.Match(text, start, end - start);
        if (!match.Success) throw new InvalidDataException($"'{field}' not found in the tune object.");
        int open = match.Index + match.Length - 1, close = Matching(text, open, '[', ']');
        string body = text[(open + 1)..close];
        // Keep the file's number style: fixed three decimals (0.480, as `carsim calibrate-ve` prints) or shortest (0.48).
        bool fixedDecimals = field != VolumetricEfficiency || Regex.IsMatch(body, @"\d\.\d{2}0\b");
        string Number(double v) => fixedDecimals ? Format(field, v) : v.ToString("0.###", Inv);
        var firstRow = Regex.Match(body, "\\n([ \\t]*)\\[");
        string rowIndent = firstRow.Success ? firstRow.Groups[1].Value : "        ";
        int lineStart = text.LastIndexOf('\n', close) + 1;
        string closeIndent = text[lineStart..close].Trim().Length == 0 ? text[lineStart..close] : "";
        var sb = new StringBuilder();
        sb.Append('\n');
        for (int r = 0; r < rows.Length; r++)
        {
            sb.Append(rowIndent).Append('[').Append(string.Join(", ", rows[r].Select(Number))).Append(']');
            if (r < rows.Length - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append(closeIndent);
        return text[..(open + 1)] + sb + text[close..];
    }

    /// <summary>Replaces a flat list of numbers (<c>"field": [a, b]</c>) in place, in the field's number format.</summary>
    private static string ReplaceList(string text, int start, int end, string field, double[] values)
    {
        var key = new Regex("\"" + Regex.Escape(field) + "\"\\s*:\\s*\\[");
        var match = key.Match(text, start, end - start);
        if (!match.Success) throw new InvalidDataException($"'{field}' not found in the tune object.");
        int open = match.Index + match.Length - 1, close = Matching(text, open, '[', ']');
        return text[..(open + 1)] + string.Join(", ", values.Select(v => Format(field, v))) + text[close..];
    }

    private static string ReplaceScalar(string text, int start, int end, string field, double value)
    {
        var key = new Regex("(\"" + Regex.Escape(field) + "\"\\s*:\\s*)(-?[0-9.]+|null)");
        var match = key.Match(text, start, end - start);
        if (!match.Success) throw new InvalidDataException($"'{field}' not found in the tune object.");
        string written = double.IsNaN(value) ? "null" : Format(field, value);
        return text[..match.Groups[2].Index] + written + text[(match.Groups[2].Index + match.Groups[2].Length)..];
    }
}
