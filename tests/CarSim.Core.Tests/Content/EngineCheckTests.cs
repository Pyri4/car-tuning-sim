using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CarSim.Core.Content;
using CarSim.Core.Engines;

namespace CarSim.Core.Tests.Content;

/// <summary>
/// `carsim check-engine` (Engine Authoring Factory 1.0, Phase 2): assembly-aware validation of one engine. Every negative
/// case asserts the reason (code and section), not only that the check fails. Fixtures are test layers over the base
/// content and the synthetic matrix; none ships.
/// </summary>
public class EngineCheckTests
{
    private static IEnumerable<(string, string)> Files(string root, string prefix) =>
        Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (prefix + Path.GetRelativePath(root, f), File.ReadAllText(f)));

    private static ContentLoadResult Load(string? layer = null)
    {
        var layers = new List<IEnumerable<(string, string)>>
        {
            Files(TestContent.BaseContentPath, ""),
            Files(Path.Combine(TestContent.MatrixLayersPath, "engine-matrix"), "test/"),
        };
        if (layer != null) layers.Add(new[] { ("fixture/layer.json", layer) });
        return ContentLoader.LoadFromLayers(layers);
    }

    private static EngineCheckReport Check(string engineId, string? layer = null) => EngineCheck.Run(Load(layer), engineId);

    /// <summary>The M54's engine definition as a JSON object under another id, for edits a variant cannot express.</summary>
    private static JsonObject M54Copy(string id)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(TestContent.BaseContentPath, "engines", "isar_m54.json")), documentOptions: options)!;
        var engine = doc["engines"]![0]!.DeepClone().AsObject();
        engine["id"] = id;
        return engine;
    }

    private static string Engines(params JsonObject[] engines) => new JsonObject { ["engines"] = new JsonArray(engines) }.ToJsonString();

    private static CheckFinding Finding(EngineCheckReport r, string code) =>
        r.Findings.FirstOrDefault(f => f.Code == code) ?? throw new Xunit.Sdk.XunitException(
            $"No '{code}' finding. Report:\n{r.ToText(verbose: true)}");

    // ---- Valid engines -----------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> AllEngines => Load().Database.Engines.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(AllEngines))]
    public void EveryShippedAndSyntheticEnginePasses(string engineId)
    {
        var r = Check(engineId);
        Assert.True(r.Passed, r.ToText(verbose: true));
        Assert.Equal(0, r.ExitCode());
        Assert.DoesNotContain(r.Findings, f => f.Section is CheckSection.Parts or CheckSection.Topology or CheckSection.Interfaces
                                               && f.Severity != IssueSeverity.Info);
    }

    [Fact]
    public void TheFictionalK20PassesWithoutWarnings()
    {
        var r = Check(TestContent.K20);
        Assert.Empty(r.Warnings);
        Assert.Equal(0, r.ExitCode(strict: true));
        Assert.Equal(IssueSeverity.Info, Finding(r, "provenance_incomplete").Severity); // fictional: design values, no sources
    }

    [Fact]
    public void AValidRealEngineWithEstimatedDataPassesAndSaysWhatItRestsOn()
    {
        var r = Check(TestContent.M54);
        Assert.True(r.Passed);
        Assert.Equal(0, r.ExitCode());
        Assert.Equal(3, r.ExitCode(strict: true)); // its warnings (unmodelled features, missing provenance) block --strict
        Assert.True(r.Provenance!.ByType[ProvenanceTypes.Estimated] > 0); // estimated values are accepted, and counted
        Assert.Equal(0, r.Provenance.ByType[ProvenanceTypes.Fitted]);
        Assert.Equal(r.Provenance.Authored, r.Provenance.Recorded + r.Provenance.Unrecorded);
        var missing = Finding(r, "provenance_incomplete");
        Assert.Equal((CheckSection.Provenance, IssueSeverity.Warning), (missing.Section, missing.Severity));
        Assert.Contains($"{r.Provenance.Unrecorded} authored value", missing.Message);
        Assert.Contains(r.Details, d => d.StartsWith("m54.cams.oem: no provenance for", StringComparison.Ordinal) && d.Contains("lobe_separation_deg"));
        Assert.Equal(5, r.Findings.Count(f => f.Code == "feature_not_modelled"));
        Assert.Contains(r.Facts, f => f.Section == CheckSection.Interfaces && f.Text.Contains("isar_c30"));
    }

    // ---- Identity ----------------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnknownEngineIsReportedWithTheLoadedOnes()
    {
        var r = Check("no_such_engine");
        var f = Finding(r, "unknown_engine");
        Assert.Equal(CheckSection.Identity, f.Section);
        Assert.Contains(TestContent.M54, f.Message);
        Assert.Equal(2, r.ExitCode());
    }

    [Fact]
    public void AnAbstractFamilyIsRejectedAndItsVariantsNamed()
    {
        var r = Check("m54_family", """
            { "engines": [ { "id": "m54_family", "abstract": true, "extends": "isar_m54", "name": "M54 family" },
                           { "id": "m54_variant", "extends": "m54_family", "name": "M54 variant" } ] }
            """);
        var f = Finding(r, "abstract_engine");
        Assert.Equal((CheckSection.Identity, IssueSeverity.Error), (f.Section, f.Severity));
        Assert.Contains("m54_variant", f.Message);
        Assert.Equal(2, r.ExitCode());
        Assert.True(Check("m54_variant", """
            { "engines": [ { "id": "m54_family", "abstract": true, "extends": "isar_m54", "name": "M54 family" },
                           { "id": "m54_variant", "extends": "m54_family", "name": "M54 variant" } ] }
            """).Passed);
    }

    [Fact]
    public void AnUnknownParentBlocksTheVariantWithTheLoadersReason()
    {
        var r = Check("orphan", """{ "engines": [ { "id": "orphan", "extends": "no_such_family", "name": "Orphan" } ] }""");
        Assert.Contains(r.Errors, f => f.Section == CheckSection.Content && f.Message.Contains("unknown engine 'no_such_family'"));
        Assert.Contains("did not load", Finding(r, "unknown_engine").Message);
        Assert.Equal(2, r.ExitCode());
    }

    [Fact]
    public void AVariantInheritanceCycleBlocksTheVariant()
    {
        var r = Check("a", """{ "engines": [ { "id": "a", "extends": "b", "name": "A" }, { "id": "b", "extends": "a", "name": "B" } ] }""");
        Assert.Contains(r.Errors, f => f.Section == CheckSection.Content && f.Message.Contains("cycle"));
        Assert.False(r.Passed);
    }

    [Fact]
    public void ARealEngineWithoutFamilyOrReferenceIsFlagged()
    {
        var r = Check("anon", """{ "engines": [ { "id": "anon", "extends": "isar_m54", "name": "Anonymous", "identity": { "family": null, "reference": null } } ] }""");
        Assert.True(r.Passed);
        Assert.Equal(CheckSection.Identity, Finding(r, "identity_family_missing").Section);
        Assert.Equal(CheckSection.Identity, Finding(r, "identity_reference_missing").Section);
        var noIdentity = Check("syn_copy", """{ "engines": [ { "id": "syn_copy", "extends": "syn_i4_sohc", "name": "Copy", "identity": null } ] }""");
        Assert.Equal(IssueSeverity.Warning, Finding(noIdentity, "identity_missing").Severity);
    }

    // ---- Parts, topology, interfaces -----------------------------------------------------------------------------

    [Fact]
    public void AMissingRequiredStockPartBlocks()
    {
        var engine = M54Copy("no_oil_pump");
        engine["stock_parts"]!.AsObject().Remove("oil_pump");
        var r = Check("no_oil_pump", Engines(engine));
        var f = Finding(r, "stock_part_missing");
        Assert.Equal((CheckSection.Parts, IssueSeverity.Error), (f.Section, f.Severity));
        Assert.Contains("'oil_pump'", f.Message);
        Assert.Equal(CheckSection.Parts, Finding(r, "missing_part").Section);
        Assert.Equal(2, r.ExitCode());
    }

    [Fact]
    public void APartWithTheWrongInterfaceBlocksUnderInterfaces()
    {
        var r = Check("k20_head_on_six", """
            { "engines": [ { "id": "k20_head_on_six", "extends": "isar_m54", "name": "Wrong head",
                             "stock_parts": { "cylinder_head": "k20.head.oem" } } ] }
            """);
        var f = r.Errors.First(x => x.Code == "missing_interface");
        Assert.Equal(CheckSection.Interfaces, f.Section);
        Assert.Contains("'k20.deck'", r.Errors.Where(x => x.Code == "missing_interface").Select(x => x.Message).Aggregate((a, b) => a + b));
    }

    [Fact]
    public void ADuplicateComponentForOneBankIsATopologyError()
    {
        var engine = M54Copy("two_heads");
        engine["slots"]!.AsArray().Add(JsonNode.Parse("""{ "id": "cylinder_head_2", "category": "cylinder_head", "install_after": ["head_gasket"] }"""));
        engine["stock_parts"]!["cylinder_head_2"] = "m54.head.oem";
        var r = Check("two_heads", Engines(engine));
        var f = Finding(r, "unsupported_topology");
        Assert.Equal((CheckSection.Topology, IssueSeverity.Error), (f.Section, f.Severity));
        Assert.Contains("'cylinder_head', 'cylinder_head_2'", f.Message.Replace("\"", "'"));
    }

    [Fact]
    public void ASlotOnANonexistentBankIsATopologyError()
    {
        var engine = M54Copy("bad_bank");
        engine["slots"]!.AsArray().Single(s => (string?)s!["id"] == "cylinder_head")!["banks"] = new JsonArray("left");
        var r = Check("bad_bank", Engines(engine));
        Assert.Contains(r.Errors, f => f.Section == CheckSection.Topology && f.Message.Contains("names bank 'left'"));
        Assert.False(r.Passed);
    }

    [Fact]
    public void AnImpossibleLayoutIsATopologyError()
    {
        var engine = M54Copy("v_without_banks");
        engine["layout"] = "v";
        var r = Check("v_without_banks", Engines(engine));
        Assert.Contains(r.Errors, f => f.Section == CheckSection.Topology && f.Code == "unsupported_topology");
    }

    [Fact]
    public void AnAssemblyOrderCycleIsReportedNotThrown()
    {
        var engine = M54Copy("cycle_slots");
        engine["slots"]!.AsArray().Single(s => (string?)s!["id"] == "block")!["install_after"] = new JsonArray("crankshaft");
        var r = Check("cycle_slots", Engines(engine));
        Assert.False(r.Passed);
        Assert.Contains(r.Errors, f => f.Message.Contains("cycle"));
    }

    // ---- Geometry, part variants -------------------------------------------------------------------------------------

    [Fact]
    public void AnIncompatiblePartVariantIsAGeometryError()
    {
        var r = Check("big_bore_pistons", """
            { "parts": [ { "id": "m54.pistons.86mm", "extends": "m54.pistons.oem", "name": "86 mm pistons", "spec": { "bore_mm": 86.0 } } ],
              "engines": [ { "id": "big_bore_pistons", "extends": "isar_m54", "name": "Wrong pistons",
                             "stock_parts": { "pistons": "m54.pistons.86mm" } } ] }
            """);
        var f = Finding(r, "piston_bore_mismatch");
        Assert.Equal((CheckSection.Geometry, IssueSeverity.Error), (f.Section, f.Severity));
        Assert.Equal(2, r.ExitCode());
    }

    [Fact]
    public void PistonsThatHitTheHeadAreAGeometryError()
    {
        var r = Check("tall_pistons", """
            { "parts": [ { "id": "m54.pistons.tall", "extends": "m54.pistons.oem", "name": "Tall pistons", "spec": { "compression_height_mm": 33.0 } } ],
              "engines": [ { "id": "tall_pistons", "extends": "isar_m54", "name": "Tall pistons", "stock_parts": { "pistons": "m54.pistons.tall" } } ] }
            """);
        Assert.Equal(CheckSection.Geometry, Finding(r, "piston_head_contact").Section);
        Assert.False(r.Passed);
    }

    [Fact]
    public void ImplausibleButLoadableGeometryIsAWarningNotAnError()
    {
        // A 50 mm stroke under the M54's 84 mm bore: legal content, unusual engine. The author is told, not stopped.
        var r = Check("short_stroke", """
            { "parts": [ { "id": "m54.crank.60", "extends": "m54.crankshaft.oem", "name": "50 mm crank", "spec": { "stroke_mm": 50.0 } } ],
              "engines": [ { "id": "short_stroke", "extends": "isar_m54", "name": "Short stroke", "stock_parts": { "crankshaft": "m54.crank.60" } } ] }
            """);
        var f = Finding(r, "implausible_bore_stroke");
        Assert.Equal((CheckSection.Geometry, IssueSeverity.Warning), (f.Section, f.Severity));
        Assert.Contains("1.68", f.Message);
    }

    [Fact]
    public void AnInvalidProvenanceRecordOnAStockPartBlocks()
    {
        var r = Check("bad_prov", """
            { "parts": [ { "id": "m54.pump.x", "extends": "m54.oil_pump.oem", "name": "Pump", "provenance": { "relief_pressure_kpa": { "type": "guessed" } } } ],
              "engines": [ { "id": "bad_prov", "extends": "isar_m54", "name": "Bad provenance", "stock_parts": { "oil_pump": "m54.pump.x" } } ] }
            """);
        Assert.Contains(r.Errors, f => f.Section == CheckSection.Content && f.Message.Contains("m54.pump.x") && f.Message.Contains("type must be one of"));
        Assert.Equal(2, r.ExitCode());
    }

    // ---- Features ----------------------------------------------------------------------------------------------------

    [Fact]
    public void AMissingFeatureDeclarationOnARealEngineIsAWarning()
    {
        var r = Check("undeclared", """{ "engines": [ { "id": "undeclared", "extends": "isar_m54", "name": "Undeclared", "identity": { "features": [] } } ] }""");
        var undeclared = r.Findings.Where(f => f.Code == "feature_undeclared").ToList();
        Assert.Equal(new[] { "intake_cam_phasing", "variable_intake" }, undeclared.Select(f => f.Message.Split(' ')[4]).OrderBy(s => s, StringComparer.Ordinal));
        Assert.All(undeclared, f => Assert.Equal((CheckSection.Features, IssueSeverity.Warning), (f.Section, f.Severity)));
        Assert.True(r.Passed);
    }

    [Fact]
    public void AModelledFeatureWithoutItsHardwareBlocks()
    {
        var r = Check("no_turbo", """
            { "engines": [ { "id": "no_turbo", "extends": "isar_m54", "name": "Claims a turbo",
                             "identity": { "features": [ { "feature": "turbocharger" } ] } } ] }
            """);
        var f = Finding(r, "feature_missing_data");
        Assert.Equal((CheckSection.Features, IssueSeverity.Error), (f.Section, f.Severity));
        Assert.Contains("turbocharger", f.Message);
    }

    /// <summary>
    /// The B58 negative fixture: a B58-style declaration (turbo inline engine with direct injection, Valvetronic,
    /// double VANOS, twin-scroll turbine) on the synthetic turbo four as the stand-in hardware. Validation content only:
    /// the physics is not extended, and the check must say exactly which features the simulator lacks.
    /// </summary>
    private const string B58Fixture = """
        { "engines": [ { "id": "b58_fixture", "extends": "syn_i4_turbo", "name": "B58-style declaration (fixture)",
            "identity": { "kind": "real", "manufacturer": "BMW", "family": "B58", "variant": "B58B30 (fixture)",
              "features": [
                { "feature": "turbocharger" },
                { "feature": "intake_cam_phasing" },
                { "feature": "exhaust_cam_phasing", "approximation": "omitted" },
                { "feature": "direct_injection", "approximation": "port injection" },
                { "feature": "variable_valve_lift_continuous", "approximation": "omitted" },
                { "feature": "twin_scroll_turbine", "approximation": "single-scroll turbine" } ] } } ] }
        """;

    [Fact]
    public void TheB58FixtureExposesEveryUnsupportedCapability()
    {
        var r = Check("b58_fixture", B58Fixture);
        var notModelled = r.Findings.Where(f => f.Code == "feature_not_modelled").Select(f => f.Message.Split(' ')[0]).OrderBy(s => s, StringComparer.Ordinal);
        Assert.Equal(new[] { "direct_injection", "exhaust_cam_phasing", "twin_scroll_turbine", "variable_valve_lift_continuous" }, notModelled);
        Assert.Equal(FeatureStatus.Supported, r.Features.Single(f => f.Feature == "turbocharger").Status);
        Assert.Equal(FeatureStatus.Supported, r.Features.Single(f => f.Feature == "intake_cam_phasing").Status);
        Assert.Contains("stands in: port injection", r.Findings.Single(f => f.Message.StartsWith("direct_injection", StringComparison.Ordinal)).Message);
        Assert.True(r.Passed);                    // declared with approximations: honest, not blocking by default
        Assert.Equal(3, r.ExitCode(strict: true)); // but never "fully supported"
        Assert.Equal(CheckSection.Identity, Finding(r, "identity_reference_missing").Section);
    }

    [Fact]
    public void AnUnmodelledFeatureWithoutAnApproximationBlocks()
    {
        var r = Check("b58_fixture", B58Fixture.Replace("""{ "feature": "direct_injection", "approximation": "port injection" }""", """{ "feature": "direct_injection" }"""));
        Assert.Contains(r.Errors, f => f.Section == CheckSection.Content && f.Message.Contains("'direct_injection' is not modelled"));
        Assert.Equal(2, r.ExitCode());
    }

    // ---- LS3 readiness (architecture only; no LS3 content) ------------------------------------------------------------

    [Fact]
    public void AnLs3StyleArchitectureIsCheckableWithTodaysCapabilities()
    {
        // The pushrod V8 of the synthetic matrix under a real-engine identity with a cross-plane firing order: what an
        // LS3 author would start from. It must pass, with its missing sources reported, not rejected.
        var r = Check("ls3_style", """
            { "engines": [ { "id": "ls3_style", "extends": "syn_v8_ohv", "name": "LS3-style architecture (fixture)",
                "firing_order": [1, 8, 7, 2, 6, 5, 4, 3],
                "identity": { "kind": "real", "family": "LS", "variant": "LS3 (fixture)", "features": [] } } ] }
            """);
        Assert.True(r.Passed, r.ToText(verbose: true));
        Assert.Contains(r.Facts, f => f.Section == CheckSection.Architecture && f.Text.Contains("V8") && f.Text.Contains("OHV"));
        Assert.Contains(r.Facts, f => f.Text == "firing order 1-8-7-2-6-5-4-3");
        Assert.Equal(IssueSeverity.Warning, Finding(r, "provenance_incomplete").Severity);
        // An L99-style variant's cylinder deactivation is reported as not modelled.
        var l99 = Check("l99_style", """
            { "engines": [ { "id": "l99_style", "extends": "syn_v8_ohv", "name": "L99-style (fixture)",
                "identity": { "kind": "real", "family": "LS", "features": [ { "feature": "cylinder_deactivation", "approximation": "omitted" } ] } } ] }
            """);
        Assert.Contains(l99.Findings, f => f.Code == "feature_not_modelled" && f.Message.StartsWith("cylinder_deactivation", StringComparison.Ordinal));
    }

    // ---- Completeness --------------------------------------------------------------------------------------------------

    [Fact]
    public void AStockTuneThatDisagreesWithTheBuildIsReported()
    {
        var r = Check("wrong_tune", """{ "engines": [ { "id": "wrong_tune", "extends": "isar_m54", "name": "Wrong tune", "stock_tune": "k20.stock" } ] }""");
        Assert.Equal(CheckSection.Completeness, Finding(r, "tune_displacement_mismatch").Section);
        Assert.Equal(CheckSection.Completeness, Finding(r, "tune_injector_flow_mismatch").Section);
        Assert.Contains("1998 cc", Finding(r, "tune_displacement_mismatch").Message);
    }

    [Fact]
    public void AnEngineWithoutATuneYetCanStillPassItsCheck()
    {
        var r = Check("untuned", """{ "engines": [ { "id": "untuned", "extends": "isar_m54", "name": "Untuned", "stock_tune": "" } ] }""");
        Assert.True(r.Passed); // the pipeline is data → check-engine → tune generation
        Assert.Equal(IssueSeverity.Info, Finding(r, "no_stock_tune").Severity);
    }

    // ---- Determinism, exit codes, rule coverage ----------------------------------------------------------------------

    [Fact]
    public void TheReportIsDeterministic()
    {
        string a = Check(TestContent.M54).ToText(verbose: true), b = Check(TestContent.M54).ToText(verbose: true);
        Assert.Equal(a, b);
        string c = EngineCheck.Run(Load(B58Fixture), "b58_fixture").ToText(verbose: true);
        Assert.Equal(c, EngineCheck.Run(Load(B58Fixture), "b58_fixture").ToText(verbose: true));
        Assert.Contains("\nRESULT  PASS with", a);
    }

    [Fact]
    public void ExitCodesFollowTheVerdict()
    {
        Assert.Equal((0, 0), (Check(TestContent.K20).ExitCode(), Check(TestContent.K20).ExitCode(strict: true)));
        Assert.Equal((0, 3), (Check(TestContent.M54).ExitCode(), Check(TestContent.M54).ExitCode(strict: true)));
        Assert.Equal((2, 2), (Check("nope").ExitCode(), Check("nope").ExitCode(strict: true)));
        Assert.StartsWith("BLOCKED:", Check("nope").Verdict());
        Assert.StartsWith("BLOCKED under --strict", Check(TestContent.M54).Verdict(strict: true));
    }

    [Fact]
    public void EveryAssemblyValidatorCodeHasASection()
    {
        // A new validator rule must say where it is reported (otherwise it would land under Parts silently).
        string source = File.ReadAllText(Path.Combine(TestContent.RepoRoot, "src", "CarSim.Core", "Engines", "AssemblyValidator.cs"));
        var codes = Regex.Matches(source, "(?:IssueSeverity\\.\\w+|CheckFit\\([^,]+,[^,]+),\\s*\"([a-z_]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(codes.Count >= 30, $"found {codes.Count} codes");
        Assert.All(codes, code => Assert.True(EngineCheck.ValidatorSections.ContainsKey(code), $"'{code}' has no section"));
    }
}
