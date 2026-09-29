using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarSim.Core.Content;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Fuels;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;

namespace CarSim.Verification.Calibration;

/// <summary>What <c>carsim generate-tune</c> is asked for. The fuel is explicit: no default is ever chosen.</summary>
/// <param name="EngineId">The engine to generate a baseline tune for (its stock build).</param>
/// <param name="FuelId">The calibration fuel. Required.</param>
/// <param name="PolicyTuneId">The tune whose hand-authored policy (λ targets, rev limit, idle, axes, boost target) the
/// generated tune keeps; default: the engine's stock tune.</param>
/// <param name="TuneId">The generated tune's id; default: the policy tune's (so it can replace it from a later content layer).</param>
/// <param name="Strict">Refuse when <c>check-engine</c> reports warnings too (<c>--strict 1</c>).</param>
public sealed record GenerationRequest(string EngineId, string? FuelId, string? PolicyTuneId = null, string? TuneId = null, bool Strict = false);

/// <summary>Where a value of a generated tune comes from.</summary>
public static class FieldOrigins
{
    /// <summary>Set from the stock build's derived values, its parts' specs or the fuel (the ECU's beliefs).</summary>
    public const string Derived = "derived";

    /// <summary>Produced by a calibrator step of the recipe.</summary>
    public const string Calibrated = "calibrated";

    /// <summary>Copied unchanged from the policy tune: no calibrator produces it.</summary>
    public const string HandAuthored = "hand-authored";
}

/// <summary>One field of the generated tune: its origin (<see cref="FieldOrigins"/>) and where the value comes from.</summary>
public sealed record GeneratedField(string Field, string Origin, string Basis);

/// <summary>Something the generator does not produce, and why. <paramref name="Status"/> is NOT GENERATABLE or NOT MODELLED.</summary>
public sealed record GenerationNote(string Subject, string Status, string Reason);

/// <summary>
/// The generation block of the record written beside a generated tune (<see cref="TuneRecipe.Generated"/>): what made it,
/// from what, and the digest of the tune file as written. No timestamp and no path: the same inputs give the same record.
/// <paramref name="Manifest"/> is <see cref="TuneGenerator.ManifestLoaded"/> or <see cref="TuneGenerator.ManifestNone"/>
/// (no manifest: no table was known to be hand-authored).
/// </summary>
public sealed record GenerationRecord(string Generator, int Version, string PolicyTune, string RecipeSource, string Manifest,
    IReadOnlyList<string[]> Assembly, IReadOnlyList<GeneratedField> Fields, IReadOnlyList<GenerationNote> NotGenerated,
    int Iterations, string OutputSha256);

/// <summary>
/// The tune manifest a generation uses (<see cref="TuneGenerator.FindManifest"/>): the manifest and where it was read from,
/// none (<see cref="Manifest"/> and <see cref="Path"/> null), or why the one named or found cannot be used
/// (<see cref="Error"/>: generation must not run).
/// </summary>
public sealed record ManifestLookup(TuneManifest? Manifest, string? Path, string? Error);

