using System.Text.Json;
using System.Text.Json.Nodes;
using CarSim.Core.Content;
using CarSim.Verification;
using CarSim.Verification.Calibration;

namespace CarSim.Core.Tests.Verification;

/// <summary>
/// Content for the derived-value and tune-generation tests (Engine Authoring Factory 1.0, Phase 3): the base content, the
/// synthetic matrix, and test-only layers of variants over the synthetic pushrod V8. Nothing here ships.
/// </summary>
public static class GeneratorFixtures
{
    private static IEnumerable<(string, string)> Files(string root, string prefix) =>
        Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (prefix + Path.GetRelativePath(root, f), File.ReadAllText(f)));

    /// <summary>Base content, the synthetic matrix and, optionally, test layers on top (errors included, as check-engine loads).</summary>
    public static ContentLoadResult Load(params string[] layers)
    {
        var all = new List<IEnumerable<(string, string)>>
        {
            Files(TestContent.BaseContentPath, ""),
            Files(Path.Combine(TestContent.MatrixLayersPath, "engine-matrix"), "test/"),
        };
        all.AddRange(layers.Select((layer, i) => new[] { ($"fixture/layer{i}.json", layer) }));
        return ContentLoader.LoadFromLayers(all);
    }

    public static TuneManifest Manifest { get; } = TuneManifest.Load(RepoPaths.TuneManifest);

    /// <summary>
    /// Variants of the synthetic V8:
    /// <list type="bullet">
    /// <item><c>fixture_v8_stroker</c>: an engine variant on part variants — a 75 mm crank (4 mm more stroke) with 2 mm shorter rods (the same deck
    /// clearance) and 320 cc/min injectors;</item>
    /// <item><c>fixture_v8_bad_pistons</c>: 97 mm pistons in its 94 mm bores (check-engine error);</item>
    /// <item><c>fixture_v8_untuned</c>: no stock tune;</item>
    /// <item><c>fixture_v8d_basic_ecu</c>: the DOHC V8 with an ECU that cannot drive its phasers;</item>
    /// <item><c>fixture_i4t_b58_like</c>: the turbo four declaring hardware the simulator does not model (as check-engine's B58 fixture).</item>
    /// </list>
    /// </summary>
    public const string Variants = """
        {
          "parts": [
            { "id": "fixture.v8.crankshaft.stroker", "extends": "syn.v8.crankshaft", "name": "Stroker crankshaft (75 mm)", "spec": { "stroke_mm": 75 } },
            { "id": "fixture.v8.rods.short", "extends": "syn.v8.rods", "name": "Short rods (143 mm)", "spec": { "length_mm": 143 } },
            { "id": "fixture.v8.injectors.320cc", "extends": "syn.injectors.8x260cc", "name": "320 cc/min injectors (set of 8)",
              "spec": { "flow_cc_min": 320, "dead_time_ms": 0.8 } },
            { "id": "fixture.v8.pistons.oversize", "extends": "syn.v8.pistons", "name": "Oversize pistons (97 mm)", "spec": { "bore_mm": 97 } },
            { "id": "fixture.v8.head_gasket.thick", "extends": "syn.v8.head_gasket", "name": "Thick head gasket", "spec": { "compressed_thickness_mm": 2.2 } }
          ],
          "engines": [
            { "id": "fixture_v8_stroker", "extends": "syn_v8_ohv", "name": "Synth V8 stroker (fixture)",
              "stock_parts": { "crankshaft": "fixture.v8.crankshaft.stroker", "connecting_rods": "fixture.v8.rods.short",
                               "injectors": "fixture.v8.injectors.320cc" } },
            { "id": "fixture_v8_bad_pistons", "extends": "syn_v8_ohv", "name": "Synth V8, pistons too big (fixture)",
              "stock_parts": { "pistons": "fixture.v8.pistons.oversize" } },
            { "id": "fixture_v8_untuned", "extends": "syn_v8_ohv", "name": "Synth V8 without a tune (fixture)", "stock_tune": "" },
            { "id": "fixture_v8d_basic_ecu", "extends": "syn_v8_dohc_vvt", "name": "Synth DOHC V8 on a basic ECU (fixture)",
              "stock_parts": { "ecu": "syn.ecu.basic" } },
            { "id": "fixture_i4t_b58_like", "extends": "syn_i4_turbo", "name": "B58-style declaration (fixture)",
              "identity": { "kind": "fictional", "manufacturer": "Synth", "family": "B58-like",
                "features": [
                  { "feature": "turbocharger" },
                  { "feature": "intake_cam_phasing" },
                  { "feature": "exhaust_cam_phasing", "approximation": "omitted" },
                  { "feature": "direct_injection", "approximation": "port injection" } ] } }
          ]
        }
        """;

    /// <summary>
    /// A layer with a copy of the tune <paramref name="tuneId"/> (from the content file <paramref name="file"/>, relative to the
    /// repository) under <paramref name="newId"/>, edited by <paramref name="edit"/>.
    /// </summary>
    public static string TuneCopy(string file, string tuneId, string newId, Action<JsonObject> edit)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(TestContent.RepoRoot, file)), documentOptions: options)!;
        var tune = doc["tunes"]!.AsArray().Single(t => (string?)t!["id"] == tuneId)!.DeepClone().AsObject();
        tune["id"] = newId;
        edit(tune);
        return new JsonObject { ["tunes"] = new JsonArray(tune) }.ToJsonString();
    }

    /// <summary>A plan written out as if it had been generated (no calibration run): for tests of the writer alone.</summary>
    public static TuneGeneration Uncalibrated(TuneGeneration planned) => new()
    {
        Request = planned.Request,
        Check = planned.Check,
        Plan = planned.Plan,
        Tune = planned.Plan!.Skeleton,
        Text = TuneGenerator.WriteTuneFile(planned.Plan.Skeleton, planned.Plan.CalibratedFields),
    };

    /// <summary>A fresh empty directory under the system's temporary directory.</summary>
    public static string TempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "carsim-generate-tune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