/// <summary>Why a generation stopped. <paramref name="Code"/> is stable (tests and tools read it).</summary>
public sealed record GenerationRefusal(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>Everything decided before any calibrator runs (<see cref="TuneGenerator.Plan"/>).</summary>
public sealed class TunePlan
{
    public required EngineDefinition Engine { get; init; }
    public required FuelDefinition Fuel { get; init; }

    /// <summary>The tune the hand-authored policy and the calibrators' starting tables come from.</summary>
    public required TuneDocument Policy { get; init; }

    /// <summary>The resolved stock assembly (variants resolved by the loader; the build the calibrators run on).</summary>
    public required EngineAssembly Assembly { get; init; }
    public required EngineCapabilities Capabilities { get; init; }
    public required EngineDerivedValues Derived { get; init; }

    /// <summary>The recipe the calibration runs: engine, fuel (the requested one), steps and hand-authored tables.</summary>
    public required TuneRecipe Recipe { get; init; }
    public required string RecipeSource { get; init; }

    /// <summary>
    /// <see cref="TuneGenerator.ManifestLoaded"/>, or <see cref="TuneGenerator.ManifestNone"/>: no tune manifest, so no table
    /// is known to be hand-authored (the report warns; the record says so).
    /// </summary>
    public required string Manifest { get; init; }

    /// <summary>The policy tune with the derived beliefs set: where calibration starts.</summary>
    public required TuneDocument Skeleton { get; init; }
    public required IReadOnlyList<GeneratedField> Fields { get; init; }
    public required IReadOnlyList<GenerationNote> Notes { get; init; }

    /// <summary>The fields the recipe's calibrator steps write.</summary>
    public IReadOnlyList<string> CalibratedFields => TuneRegenerator.CalibratedFields(Recipe);
}

/// <summary>
/// The result of <see cref="TuneGenerator.Plan"/> or <see cref="TuneGenerator.Generate"/>: the check, the plan, and — for a
/// generation that was not refused — the generated tune, its file text, how many recipe iterations it took to settle, and
/// its validation findings. <see cref="ToText"/> is the deterministic report.
/// </summary>
public sealed class TuneGeneration
{
    public required GenerationRequest Request { get; init; }
    public EngineCheckReport? Check { get; init; }
    public TunePlan? Plan { get; init; }
    public IReadOnlyList<GenerationRefusal> Refusals { get; init; } = Array.Empty<GenerationRefusal>();

    /// <summary>The generated tune (null for a plan or a refusal).</summary>
    public TuneDocument? Tune { get; init; }

    /// <summary>The tune file as written (<see cref="TuneGenerator.WriteTuneFile"/>).</summary>
    public string? Text { get; init; }
    public int Iterations { get; init; }

    /// <summary>The last pass of the recipe over the generated tune: every field reproduced (the regeneration guarantee).</summary>
    public SettledTune? Settled { get; init; }

    /// <summary>What the validation of the generated tune found (errors refuse it; warnings and notes are reported).</summary>
    public IReadOnlyList<CheckFinding> Validation { get; init; } = Array.Empty<CheckFinding>();

    public bool Refused => Refusals.Count > 0;
    public bool Has(string refusalCode) => Refusals.Any(r => r.Code == refusalCode);

    /// <summary>The report. Deterministic: the same content and request give the same text.</summary>
    public string ToText(bool verbose = false)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("generate-tune ").Append(Request.EngineId).Append(" on ").Append(Request.FuelId ?? "(no fuel)");
        if (Plan != null) sb.Append(" — ").Append(Plan.Engine.Name);
        else if (Check is { EngineName.Length: > 0 }) sb.Append(" — ").Append(Check.EngineName);
        sb.Append('\n');
        if (Check != null)
        {
            sb.Append("\nCHECK  ").Append(Check.Verdict(Request.Strict)).Append(" (carsim check-engine ").Append(Request.EngineId).Append(")\n");
            foreach (var e in Check.Errors) sb.Append("  ").Append(e).Append('\n');
            foreach (var w in Check.Warnings) sb.Append("  ").Append(w).Append('\n');
        }
        if (Plan is { } p)
        {
            sb.Append("\nENGINE  ").Append(string.Join(", ", p.Capabilities.Describe())).Append('\n');
            sb.Append("  resolved stock build: ").Append(p.Assembly.Installed.Count).Append(" parts")
              .Append(p.Engine.Extends != null ? $" (variant of '{p.Engine.Extends}', resolved by the loader)" : "").Append('\n');
            if (verbose)
                foreach (var slot in p.Engine.Slots.Where(s => p.Assembly.IsInstalled(s.Id)))
                    sb.Append("    ").Append(slot.Id).Append(" = ").Append(p.Assembly.PartIn(slot.Id)!.Definition.Id).Append('\n');
            sb.Append("\nDERIVED\n");
            foreach (var line in verbose ? p.Derived.Lines() : p.Derived.Summary()) sb.Append("  ").Append(line).Append('\n');
            sb.Append("\nFUEL  ").Append(p.Fuel.Id).Append(" (").Append(p.Fuel.Name).Append(string.Create(inv,
                $"): RON {p.Fuel.OctaneRon:0.#}, stoichiometric AFR {p.Fuel.StoichiometricAfr:0.###}, density {p.Fuel.DensityKgL:0.####} kg/L\n"));
            sb.Append("\nPOLICY  ").Append(p.Policy.Id).Append(Request.PolicyTuneId == null ? " (the engine's stock tune)" : " (--policy)").Append('\n');
            sb.Append("\nRECIPE  ").Append(string.Join(" → ", p.Recipe.Steps.Select(TuneGenerator.Describe))).Append('\n');
            sb.Append("  ").Append(p.RecipeSource).Append('\n');
            sb.Append("  hand-authored: ").Append(p.Recipe.HandAuthored.Count == 0 ? "none" : string.Join(", ", p.Recipe.HandAuthored)).Append('\n');
            sb.Append("  manifest: ").Append(p.Manifest).Append('\n');
            if (p.Manifest == TuneGenerator.ManifestNone) sb.Append("  ").Append(TuneGenerator.NoManifestWarning).Append('\n');
            sb.Append("\nFIELDS\n");
            foreach (var f in p.Fields) sb.Append("  ").Append(f.Field.PadRight(32)).Append(f.Origin.PadRight(15)).Append(f.Basis).Append('\n');
            if (p.Notes.Count > 0)
            {
                sb.Append("\nNOT GENERATED\n");
                foreach (var n in p.Notes) sb.Append("  [").Append(n.Status).Append("] ").Append(n.Subject).Append(": ").Append(n.Reason).Append('\n');
            }
        }
        if (Settled != null)
        {
            int cycles = Settled.Fields.Sum(f => f.Oscillating);
            sb.Append("\nCALIBRATION  settled after ").Append(Iterations).Append(" iteration(s) of the recipe on ").Append(Request.FuelId)
              .Append(": a further pass reproduces every calibrated value (the regeneration guarantee)")
              .Append(cycles > 0 ? $"; {cycles} value(s) in a rounding 2-cycle keep theirs" : "").Append('\n');
        }
        if (Tune != null)
        {
            var shown = Validation.Where(f => verbose || f.Severity != IssueSeverity.Info).ToList();
            sb.Append("\nVALIDATION  ").Append(Validation.Any(f => f.Severity == IssueSeverity.Warning) ? "warning" : "ok")
              .Append(" (tune rules, the ECU's settable ranges, beliefs = build and fuel, the tune ↔ build rules of check-engine, file round trip)\n");
            foreach (var f in shown) sb.Append("  ").Append(f).Append('\n');
            sb.Append("\nAGAINST THE POLICY TUNE ").Append(Plan!.Policy.Id).Append('\n');
            foreach (var line in TuneGenerator.Differences(Plan.Policy, Tune)) sb.Append("  ").Append(line).Append('\n');
        }
        sb.Append("\nRESULT  ");
        if (Refused)
        {
            sb.Append("REFUSED (").Append(Refusals.Count).Append(")\n");
            foreach (var r in Refusals) sb.Append("  ").Append(r).Append('\n');
        }
        else sb.Append(Tune != null ? "GENERATED" : "PLANNED (no calibration run)").Append('\n');
        return sb.ToString();
    }
}

/// <summary>What <see cref="TuneGenerator.Write"/> did. <paramref name="ExitCode"/>: 0 written or unchanged, 2 refused, 4 differs (<c>--check</c>), 1 usage.</summary>
public sealed record WriteOutcome(int ExitCode, string Message);

/// <summary>
/// Deterministic baseline tune generation (Engine Authoring Factory 1.0, Phase 3; <c>carsim generate-tune</c>). It adds no
/// calibration mathematics and no tuning system: it orchestrates what exists.
/// <list type="number">
/// <item>Content is loaded as it is (variants resolved by the loader); <see cref="EngineCheck"/> runs on the engine and any
/// error refuses generation.</item>
/// <item>The fuel is explicit. The resolved stock assembly is built and its <see cref="EngineDerivedValues"/> computed.</item>
/// <item>The ECU's beliefs come from the build and the fuel — displacement from the derived values (the one source), injector
/// flow and dead time from the installed injectors, stoichiometric AFR and density from the fuel. Everything no calibrator
/// produces (λ targets, rev limit, idle, axes, a boost target, any table a recipe lists as hand-authored) comes unchanged
/// from a policy tune, by default the engine's stock tune; without one, generation is refused as NOT GENERATABLE.</item>
/// <item>The recipe is the manifest's for that tune when it calibrates this engine's stock build, otherwise derived from the
/// hardware by the rule the manifest's recipes follow (<see cref="DeriveSteps"/>).</item>
/// <item>The recipe runs through <see cref="TuneRegenerator.Settle"/> — the dev calibrators, as <c>regenerate-tunes</c> runs
/// them — until a pass reproduces every calibrated value: the generated tune is a fixed point of its recipe, so regenerating
/// it gives it back.</item>
/// <item>The tune is validated (<see cref="Validate"/>), written in the existing tune format with a record beside it, and
/// never over a file the generator did not write (<see cref="Write"/>).</item>
/// </list>
/// Nothing here knows an engine, a part or a fuel by id.
/// </summary>
public static class TuneGenerator
{
    public const string Name = "carsim generate-tune";

    /// <summary>Bumped when the generator's output for the same inputs changes.</summary>
    public const int Version = 1;

    /// <summary>Recipe iterations allowed before a generation is refused as not settling.</summary>
    public const int MaxIterations = 5;

    /// <summary>Extension of the record written beside a generated tune file (not *.json, so a content loader never reads it).</summary>
    public const string RecordExtension = ".recipe.jsonc";

    /// <summary>What the report and the record say about the tune manifest (<see cref="TunePlan.Manifest"/>).</summary>
    public const string ManifestLoaded = "loaded", ManifestNone = "none";

    /// <summary>The report's line when a generation runs without a tune manifest (review finding P3-001).</summary>
    public const string NoManifestWarning =
        "WARNING: no tune manifest was found; no table is known to be hand-authored and all calibratable tables will be " +
        "regenerated (a table a manifest lists as hand-authored, such as a factory spark map, is replaced).";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- The tune manifest ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The tune manifest for a generation. The manifest says which tables are hand-authored, so it is never silently
    /// dropped (review finding P3-001): a manifest named explicitly (<paramref name="explicitPath"/>, <c>--manifest</c>) must
    /// exist and load, and so must one discovered in the repository (<paramref name="discoveredPath"/>) — otherwise
    /// <see cref="ManifestLookup.Error"/> says why and generation must not run. Only when none is named and none is found
    /// does generation run without one, reported as <see cref="ManifestNone"/> with <see cref="NoManifestWarning"/>.
    /// </summary>
    public static ManifestLookup FindManifest(string? explicitPath, string? discoveredPath)
    {
        if (explicitPath != null)
        {
            if (!File.Exists(explicitPath))
                return new ManifestLookup(null, explicitPath, $"--manifest {explicitPath}: no such file. A manifest named explicitly must load " +
                                                               "(it says which tables are hand-authored); nothing was generated.");
            return Read(explicitPath, $"--manifest {explicitPath}");
        }
        if (discoveredPath != null && File.Exists(discoveredPath)) return Read(discoveredPath, $"the tune manifest {discoveredPath}");
        return new ManifestLookup(null, null, null);

        static ManifestLookup Read(string path, string what)
        {
            try
            {
                var manifest = TuneManifest.Load(path);
                var problems = ManifestProblems(manifest);
                return problems.Count == 0
                    ? new ManifestLookup(manifest, path, null)
                    : new ManifestLookup(null, path, $"{what} is not a valid tune manifest: {string.Join("; ", problems)}. Nothing was generated.");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                return new ManifestLookup(null, path, $"{what} could not be loaded: {ex.Message} Nothing was generated.");
            }
        }
    }

    /// <summary>What a recipe of a tune manifest lacks for the generator to read it (every field it reads, and known calibrators).</summary>
    public static IReadOnlyList<string> ManifestProblems(TuneManifest manifest)
    {
        if (manifest.Tunes == null) return new[] { "it has no \"tunes\" list" };
        var problems = new List<string>();
        for (int i = 0; i < manifest.Tunes.Count; i++)
        {
            var r = manifest.Tunes[i];
            string at = $"tunes[{i}]";
            if (r == null) { problems.Add($"{at} is empty"); continue; }
            foreach (var (name, value) in new[] { ("tune", r.Tune), ("engine", r.Engine), ("fuel", r.Fuel) })
                if (string.IsNullOrWhiteSpace(value)) problems.Add($"{at} has no \"{name}\"");
            if (r.Steps == null) problems.Add($"{at} has no \"steps\"");
            else
                foreach (var step in r.Steps)
                {
                    try { TuneRegenerator.FieldOf(step?.Calibrator ?? ""); }
                    catch (InvalidDataException) { problems.Add($"{at} has an unknown calibrator step '{step?.Calibrator}'"); }
                }
            if (r.HandAuthored == null) problems.Add($"{at} has no \"hand_authored\" list");
        }
        return problems;
    }

    // ---- Plan ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Everything up to the calibration: the check, the fuel, the resolved assembly, its derived values, the policy, the
    /// recipe, the starting tune and its validation. Cheap (no engine is run).
    /// </summary>
    public static TuneGeneration Plan(ContentLoadResult load, GenerationRequest request, TuneManifest? manifest)
    {
        var db = load.Database;
        var refusals = new List<GenerationRefusal>();

        // 1. The engine must pass check-engine: it resolves, its stock parts resolve and install, the assembly is valid.
        var check = EngineCheck.Run(load, request.EngineId);
        if (!check.Passed)
            refusals.Add(new("check_failed", $"check-engine {request.EngineId}: {check.Verdict(request.Strict)}. Generation needs an engine that passes " +
                                             "it; fix the errors listed under CHECK."));
        else if (request.Strict && check.Warnings.Any())
            refusals.Add(new("check_warnings", $"--strict: check-engine {request.EngineId} reports {check.Warnings.Count()} warning(s)."));

        // 2. The fuel is explicit: it sets the spark map's knock limit and the ECU's fuel beliefs.
        string fuels = string.Join(", ", db.Fuels.Keys.OrderBy(k => k, StringComparer.Ordinal));
        FuelDefinition? requestedFuel = null;
        if (string.IsNullOrWhiteSpace(request.FuelId))
            refusals.Add(new("fuel_required", $"The calibration fuel must be explicit (--fuel <id>); none is chosen by default. Loaded: {fuels}."));
        else if (!db.Fuels.TryGetValue(request.FuelId, out requestedFuel))
            refusals.Add(new("unknown_fuel", $"No fuel '{request.FuelId}'. Loaded: {fuels}."));
        if (refusals.Count > 0) return new TuneGeneration { Request = request, Check = check, Refusals = refusals };
        var fuel = requestedFuel!; // present: a missing or unknown fuel was refused above

        // 3. The resolved engine (a variant arrives merged by the loader) and its stock assembly.
        var engine = db.Engines[request.EngineId];
        var assembly = EngineAssembly.CreateStock(engine, db, new PartInstanceFactory());

        // 4. The policy: hand-authored values no calibrator produces.
        string policyId = request.PolicyTuneId ?? engine.StockTune;
        if (policyId.Length == 0)
            return Refuse(request, check, "no_policy_tune",
                "NOT GENERATABLE: target_lambda, rev_limit_rpm, idle_rpm and the table axes (and a boost target on a turbo build) are " +
                "hand-authored policy that no calibrator produces, and the engine names no stock tune to take them from. Author a " +
                "tune with that policy (its VE and spark tables may be placeholders) and name it with --policy <tune-id> or as the " +
                "engine's stock_tune.");
        if (!db.Tunes.TryGetValue(policyId, out var policy))
            return Refuse(request, check, "unknown_policy_tune", $"No tune '{policyId}' to take the policy from.");

        // 5. Derived values of the resolved build: the one source of the ECU's displacement belief.
        var derived = EngineDerivedValues.Of(assembly, policy.RevLimitRpm, $"policy tune {policy.Id}");
        if (derived.DisplacementCc is not double displacement)
            return Refuse(request, check, "derived_unavailable", $"The build's displacement cannot be derived ({derived.Find(EngineDerivedValues.Keys.Displacement)?.Basis}).");
        var injectorPart = assembly.FindByCategory(PartCategory.Injectors);
        if (injectorPart?.EffectiveSpec is not InjectorSpec injectors)
            return Refuse(request, check, "no_injectors", "NOT GENERATABLE: the build has no injectors to set injector_flow_cc_min and injector_dead_time_ms from.");

        // 6. The recipe: the manifest's for this tune on this engine's stock build, else derived from the hardware.
        var capabilities = EngineCapabilities.Resolve(assembly);
        var policyRecipe = manifest?.Tunes.FirstOrDefault(t => t.Tune == policy.Id);
        IReadOnlyList<CalibrationStep> steps;
        IReadOnlyList<string> handAuthored;
        string recipeSource;
        if (policyRecipe != null && policyRecipe.Engine == engine.Id && IsStockBuild(policyRecipe.Build))
        {
            steps = policyRecipe.Steps;
            handAuthored = policyRecipe.HandAuthored;
            recipeSource = $"the tune manifest's recipe for {policy.Id} (this engine's stock build), steps and parameters unchanged";
        }
        else
        {
            var hand = new List<string>(policyRecipe?.HandAuthored ?? Array.Empty<string>());
            if (!hand.Contains("target_lambda")) hand.Add("target_lambda");
            if (policy.BoostTargetKpa != null && !hand.Contains("boost_target_kpa")) hand.Add("boost_target_kpa");
            handAuthored = hand;
            steps = DeriveSteps(capabilities, hand);
            recipeSource = "derived from the hardware (schedule: cams, runner_switch, lift_switch as fitted and ECU-driven; then ve → spark → ve)" +
                           (policyRecipe != null ? $"; the manifest's recipe for {policy.Id} calibrates {policyRecipe.Engine}{(IsStockBuild(policyRecipe.Build) ? "" : " with a modified build")}, so only its hand-authored list is kept" : "");
        }

        // 7. The starting tune: the policy with the beliefs set from the build and the fuel.
        string tuneId = request.TuneId ?? policy.Id;
        var recipe = new TuneRecipe(tuneId, engine.Id, "generated", "", fuel.Id, new BuildRecipe(null, null), steps, handAuthored,
            Provenance(engine, fuel, policy, steps));
        var calibrated = TuneRegenerator.CalibratedFields(recipe);
        // The calibrators build their engine from the recipe (TuneRegenerator.BuildAssembly): it must be the build checked here.
        var calibrationBuild = TuneRegenerator.BuildAssembly(db, recipe);
        var differentSlots = engine.Slots.Where(s => calibrationBuild.PartIn(s.Id)?.Definition.Id != assembly.PartIn(s.Id)?.Definition.Id)
            .Select(s => s.Id).ToList();
        if (differentSlots.Count > 0)
            return Refuse(request, check, "calibration_build_mismatch",
                $"The calibrators would run on another build than the one checked (slots {string.Join(", ", differentSlots)}).");
        var skeleton = policy with
        {
            Id = tuneId,
            Name = $"{policy.Name} (generated on {fuel.Id})",
            Description = recipe.Provenance,
            Source = "",
            DisplacementCc = displacement,
            InjectorFlowCcMin = injectors.FlowCcMin,
            InjectorDeadTimeMs = injectors.DeadTimeMs,
            FuelStoichAfr = fuel.StoichiometricAfr,
            FuelDensityKgL = fuel.DensityKgL,
        };
        // A cam map the recipe calibrates starts at the phaser's park position (what the ECU does without one); an intake
        // with three or more stages gets an empty list of upper switch speeds (switch no further) for the step to fill.
        if (calibrated.Contains(TuneRegenerator.IntakeCamAdvance) && skeleton.IntakeCamAdvanceDeg == null)
            skeleton = skeleton with { IntakeCamAdvanceDeg = skeleton.LoadAxisKpa.Select(_ => new double[skeleton.RpmAxis.Length]).ToArray() };
        int stages = assembly.PartsOf(PartCategory.IntakeManifold).Select(x => ((IntakeManifoldSpec)x.Part.EffectiveSpec).AllStages().Count).DefaultIfEmpty(1).Max();
        if (calibrated.Contains(TuneRegenerator.RunnerSwitch) && stages > 2 && skeleton.IntakeRunnerUpperSwitchRpm == null)
            skeleton = skeleton with { IntakeRunnerUpperSwitchRpm = Array.Empty<double>() };

        var plan = new TunePlan
        {
            Engine = engine, Fuel = fuel, Policy = policy, Assembly = assembly, Capabilities = capabilities, Derived = derived,
            Recipe = recipe, RecipeSource = recipeSource, Manifest = manifest == null ? ManifestNone : ManifestLoaded, Skeleton = skeleton,
            Fields = Fields(skeleton, calibrated, recipe, policy, injectorPart.Definition.Id, fuel),
            Notes = Notes(check, capabilities, handAuthored, calibrated, policy),
        };

        // 8. The starting tune must be valid before any calibrator runs on it.
        var problems = Validate(plan, skeleton);
        if (Blocks(problems))
            return new TuneGeneration
            {
                Request = request, Check = check, Plan = plan,
                Refusals = problems.Where(f => f.Severity == IssueSeverity.Error).Select(f => new GenerationRefusal(f.Code, f.Message)).ToList(),
            };
        return new TuneGeneration { Request = request, Check = check, Plan = plan };
    }

    // ---- Generate -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="Plan"/>, then the recipe until it settles, then validation and the file's round trip. Deterministic:
    /// the same content and request give the same tune, text and report.
    /// </summary>
    public static TuneGeneration Generate(ContentLoadResult load, GenerationRequest request, TuneManifest? manifest)
    {
        var planned = Plan(load, request, manifest);
        if (planned.Refused) return planned;
        var plan = planned.Plan!;

        // The recipe, as regenerate-tunes runs it, until a pass reproduces its own input: a fixed point of the recipe.
        var doc = plan.Skeleton;
        SettledTune? settled = null;
        int iterations = 0;
        for (int i = 1; i <= MaxIterations; i++)
        {
            iterations = i;
            settled = TuneRegenerator.Settle(load.Database, plan.Recipe, doc);
            if (settled.Reproduced) break;
            doc = settled.Document;
        }
        if (!settled!.Reproduced)
            return With(planned, new GenerationRefusal("not_settled", $"The recipe did not settle in {MaxIterations} iterations: " +
                string.Join("; ", settled.Fields.Where(f => !f.Reproduced).Select(f => $"{f.Field} {f.Differing} of {f.Cells} values still moving"))));

        var validation = Validate(plan, doc);
        if (Blocks(validation))
            return With(planned, validation.Where(f => f.Severity == IssueSeverity.Error).Select(f => new GenerationRefusal(f.Code, f.Message)).ToArray());

        string text = WriteTuneFile(doc, plan.CalibratedFields);
        var reloaded = ContentLoader.LoadFromStrings(new[] { ("generated.json", text) });
        var differing = reloaded.Success && reloaded.Database.Tunes.TryGetValue(doc.Id, out var back) ? DifferingFields(doc, back) : new[] { "(did not load)" };
        if (differing.Count > 0)
            return With(planned, new GenerationRefusal("round_trip_failed",
                $"The written tune does not load back identically: {string.Join(", ", differing)} {string.Join("; ", reloaded.Errors)}"));

        return new TuneGeneration
        {
            Request = request, Check = planned.Check, Plan = plan, Tune = doc, Text = text, Iterations = iterations, Settled = settled,
            Validation = validation,
        };
    }

    // ---- Recipe --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The recipe for a build from its hardware, by the rule every recipe in the tune manifest follows
    /// (docs/VERIFICATION.md, "Tune regeneration"): the hardware schedule first — <c>cams</c> for an intake phaser the ECU
    /// drives, <c>runner_switch</c> for a variable intake it switches, <c>lift_switch</c> for two-stage cams it switches —
    /// then the maps measured on it: <c>ve</c>, and when the spark map is calibrated <c>spark</c> and <c>ve</c> again (spark
    /// changes the exhaust temperature and residuals the fuel map was measured with). A table listed in
    /// <paramref name="handAuthored"/> gets no step. Default step parameters are the calibrators' own.
    /// </summary>
    public static IReadOnlyList<CalibrationStep> DeriveSteps(EngineCapabilities capabilities, IReadOnlyCollection<string> handAuthored)
    {
        var steps = new List<CalibrationStep>();
        void Schedule(bool fitted, string calibrator)
        {
            if (fitted && !handAuthored.Contains(TuneRegenerator.FieldOf(calibrator))) steps.Add(new CalibrationStep(calibrator));
        }
        Schedule(capabilities.IntakeCamPhasing, "cams");
        Schedule(capabilities.VariableIntakeRunner, "runner_switch");
        Schedule(capabilities.VariableValveLift, "lift_switch");
        bool ve = !handAuthored.Contains(TuneRegenerator.VolumetricEfficiency), spark = !handAuthored.Contains(TuneRegenerator.IgnitionAdvance);
        if (ve) steps.Add(new CalibrationStep("ve"));
        if (spark)
        {
            steps.Add(new CalibrationStep("spark"));
            if (ve) steps.Add(new CalibrationStep("ve"));
        }
        return steps;
    }

    /// <summary>A step as the report shows it: the calibrator and any parameter that is not the calibrator's default.</summary>
    public static string Describe(CalibrationStep s)
    {
        var p = new List<string>();
        if (s.StepDeg is double step) p.Add(string.Create(Inv, $"step {step:0.##}°"));
        if (s.HoldS is double hold) p.Add(string.Create(Inv, $"hold {hold:0.##} s"));
        if (s.KnockMarginDeg is double knock) p.Add(string.Create(Inv, $"knock margin {knock:0.##}°"));
        if (s.MbtMarginDeg is double mbt) p.Add(string.Create(Inv, $"MBT margin {mbt:0.##}°"));
        return p.Count == 0 ? s.Calibrator : $"{s.Calibrator} ({string.Join(", ", p)})";
    }

    private static bool IsStockBuild(BuildRecipe? build) => build == null || ((build.Swap?.Count ?? 0) == 0 && (build.Add?.Count ?? 0) == 0);

    private static string Provenance(EngineDefinition engine, FuelDefinition fuel, TuneDocument policy, IReadOnlyList<CalibrationStep> steps) =>
        $"Generated by {Name} (version {Version}) for {engine.Id} on {fuel.Id}: the ECU's beliefs from the stock build and the fuel; " +
        $"{string.Join(" → ", steps.Select(Describe))} with the dev calibrators until a further pass reproduces every value; " +
        $"hand-authored policy (λ targets, rev limit, idle, axes and any table the recipe lists) from {policy.Id}.";

    // ---- Validation ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The rules a starting or generated tune must meet, as findings (errors refuse it):
    /// <list type="bullet">
    /// <item>the tune format's own rules and ranges (<see cref="TuneDocument.Validate"/>: what the ECU can be set to);</item>
    /// <item>the beliefs are exactly the build's and the fuel's (the generator's invariant; <c>belief_not_derived</c>);</item>
    /// <item>the ECU honours the tune's rev limit: the calibrators treat every rpm column up to it as running, so a limit above
    /// the ECU's ceiling would put columns on the fuel cut (<c>calibration_rev_limit_above_ecu</c>);</item>
    /// <item>the assembly rules that depend on the tune (valve float, crank and flywheel ratings against its rev limit; MAP
    /// sensor and springs against its boost target), and the tune ↔ build rules of check-engine (<see cref="EngineCheck.CheckTune"/>),
    /// with their own severities.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<CheckFinding> Validate(TunePlan plan, TuneDocument doc)
    {
        var f = new List<CheckFinding>();
        void Error(string code, string message) => f.Add(new CheckFinding(CheckSection.Completeness, IssueSeverity.Error, code, message));
        foreach (var problem in doc.Validate()) Error("tune_invalid", problem);
        if (f.Count > 0) return f;
        EcuTune tune;
        try { tune = EcuTune.FromDocument(doc); }
        catch (InvalidDataException ex) { Error("tune_invalid", ex.Message); return f; }

        var injectors = plan.Assembly.SpecOf<InjectorSpec>(PartCategory.Injectors);
        foreach (var (field, belief, truth, from) in new (string, double?, double?, string)[]
                 {
                     ("displacement_cc", doc.DisplacementCc, plan.Derived.DisplacementCc, "the build's derived displacement"),
                     ("injector_flow_cc_min", doc.InjectorFlowCcMin, injectors?.FlowCcMin, "the installed injectors' flow_cc_min"),
                     ("injector_dead_time_ms", doc.InjectorDeadTimeMs, injectors?.DeadTimeMs, "the installed injectors' dead_time_ms"),
                     ("fuel_stoich_afr", doc.FuelStoichAfr, plan.Fuel.StoichiometricAfr, $"fuel {plan.Fuel.Id}'s stoichiometric_afr"),
                     ("fuel_density_kg_l", doc.FuelDensityKgL, plan.Fuel.DensityKgL, $"fuel {plan.Fuel.Id}'s density_kg_l"),
                 })
            if (!Equals(belief, truth))
                Error("belief_not_derived", string.Create(Inv, $"{field} is {belief}, not {from} ({truth})."));

        if (plan.Assembly.SpecOf<EcuSpec>(PartCategory.Ecu) is { } ecu && doc.RevLimitRpm > ecu.MaxRevLimitRpm)
            Error("calibration_rev_limit_above_ecu", string.Create(Inv,
                $"The policy's {doc.RevLimitRpm:0} rpm limit is above the ECU's {ecu.MaxRevLimitRpm:0} rpm ceiling: the ECU cuts fuel at {ecu.MaxRevLimitRpm:0}, ") +
                "so the calibrators would measure the columns above it on the limiter. Lower rev_limit_rpm in the policy tune, or fit an ECU " +
                "that allows the limit.");

        var context = AssemblyValidator.Validate(plan.Assembly, new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa));
        foreach (var issue in context.Issues.Where(i => EngineCheck.ValidatorSections.GetValueOrDefault(i.Code) == CheckSection.Limits))
            f.Add(new CheckFinding(CheckSection.Limits, issue.Severity, issue.Code, issue.Message));
        f.AddRange(EngineCheck.CheckTune(plan.Assembly, tune, doc, plan.Capabilities, plan.Derived, subject: "generated tune"));
        return f.Distinct().OrderByDescending(x => x.Severity).ThenBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Message, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether validation findings stop the generation: any error does.</summary>
    private static bool Blocks(IReadOnlyList<CheckFinding> findings) => findings.Any(x => x.Severity == IssueSeverity.Error);

    // ---- Fields and notes ----------------------------------------------------------------------------------------------

    private static IReadOnlyList<GeneratedField> Fields(TuneDocument t, IReadOnlyList<string> calibrated, TuneRecipe recipe, TuneDocument policy,
        string injectorsId, FuelDefinition fuel)
    {
        var list = new List<GeneratedField>();
        void Hand(string field, bool present = true)
        {
            if (present) list.Add(new GeneratedField(field, FieldOrigins.HandAuthored, $"policy tune {policy.Id}"));
        }
        void Table(string field, bool present)
        {
            if (calibrated.Contains(field))
                list.Add(new GeneratedField(field, FieldOrigins.Calibrated,
                    "step " + string.Join(", ", recipe.Steps.Where(s => TuneRegenerator.FieldOf(s.Calibrator) == field).Select(Describe).Distinct())));
            else Hand(field, present);
        }
        Hand("rpm_axis");
        Hand("load_axis_kpa");
        Hand("target_lambda");
        Table(TuneRegenerator.VolumetricEfficiency, t.VolumetricEfficiency != null);
        Table(TuneRegenerator.IgnitionAdvance, true);
        Table(TuneRegenerator.IntakeCamAdvance, t.IntakeCamAdvanceDeg != null);
        Hand("boost_target_kpa", t.BoostTargetKpa != null);
        Table(TuneRegenerator.LiftSwitch, t.ValveLiftSwitchRpm != null);
        Table(TuneRegenerator.RunnerSwitch, t.IntakeRunnerSwitchRpm != null);
        if (t.IntakeRunnerUpperSwitchRpm != null)
            list.Add(calibrated.Contains(TuneRegenerator.RunnerSwitch)
                ? new GeneratedField(TuneRegenerator.RunnerUpperSwitch, FieldOrigins.Calibrated, "step runner_switch")
                : new GeneratedField(TuneRegenerator.RunnerUpperSwitch, FieldOrigins.HandAuthored, $"policy tune {policy.Id}"));
        Hand("rev_limit_rpm");
        Hand("idle_rpm");
        Hand("knock_control_enabled");
        list.Add(new GeneratedField("displacement_cc", FieldOrigins.Derived, "the stock build's derived displacement (EngineDerivedValues)"));
        list.Add(new GeneratedField("injector_flow_cc_min", FieldOrigins.Derived, $"injectors {injectorsId}: flow_cc_min"));
        list.Add(new GeneratedField("injector_dead_time_ms", FieldOrigins.Derived, $"injectors {injectorsId}: dead_time_ms"));
        list.Add(new GeneratedField("fuel_stoich_afr", FieldOrigins.Derived, $"fuel {fuel.Id}: stoichiometric_afr"));
        list.Add(new GeneratedField("fuel_density_kg_l", FieldOrigins.Derived, $"fuel {fuel.Id}: density_kg_l"));
        return list;
    }

    private static IReadOnlyList<GenerationNote> Notes(EngineCheckReport check, EngineCapabilities caps, IReadOnlyList<string> handAuthored,
        IReadOnlyList<string> calibrated, TuneDocument policy)
    {
        const string NotGeneratable = "NOT GENERATABLE", NotModelled = "NOT MODELLED";
        var notes = new List<GenerationNote>
        {
            new("target_lambda", NotGeneratable, "λ targets are calibration policy: no calibrator produces them and no named λ policy exists; kept from the policy tune."),
            new("rev_limit_rpm, idle_rpm", NotGeneratable, "reference data or a design decision, never guessed; kept from the policy tune."),
            new("rpm_axis, load_axis_kpa", NotGeneratable, "the tables' layout is a calibration choice; kept from the policy tune."),
            new("knock_control_enabled", NotGeneratable, "an ECU strategy choice; kept from the policy tune."),
        };
        if (policy.BoostTargetKpa != null)
            notes.Add(new("boost_target_kpa", NotGeneratable, "a boost target needs a sourced figure or an explicit decision: no calibrator produces it; kept from the policy tune."));
        foreach (var table in handAuthored.Where(h => h is not ("target_lambda" or "boost_target_kpa")))
            notes.Add(new(table, NotGeneratable, $"listed as hand-authored by the recipe: kept from {policy.Id}, never recalibrated."));
        if (caps.IntakeCamPhasers && !caps.IntakeCamPhasing)
            notes.Add(new(TuneRegenerator.IntakeCamAdvance, NotGeneratable, "the ECU cannot drive the intake phaser (cam_phaser_uncontrolled): no cam map; the phaser stays parked."));
        if (caps.VariableLiftCams && !caps.VariableValveLift)
            notes.Add(new(TuneRegenerator.LiftSwitch, NotGeneratable, "the ECU cannot switch the two-stage cams (valve_lift_uncontrolled): the base profile stays."));
        if (caps.SwitchedRunners && !caps.VariableIntakeRunner)
            notes.Add(new(TuneRegenerator.RunnerSwitch, NotGeneratable, "the ECU cannot switch the intake runners (intake_runner_uncontrolled): the primary runner stays."));
        foreach (var f in check.Features.Where(f => f.Status == FeatureStatus.NotModelled))
            notes.Add(new(f.Feature, NotModelled, $"{f.Description} is not simulated, so the tune has no control for it; stands in: {f.Approximation?.TrimEnd('.')}."));
        return notes;
    }

    /// <summary>How <paramref name="generated"/> differs from <paramref name="policy"/>: tables cell by cell, scalars by value.</summary>
    public static IReadOnlyList<string> Differences(TuneDocument policy, TuneDocument generated)
    {
        var lines = new List<string>();
        foreach (var p in ComparedProperties)
        {
            object? a = p.GetValue(policy), b = p.GetValue(generated);
            if (Same(a, b)) continue;
            string name = JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name);
            if (a is double[][] ta && b is double[][] tb && ta.Length == tb.Length && ta.Zip(tb).All(r => r.First.Length == r.Second.Length))
            {
                var cells = ta.Zip(tb).SelectMany(r => r.First.Zip(r.Second)).Where(c => !c.First.Equals(c.Second)).ToList();
                lines.Add(string.Create(Inv, $"{name}: {cells.Count} of {ta.Sum(r => r.Length)} values differ (max |Δ| {cells.Max(c => Math.Abs(c.First - c.Second)):0.###})"));
            }
            else lines.Add($"{name}: {Show(a)} → {Show(b)}");
        }
        return lines.Count == 0 ? new[] { "identical" } : lines;
    }

    private static string Show(object? v) => v switch
    {
        null => "none",
        double d => d.ToString("R", Inv),
        double[] a => "[" + string.Join(", ", a.Select(x => x.ToString("R", Inv))) + "]",
        double[][] t => $"a {t.Length} × {(t.Length > 0 ? t[0].Length : 0)} table",
        bool b => b ? "true" : "false",
        _ => v.ToString() ?? "",
    };

    // ---- The tune file and its record ----------------------------------------------------------------------------------

    /// <summary>Every property of a tune that content states (all but the loader's <see cref="TuneDocument.Source"/>).</summary>
    private static readonly PropertyInfo[] ComparedProperties = typeof(TuneDocument).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.Name != nameof(TuneDocument.Source)).OrderBy(p => p.MetadataToken).ToArray();

    /// <summary>Fields of <paramref name="a"/> and <paramref name="b"/> that differ (tables and lists compared value by value).</summary>
    public static IReadOnlyList<string> DifferingFields(TuneDocument a, TuneDocument b) =>
        ComparedProperties.Where(p => !Same(p.GetValue(a), p.GetValue(b))).Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name)).ToList();

    private static bool Same(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (double[] x, double[] y) => x.SequenceEqual(y),
        (double[][] x, double[][] y) => x.Length == y.Length && x.Zip(y).All(r => r.First.SequenceEqual(r.Second)),
        _ => Equals(a, b),
    };

    private static readonly JsonSerializerOptions StringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions RecordOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The tune in the existing content format (<c>{"tunes": [ … ]}</c>), one table row per line. Calibrated fields use the
    /// calibrators' number format (<see cref="TuneRegenerator.Format"/>), everything else its shortest round-trip form, so the
    /// file loads back to exactly these values. Deterministic: no timestamp, no path, "\n" line ends.
    /// </summary>
    public static string WriteTuneFile(TuneDocument t, IReadOnlyCollection<string> calibrated)
    {
        string N(string field, double v) =>
            calibrated.Contains(field) || (field == TuneRegenerator.RunnerUpperSwitch && calibrated.Contains(TuneRegenerator.RunnerSwitch))
                ? TuneRegenerator.Format(field, v) : v.ToString("R", Inv);
        var props = new List<string>();
        void Str(string key, string v) => props.Add($"\"{key}\": {JsonSerializer.Serialize(v, StringOptions)}");
        void Num(string key, double? v)
        {
            if (v is double x) props.Add($"\"{key}\": {N(key, x)}");
        }
        void List(string key, double[]? v)
        {
            if (v != null) props.Add($"\"{key}\": [{string.Join(", ", v.Select(x => N(key, x)))}]");
        }
        void Table(string key, double[][]? rows)
        {
            if (rows == null) return;
            props.Add($"\"{key}\": [\n" + string.Join(",\n", rows.Select(r => $"        [{string.Join(", ", r.Select(x => N(key, x)))}]")) + "\n      ]");
        }
        Str("id", t.Id);
        Str("name", t.Name);
        Str("description", t.Description);
        List("rpm_axis", t.RpmAxis);
        List("load_axis_kpa", t.LoadAxisKpa);
        Table("target_lambda", t.TargetLambda);
        Table(TuneRegenerator.VolumetricEfficiency, t.VolumetricEfficiency);
        Table(TuneRegenerator.IgnitionAdvance, t.IgnitionAdvanceDeg);
        Table(TuneRegenerator.IntakeCamAdvance, t.IntakeCamAdvanceDeg);
        List("boost_target_kpa", t.BoostTargetKpa);
        Num(TuneRegenerator.LiftSwitch, t.ValveLiftSwitchRpm);
        Num(TuneRegenerator.RunnerSwitch, t.IntakeRunnerSwitchRpm);
        List(TuneRegenerator.RunnerUpperSwitch, t.IntakeRunnerUpperSwitchRpm);
        Num("rev_limit_rpm", t.RevLimitRpm);
        Num("idle_rpm", t.IdleRpm);
        props.Add($"\"knock_control_enabled\": {(t.KnockControlEnabled ? "true" : "false")}");
        Num("displacement_cc", t.DisplacementCc);
        Num("injector_flow_cc_min", t.InjectorFlowCcMin);
        Num("injector_dead_time_ms", t.InjectorDeadTimeMs);
        Num("fuel_stoich_afr", t.FuelStoichAfr);
        Num("fuel_density_kg_l", t.FuelDensityKgL);
        var sb = new StringBuilder();
        sb.Append($"// Generated by {Name} (version {Version}). Do not edit by hand: regenerate it, or copy it into a new hand-authored\n");
        sb.Append($"// tune. How it was made — engine, resolved build, fuel, recipe, policy, origin of every field — is in the {RecordExtension}\n");
        sb.Append("// record beside it.\n");
        sb.Append("{\n  \"tunes\": [\n    {\n");
        sb.Append(string.Join(",\n", props.Select(x => "      " + x)));
        sb.Append("\n    }\n  ]\n}\n");
        return sb.ToString();
    }

    /// <summary>
    /// The record written beside the tune file <paramref name="fileName"/>: a one-recipe tune manifest (the format
    /// <c>regenerate-tunes</c> reads) whose recipe carries the <see cref="GenerationRecord"/>. Deterministic.
    /// </summary>
    public static string WriteRecord(TuneGeneration g, string fileName)
    {
        var plan = g.Plan ?? throw new InvalidOperationException("Nothing was generated.");
        var text = g.Text ?? throw new InvalidOperationException("Nothing was generated.");
        var record = new GenerationRecord(Name, Version, plan.Policy.Id, plan.RecipeSource, plan.Manifest,
            plan.Engine.Slots.Where(s => plan.Assembly.IsInstalled(s.Id)).Select(s => new[] { s.Id, plan.Assembly.PartIn(s.Id)!.Definition.Id }).ToList(),
            plan.Fields, plan.Notes, g.Iterations, Sha256(text));
        var recipe = plan.Recipe with { File = fileName, Generated = record };
        string json = JsonSerializer.Serialize(new TuneManifest(new[] { recipe }), RecordOptions).Replace("\r\n", "\n");
        return $"// How {fileName} was made ({Name}, version {Version}). A tune manifest with one recipe; output_sha256 is the digest\n" +
               "// of the tune file as written: the generator rewrites that file only while it still matches.\n" + json + "\n";
    }

    /// <summary>The record's path for the tune file <paramref name="tunePath"/> (<c>x.json</c> → <c>x.recipe.jsonc</c>).</summary>
    public static string RecordPathFor(string tunePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(tunePath)) ?? "", Path.GetFileNameWithoutExtension(tunePath) + RecordExtension);

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Writes the generated tune to <paramref name="tunePath"/> and its record beside it — never over a file the generator did
    /// not write: an existing tune file is replaced only when the record beside it says the generator wrote it, its digest
    /// still matches (an untouched generator output) and it records the same request — engine, fuel, tune id and policy tune
    /// (<see cref="AnotherRequest"/>). A hand-authored or hand-edited file, or another request's output, is refused.
    /// With <paramref name="check"/> nothing is written: exit 0 when the files already hold exactly this output, 4 when not.
    /// </summary>
    public static WriteOutcome Write(TuneGeneration g, string tunePath, bool check = false)
    {
        if (g.Tune == null || g.Text == null) return new WriteOutcome(2, "Nothing to write: the generation was refused or not run.");
        if (!tunePath.EndsWith(".json", StringComparison.Ordinal))
            return new WriteOutcome(1, "--out must name a .json file (the tune in the content format, loadable as a content layer).");
        string path = Path.GetFullPath(tunePath), recordPath = RecordPathFor(path), fileName = Path.GetFileName(path);
        string record = WriteRecord(g, fileName);
        bool tuneExists = File.Exists(path), recordExists = File.Exists(recordPath);
        if (tuneExists || recordExists)
        {
            if (!GeneratorOwns(path, recordPath, out string why))
                return new WriteOutcome(2, $"REFUSED: {tunePath} {why}. The generator never overwrites a hand-authored or hand-edited tune: " +
                                           "choose another --out, or move the file away yourself.");
            if (AnotherRequest(recordPath, g.Plan!) is { } other)
                return new WriteOutcome(2, $"REFUSED: {tunePath} is an untouched generator output, but of another generation request ({other}). " +
                                           "The generator replaces only its own output for the same engine, fuel, tune id and policy tune: " +
                                           "choose another --out, or move the file away yourself.");
            if (File.ReadAllText(path) == g.Text && File.ReadAllText(recordPath) == record)
                return new WriteOutcome(0, $"UNCHANGED: {tunePath} already holds exactly this generated tune (regeneration is identical).");
            if (check)
                return new WriteOutcome(4, $"DIFFERENT: regenerating would change {tunePath} " +
                                           $"({(File.ReadAllText(path) == g.Text ? "its record" : "the tune")}); run without --check 1 to rewrite it.");
            File.WriteAllText(path, g.Text);
            File.WriteAllText(recordPath, record);
            return new WriteOutcome(0, $"REGENERATED: {tunePath} and {Path.GetFileName(recordPath)} rewritten (an untouched generator output).");
        }
        if (check) return new WriteOutcome(2, $"Nothing to check: {tunePath} does not exist.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, g.Text);
        File.WriteAllText(recordPath, record);
        return new WriteOutcome(0, $"WRITTEN: {tunePath} and {Path.GetFileName(recordPath)}.");
    }

    /// <summary>Whether the tune file at <paramref name="path"/> is an untouched output of this generator (see <see cref="Write"/>).</summary>
    public static bool GeneratorOwns(string path, string recordPath, out string why)
    {
        if (!File.Exists(path)) { why = $"has no tune file, but a record ({Path.GetFileName(recordPath)}) exists without it"; return false; }
        if (!File.Exists(recordPath)) { why = $"exists and is not a generator output (no {Path.GetFileName(recordPath)} record beside it)"; return false; }
        TuneRecipe? recipe;
        try { recipe = TuneManifest.Load(recordPath).Tunes is { Count: 1 } tunes ? tunes[0] : null; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException) { recipe = null; }
        if (recipe?.Generated == null || recipe.File != Path.GetFileName(path))
        {
            why = $"has a {Path.GetFileName(recordPath)} beside it that is not this generator's record of it";
            return false;
        }
        if (recipe.Generated.OutputSha256 != Sha256(File.ReadAllText(path)))
        {
            why = "was edited after it was generated (it no longer matches its record's output_sha256)";
            return false;
        }
        why = "";
        return true;
    }

    /// <summary>
    /// How the generation request recorded in <paramref name="recordPath"/> differs from <paramref name="plan"/>'s — engine,
    /// fuel, tune id, policy tune — or null when it is the same request (review finding P3-002: the digest proves the generator
    /// wrote the file, not that it is this request's output). A record that lacks any of them (older or damaged) is never
    /// assumed to match.
    /// </summary>
    public static string? AnotherRequest(string recordPath, TunePlan plan)
    {
        TuneRecipe? recorded;
        try { recorded = TuneManifest.Load(recordPath).Tunes is { Count: 1 } tunes ? tunes[0] : null; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or IOException) { recorded = null; }
        if (recorded?.Generated == null) return "its record cannot be read";
        var differences = new[]
            {
                ("engine", recorded.Engine, plan.Engine.Id), ("fuel", recorded.Fuel, plan.Fuel.Id), ("tune id", recorded.Tune, plan.Recipe.Tune),
                ("policy tune", recorded.Generated.PolicyTune, plan.Policy.Id),
            }
            .Where(x => !string.Equals(x.Item2, x.Item3, StringComparison.Ordinal))
            .Select(x => $"{x.Item1} {(string.IsNullOrEmpty(x.Item2) ? "(not recorded)" : x.Item2)}, not {x.Item3}")
            .ToList();
        return differences.Count == 0 ? null : "it was generated for " + string.Join(", ", differences);
    }

    private static TuneGeneration Refuse(GenerationRequest request, EngineCheckReport check, string code, string message) =>
        new() { Request = request, Check = check, Refusals = new[] { new GenerationRefusal(code, message) } };

    private static TuneGeneration With(TuneGeneration planned, params GenerationRefusal[] refusals) =>
        new() { Request = planned.Request, Check = planned.Check, Plan = planned.Plan, Refusals = refusals };
}
