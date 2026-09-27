using System.Text.Json;
using System.Text.Json.Nodes;
using CarSim.Core.Content;
using CarSim.Core.Damage;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Architecture;

/// <summary>
/// Architecture invariants, as properties of the simulation rather than of its code: take a synthetic engine, change
/// one structural fact about it in its JSON (cylinder count, bank count, air paths, valves, turbos, runners, cam
/// profiles, interfaces), and check that the simulation follows the data — never an assumption about the original
/// configuration. Where two descriptions of the same physical engine exist (two alike banks, or one bank holding every
/// cylinder), they must give the same answer.
/// </summary>
public class ArchitectureInvariantTests
{
    // ---- Content variants (base game + synthetic matrix, edited as JSON) --------------------------------------------

    private sealed class Docs
    {
        private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        public readonly List<(string Source, JsonNode Node)> Files;

        public Docs()
        {
            Files = new[] { TestContent.BaseContentPath, Path.Combine(TestContent.MatrixLayersPath, "engine-matrix") }
                .SelectMany(root => Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
                    .Select(f => (Source: Path.GetRelativePath(TestContent.RepoRoot, f), Node: JsonNode.Parse(File.ReadAllText(f), documentOptions: Options)!)))
                .ToList();
        }

        public JsonObject Item(string array, string id) => Files.SelectMany(d => d.Node[array]?.AsArray() ?? new JsonArray())
            .Single(n => (string?)n!["id"] == id)!.AsObject();

        public JsonObject Spec(string partId) => Item("parts", partId)["spec"]!.AsObject();

        /// <summary>Adds a copy of <paramref name="partId"/> under <paramref name="newId"/> with its spec edited.</summary>
        public void Clone(string partId, string newId, Action<JsonObject> editSpec)
        {
            var copy = Item("parts", partId).DeepClone().AsObject();
            copy["id"] = newId;
            editSpec(copy["spec"]!.AsObject());
            Files.Add(("variant.json", new JsonObject { ["parts"] = new JsonArray(copy) }));
        }

        public JsonArray Slots(string engine) => Item("engines", engine)["slots"]!.AsArray();

        public ContentDatabase Load() => ContentLoader.LoadFromStrings(Files.Select(d => (d.Source, d.Node.ToJsonString()))).GetOrThrow();
    }

    private static EngineSimulation Sim(ContentDatabase db, string engine, string fuel = "gasoline_98", EcuTune? tune = null, Action<EngineAssembly>? edit = null)
    {
        var def = db.GetEngine(engine);
        tune ??= EcuTune.FromDocument(db.GetTune(def.StockTune));
        var a = EngineAssembly.CreateStock(def, db, new PartInstanceFactory());
        edit?.Invoke(a);
        var config = EngineConfiguration.Build(a, db.GetFuel(fuel), new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa)).GetOrThrow();
        return new EngineSimulation(config, tune, EngineState.Warm()) { DamageEnabled = false };
    }

    private static void Swap(EngineAssembly a, string slot, PartDefinition part)
    {
        Assert.True(a.Remove(slot, out _).Ok);
        Assert.True(a.Install(slot, new PartInstanceFactory(77_000_000).Create(part)).Ok);
    }

    private static void Near(double expected, double actual, double relative, string what) =>
        Assert.True(Math.Abs(actual - expected) <= relative * Math.Abs(expected), $"{what}: expected {expected:R}, got {actual:R}");

    /// <summary>
    /// Rewrites a banked family as one bank holding every cylinder: each banked slot collapses to its first bank's slot
    /// (id without the bank suffix), and parts that served one bank are replaced by clones with the flow of all banks.
    /// </summary>
    private static void CollapseToOneBank(Docs docs, string engine, string layout)
    {
        var e = docs.Item("engines", engine);
        var bankIds = e["banks"]!.AsArray().Select(b => (string)b!["id"]!).ToList();
        e.Remove("banks");
        e.Remove("bank_angle_deg");
        e["layout"] = layout;
        var slots = e["slots"]!.AsArray();
        var stock = e["stock_parts"]!.AsObject();
        string Strip(string id) => bankIds.Aggregate(id, (s, b) => s.EndsWith("_" + b, StringComparison.Ordinal) ? s[..^(b.Length + 1)] : s);
        var keep = new JsonArray();
        var seen = new HashSet<string>();
        foreach (var s in slots.Select(x => x!.AsObject()).ToList())
        {
            string id = (string)s["id"]!, newId = Strip(id);
            bool banked = s["banks"] != null;
            if (!seen.Add(newId)) { stock.Remove(id); continue; }
            var copy = s.DeepClone().AsObject();
            copy["id"] = newId;
            copy.Remove("banks");
            if (copy["install_after"] is JsonArray after)
                copy["install_after"] = new JsonArray(after.Select(x => Strip((string)x!)).Distinct().Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            keep.Add(copy);
            string partId = (string)stock[id]!;
            stock.Remove(id);
            // A part that served one bank now serves them all: give it the flow of all of them (restrictions only).
            var spec = docs.Spec(partId);
            if (banked && spec["flow_cfm"] is JsonValue flow)
            {
                docs.Clone(partId, partId + ".all_banks", sp => sp["flow_cfm"] = (double)flow * bankIds.Count);
                partId += ".all_banks";
            }
            stock[newId] = partId;
        }
        e["slots"] = keep;
    }

    // ---- Cylinder count ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public void CylinderCountIsData(int n)
    {
        // The synthetic 1.6 SOHC four as an n-cylinder inline engine: block, pistons, rods, injectors and the tune's
        // displacement follow n, and every restriction the cylinders share scales with them. Per cylinder, nothing
        // may change — no hidden "four".
        const string engine = "syn_i4_sohc";
        var docs = new Docs();
        var e = docs.Item("engines", engine);
        e["cylinders"] = n;
        e["firing_order"] = new JsonArray(Enumerable.Range(1, n).Select(i => (JsonNode)i).ToArray());
        foreach (var id in new[] { "syn.s16.block" }) docs.Spec(id)["cylinders"] = n;
        foreach (var id in new[] { "syn.s16.pistons", "syn.s16.rods" }) docs.Spec(id)["count"] = n;
        var stock = e["stock_parts"]!.AsObject();
        double k = n / 4.0;
        docs.Clone("injectors.310cc", "variant.injectors", s => s["count"] = n);
        stock["injectors"] = "variant.injectors";
        foreach (var slot in new[] { "intake_manifold", "throttle_body", "exhaust_manifold", "exhaust" })
        {
            string partId = (string)stock[slot]!;
            docs.Clone(partId, "variant." + slot, s => s["flow_cfm"] = (double)s["flow_cfm"]! * k);
            stock[slot] = "variant." + slot;
        }
        var tune = docs.Item("tunes", "syn.s16.stock");
        tune["displacement_cc"] = (double)tune["displacement_cc"]! * k;
        var db = docs.Load();
        Assert.Equal(n, db.GetEngine(engine).Banks[0].Cylinders.Count);

        var original = SimFactory.At(Sim(TestContent.Matrix, engine, "gasoline_95"), 3000);
        var variant = SimFactory.At(Sim(db, engine, "gasoline_95"), 3000);
        Near(original.AirPerCycle, variant.AirPerCycle, 1e-9, "air per cylinder per cycle");
        Near(original.Lambda, variant.Lambda, 1e-9, "λ");
        Near(original.Torque / 4, variant.Torque / n, 0.01, "torque per cylinder");
        Near(original.AirMassFlow * k, variant.AirMassFlow, 1e-9, "air flow");
    }

    // ---- Bank count ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("syn_h4_turbo", "inline")]   // two banks sharing one turbo, intake and exhaust
    [InlineData("syn_v8_ohv", "inline")]     // two banks with their own manifolds and exhausts, one pushrod camshaft
    public void TwoAlikeBanksAreTheSameEngineAsOneBankOfAllItsCylinders(string engine, string layout)
    {
        // The same physical engine described two ways must give the same answer: the per-bank air paths, the shares of
        // shared elements and the per-turbo shafts add up to the one-bank description (to rounding).
        var docs = new Docs();
        CollapseToOneBank(docs, engine, layout);
        var oneBank = docs.Load();
        Assert.Single(oneBank.GetEngine(engine).Banks);
        var fuel = TestContent.Matrix.Scenarios[engine + "_bench"].Fuel;
        foreach (double rpm in new[] { 2500.0, 5000 })
        {
            var banked = SimFactory.At(Sim(TestContent.Matrix, engine, fuel), rpm, 1.0, 3.0);
            var single = SimFactory.At(Sim(oneBank, engine, fuel), rpm, 1.0, 3.0);
            Near(banked.Torque, single.Torque, 1e-6, $"{engine} torque at {rpm}");
            Near(banked.AirMassFlow, single.AirMassFlow, 1e-6, $"{engine} air flow at {rpm}");
            Near(banked.ManifoldPressure, single.ManifoldPressure, 1e-6, $"{engine} MAP at {rpm}");
            if (banked.Turbos.Count > 0) Near(banked.TurboRpm, single.TurboRpm, 1e-6, $"{engine} turbo speed at {rpm}");
        }
    }

    [Fact]
    public void AlikeBanksStayIdenticalBankByBank()
    {
        // No bank is favoured: on every multi-bank engine of the matrix, alike banks run bit-identically.
        foreach (var engine in TestContent.MatrixFamilies.Where(f => TestContent.Matrix.GetEngine(f).Banks.Count > 1))
        {
            var fuel = TestContent.Matrix.Scenarios[engine + "_bench"].Fuel;
            var t = SimFactory.At(Sim(TestContent.Matrix, engine, fuel), 4500, 1.0, 2.0);
            Assert.True(t.Banks.Count > 1);
            foreach (var bank in t.Banks) Assert.Equal(t.Banks[0], bank);
            foreach (var turbo in t.Turbos) Assert.Equal(t.Turbos[0].ShaftRpm, turbo.ShaftRpm);
        }
    }

    [Fact]
    public void APartOnOneBankChangesThatBank()
    {
        // A restrictive exhaust manifold on the V8's right bank: that bank's exhaust backs up, costing it pumping work and
        // (through more residual gas) a little air; the left bank, which shares only the plenum with it, barely notices.
        var docs = new Docs();
        docs.Clone("syn.v8.exhaust_manifold.right", "variant.restrictive", s => s["flow_cfm"] = 150.0);
        docs.Item("engines", "syn_v8_ohv")["stock_parts"]!["exhaust_manifold_right"] = "variant.restrictive";
        var stock = SimFactory.At(Sim(TestContent.Matrix, "syn_v8_ohv", "gasoline_95"), 5000, 1.0, 3.0);
        var t = SimFactory.At(Sim(docs.Load(), "syn_v8_ohv", "gasoline_95"), 5000, 1.0, 3.0);
        var (left, right) = (t.Banks[0], t.Banks[1]);
        Assert.True(right.ExhaustPortPressure > left.ExhaustPortPressure * 1.10);
        Assert.True(right.AirPerCycle < left.AirPerCycle * 0.995);
        Assert.True(right.Torque < left.Torque * 0.97);
        Near(stock.Banks[0].AirPerCycle, left.AirPerCycle, 0.01, "left bank air");
        Assert.True(t.Torque < stock.Torque && t.Torque > 0.5 * stock.Torque);
        // The ECU fuels both banks from one MAP sensor: the starved bank runs richer than the other.
        Assert.True(right.Lambda < left.Lambda);
    }

    // ---- Per-bank geometry --------------------------------------------------------------------------------------------

    [Fact]
    public void AlikeBanksHaveTheSameGeometryAsTheEngine()
    {
        foreach (var family in TestContent.MatrixFamilies.Where(f => TestContent.Matrix.GetEngine(f).Banks.Count > 1))
        {
            var c = Sim(TestContent.Matrix, family).Config;
            Assert.All(c.Banks, b => Assert.Equal(c.Geometry, b.Geometry));
        }
    }

    [Theory]
    [InlineData("head_gasket_right", "syn.v8.head_gasket", "compressed_thickness_mm", 2.0, 1)] // a thick gasket on the second bank
    [InlineData("cylinder_head_left", "syn.v8.head", "chamber_volume_cc", 72.0, 0)]          // a big-chamber head on the first
    public void EachBankCompressesToItsOwnHeadAndGasket(string slot, string stockPart, string field, double value, int changed)
    {
        int other = 1 - changed;
        var docs = new Docs();
        docs.Clone(stockPart, "variant.bank_part", s => s[field] = value);
        docs.Item("engines", "syn_v8_ohv")["stock_parts"]![slot] = "variant.bank_part";
        var db = docs.Load();
        var stock = Sim(TestContent.Matrix, "syn_v8_ohv", "gasoline_95");
        var variant = Sim(db, "syn_v8_ohv", "gasoline_95");

        // Geometry: only the bank the part serves changes, in either direction (no bank inherits another's head or gasket).
        var (sb, vb) = (stock.Config.Banks, variant.Config.Banks);
        Assert.Equal(sb[other].Geometry, vb[other].Geometry);
        Assert.True(vb[changed].Geometry.CompressionRatio < sb[changed].Geometry.CompressionRatio - 0.5);
        Assert.Equal(sb[0].Geometry.Displacement, vb[0].Geometry.Displacement);

        // Workshop: the compression test and the validator name the bank that differs.
        var a = variant.Config.Assembly;
        var compression = EngineDiagnostics.CompressionTestByBank(a);
        var stockCompression = EngineDiagnostics.CompressionTestByBank(stock.Config.Assembly);
        Assert.Equal(stockCompression[other], compression[other]);
        Assert.True(compression[changed] < stockCompression[changed] - 1.0);
        Assert.Contains($"{a.Definition.Banks[changed].Id} {compression[changed]:F1} bar", EngineDiagnostics.DescribeCompressionTest(a));
        var ratios = AssemblyValidator.Validate(a).Issues.Where(i => i.Code == "compression_ratio").ToList();
        Assert.Equal(2, ratios.Count);
        for (int b = 0; b < 2; b++)
            Assert.StartsWith($"Bank '{a.Definition.Banks[b].Id}': Static compression ratio {vb[b].Geometry.CompressionRatio:F2}:1", ratios[b].Message);
        Assert.DoesNotContain("Bank", Assert.Single(AssemblyValidator.Validate(stock.Config.Assembly).Issues, i => i.Code == "compression_ratio").Message);

        // Simulation: the lower-compression bank has more knock margin and a lower peak pressure; the other bank runs as before.
        var ts = SimFactory.At(stock, 4500, 1.0, 3.0);
        var tv = SimFactory.At(variant, 4500, 1.0, 3.0);
        Assert.True(tv.Banks[changed].KnockLimitAdvance > ts.Banks[changed].KnockLimitAdvance + 1.0);
        Assert.True(tv.Banks[changed].PeakCylinderPressure < ts.Banks[changed].PeakCylinderPressure * 0.95);
        Near(ts.Banks[other].AirPerCycle, tv.Banks[other].AirPerCycle, 0.01, "other bank air");
        Near(ts.Banks[other].PeakCylinderPressure, tv.Banks[other].PeakCylinderPressure, 0.01, "other bank peak pressure");
        Near(ts.Banks[other].KnockLimitAdvance, tv.Banks[other].KnockLimitAdvance, 0.01, "other bank knock limit");
    }

    [Fact]
    public void ACompressionTestFindsTheBankWithTheFailedGasket()
    {
        var a = Sim(TestContent.Matrix, "syn_v6_na").Config.Assembly;
        a.PartFor(PartCategory.HeadGasket, 1)!.Damage.Fail(FailureMode.HeadGasketBreach, 0);
        var byBank = EngineDiagnostics.CompressionTestByBank(a);
        Assert.True(byBank[1] < 0.5 * byBank[0]);
        Assert.Equal(byBank[1], EngineDiagnostics.CompressionTestBar(a));
        Assert.Equal($"{a.Definition.Banks[0].Id} {byBank[0]:F1} bar, {a.Definition.Banks[1].Id} {byBank[1]:F1} bar",
            EngineDiagnostics.DescribeCompressionTest(a));
    }

    [Theory]
    [InlineData("kestrel_k20")]
    [InlineData("isar_m54")]
    public void ASingleBankEnginesGeometryIsItsOnlyBanksGeometry(string family)
    {
        var db = TestContent.Database;
        var a = EngineAssembly.CreateStock(db.GetEngine(family), db, new PartInstanceFactory());
        var c = EngineConfiguration.Build(a, db.GetFuel("gasoline_98")).GetOrThrow();
        Assert.Equal(c.Geometry, Assert.Single(c.Banks).Geometry);
        Assert.Equal(EngineDiagnostics.CompressionTestBar(a), Assert.Single(EngineDiagnostics.CompressionTestByBank(a)));
        Assert.Equal($"{EngineDiagnostics.CompressionTestBar(a):F1} bar", EngineDiagnostics.DescribeCompressionTest(a));
        Assert.StartsWith($"Static compression ratio {c.Geometry.CompressionRatio:F2}:1",
            Assert.Single(AssemblyValidator.Validate(a).Issues, i => i.Code == "compression_ratio").Message);
    }

    // ---- Air paths ---------------------------------------------------------------------------------------------------

    [Fact]
    public void IntakePathCountIsData()
    {
        // The V8's common plenum split into one plenum per bank (half the flow each): the same engine while the banks
        // are alike; restrict one plenum and only its bank loses air.
        Docs Split(double rightFlowFactor)
        {
            var docs = new Docs();
            var slots = docs.Slots("syn_v8_ohv");
            var plenum = slots.Single(s => (string)s!["id"]! == "intake_manifold")!.AsObject();
            slots.Remove(plenum);
            var stock = docs.Item("engines", "syn_v8_ohv")["stock_parts"]!.AsObject();
            stock.Remove("intake_manifold");
            foreach (var (bank, factor) in new[] { ("left", 1.0), ("right", rightFlowFactor) })
            {
                var copy = plenum.DeepClone().AsObject();
                copy["id"] = $"intake_manifold_{bank}";
                copy["banks"] = new JsonArray(bank);
                copy["install_after"] = new JsonArray($"cylinder_head_{bank}");
                slots.Add(copy);
                docs.Clone("syn.v8.intake", $"variant.intake.{bank}", s => s["flow_cfm"] = (double)s["flow_cfm"]! / 2 * factor);
                stock[$"intake_manifold_{bank}"] = $"variant.intake.{bank}";
            }
            foreach (var s in slots.Select(x => x!.AsObject()).Where(s => s["install_after"] is JsonArray a && a.Any(x => (string)x! == "intake_manifold")))
                s["install_after"] = new JsonArray("intake_manifold_left", "intake_manifold_right");
            return docs;
        }
        var common = SimFactory.At(Sim(TestContent.Matrix, "syn_v8_ohv", "gasoline_95"), 4500, 1.0, 3.0);
        var db = Split(1.0).Load();
        Assert.Equal(2, EngineCapabilities.Resolve(EngineAssembly.CreateStock(db.GetEngine("syn_v8_ohv"), db, new PartInstanceFactory())).IntakePaths);
        var split = SimFactory.At(Sim(db, "syn_v8_ohv", "gasoline_95"), 4500, 1.0, 3.0);
        Near(common.Torque, split.Torque, 1e-6, "torque, one plenum vs two alike");
        var restricted = SimFactory.At(Sim(Split(0.3).Load(), "syn_v8_ohv", "gasoline_95"), 4500, 1.0, 3.0);
        Assert.True(restricted.Banks[1].ManifoldPressure < restricted.Banks[0].ManifoldPressure - 3000);
        Assert.True(restricted.Banks[1].AirPerCycle < 0.95 * restricted.Banks[0].AirPerCycle);
    }

    // ---- Valves and valvetrain ------------------------------------------------------------------------------------------

    [Fact]
    public void ValveCountIsData()
    {
        // The same flow bench curves on a head with four valves per cylinder instead of two: more valvetrain friction.
        var docs = new Docs();
        docs.Spec("syn.s16.head")["valves_per_cylinder"] = 4;
        var two = SimFactory.At(Sim(TestContent.Matrix, "syn_i4_sohc", "gasoline_95"), 4000);
        var four = SimFactory.At(Sim(docs.Load(), "syn_i4_sohc", "gasoline_95"), 4000);
        Assert.True(four.Fmep > two.Fmep);
        Assert.Equal(two.AirPerCycle, four.AirPerCycle); // breathing comes from the flow data, not the valve count
        Assert.Contains("4 valves/cyl", EngineCapabilities.Resolve(EngineAssembly.CreateStock(docs.Load().GetEngine("syn_i4_sohc"), docs.Load(), new PartInstanceFactory())).Describe());
    }

    [Fact]
    public void ValvetrainTypesMustMatchBankByBank()
    {
        // A SOHC head on the DOHC V8's right bank: refused, and the report names the bank.
        var docs = new Docs();
        docs.Clone("syn.v8d.head.right", "variant.sohc_head", s => s["valvetrain"] = "sohc");
        docs.Item("engines", "syn_v8_dohc_vvt")["stock_parts"]!["cylinder_head_right"] = "variant.sohc_head";
        var db = docs.Load();
        var report = AssemblyValidator.Validate(EngineAssembly.CreateStock(db.GetEngine("syn_v8_dohc_vvt"), db, new PartInstanceFactory()));
        var issue = Assert.Single(report.Errors, i => i.Code == "valvetrain_mismatch");
        Assert.StartsWith("Bank 'right':", issue.Message);
        Assert.Contains("SOHC", issue.Message);
        Assert.Contains("cylinder_head_right", issue.Slots);
    }

    // ---- Interfaces -----------------------------------------------------------------------------------------------------

    [Fact]
    public void InterfacesAreResolvedBankByBank()
    {
        // A right-bank head without the exhaust flange: the right exhaust manifold cannot borrow the left head's.
        var docs = new Docs();
        docs.Clone("syn.v6.head", "variant.no_flange_head", _ => { });
        var head = docs.Files.Last().Node["parts"]![0]!.AsObject();
        head["provides"] = new JsonArray("syn.v6.cam_carrier", "syn.v6.intake_flange");
        docs.Item("engines", "syn_v6_na")["stock_parts"]!["cylinder_head_right"] = "variant.no_flange_head";
        var db = docs.Load();
        var report = AssemblyValidator.Validate(EngineAssembly.CreateStock(db.GetEngine("syn_v6_na"), db, new PartInstanceFactory()));
        var issue = Assert.Single(report.Errors, i => i.Code == "missing_interface");
        Assert.Contains("syn.v6.exhaust_flange", issue.Message);
        Assert.Contains("bank 'right'", issue.Message);
        Assert.Contains("only another bank has one", issue.Message);
    }

    [Fact]
    public void AMissingBankPartIsNamedByItsBank()
    {
        var db = TestContent.Matrix;
        var a = EngineAssembly.CreateStock(db.GetEngine("syn_v8_ohv"), db, new PartInstanceFactory());
        // Strip the right bank down to its gasket, then take the gasket too.
        foreach (var s in a.RemovalSequenceFor("head_gasket_right")) Assert.True(a.Remove(s, out _).Ok);
        Assert.True(a.Remove("head_gasket_right", out _).Ok);
        var report = AssemblyValidator.Validate(a);
        Assert.Contains(report.Errors, i => i.Code == "missing_part" && i.Slots.Contains("head_gasket_right"));
        Assert.Contains(report.Errors, i => i.Code == "missing_part" && i.Slots.Contains("cylinder_head_right"));
        Assert.DoesNotContain(report.Errors, i => i.Slots.Any(s => s.EndsWith("_left", StringComparison.Ordinal)));
    }

    // ---- Turbochargers ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TurboCountIsData()
    {
        var db = TestContent.Matrix;
        // One turbo serving both banks of the flat four: each bank takes half its flow.
        var flat = Sim(db, "syn_h4_turbo");
        Assert.Single(flat.Config.Turbos);
        Assert.All(flat.Config.Banks, b => Assert.Equal(0.5, b.TurboShare));
        // A turbo per bank on the V6: two shafts, each with its own state. Fail the left one: its shaft stops, the right keeps spinning.
        var twin = Sim(db, "syn_v6_tt");
        Assert.Equal(2, twin.Config.Turbos.Count);
        var t = SimFactory.At(twin, 5000, 1.0, 3.0);
        Assert.Equal(2, t.Turbos.Count);
        Assert.True(t.Turbos.All(x => x.ShaftRpm > 50_000));
        twin.Config.Turbos[0].Part.Damage.Fail(FailureMode.TurboOverspeed, t.Time);
        var failed = SimFactory.At(twin, 5000, 1.0, 1.0);
        Assert.Equal(0.0, failed.Turbos[0].ShaftRpm);
        Assert.True(failed.Turbos[1].ShaftRpm > 50_000);
        Assert.True(failed.Banks[0].AirPerCycle < 0.8 * failed.Banks[1].AirPerCycle);
    }

    // ---- Variable valvetrain and intake -------------------------------------------------------------------------------------

    [Fact]
    public void VariableValveLiftSwitchesAtTheTunedSpeedAndOnlyWithAnEcuThatCan()
    {
        const string engine = "syn_i3_turbo_vvl";
        var db = TestContent.Matrix;
        var tune = EcuTune.FromDocument(db.GetTune(db.GetEngine(engine).StockTune));
        double on = tune.ValveLiftSwitchRpm!.Value;
        var never = tune.Clone();
        never.ValveLiftSwitchRpm = null;
        var below = SimFactory.At(Sim(db, engine, tune: tune), on - 800);
        Assert.False(below.HighValveLift);
        Assert.Equal(SimFactory.At(Sim(db, engine, tune: never), on - 800).AirPerCycle, below.AirPerCycle);
        var above = SimFactory.At(Sim(db, engine, tune: tune), on + 1200, 1.0, 2.0);
        Assert.True(above.HighValveLift);
        Assert.NotEqual(SimFactory.At(Sim(db, engine, tune: never), on + 1200, 1.0, 2.0).AirPerCycle, above.AirPerCycle);
        // Hysteresis: once on the high profile, it stays there just below the switch speed.
        var sim = Sim(db, engine, tune: tune);
        SimFactory.At(sim, on + 200);
        Assert.True(SimFactory.At(sim, on - 0.5 * EcuController.SwitchHysteresisRpm).HighValveLift);
        Assert.False(SimFactory.At(sim, on - 2 * EcuController.SwitchHysteresisRpm).HighValveLift);
        // An ECU without valve-lift control leaves the camshafts on their base profile, and the validator says so.
        var basic = Sim(db, engine, tune: tune, edit: a => Swap(a, "ecu", db.GetPart("syn.ecu.basic")));
        Assert.False(SimFactory.At(basic, on + 1200).HighValveLift);
        Assert.Contains(AssemblyValidator.Validate(basic.Config.Assembly).Warnings, i => i.Code == "valve_lift_uncontrolled");
    }

    [Fact]
    public void AVariableIntakeRunnerChangesFillingOnlyOnceSwitched()
    {
        const string engine = "syn_i6_vis";
        var db = TestContent.Matrix;
        var tune = EcuTune.FromDocument(db.GetTune(db.GetEngine(engine).StockTune));
        var primaryOnly = tune.Clone();
        primaryOnly.IntakeRunnerSwitchRpm = null;
        double sw = tune.IntakeRunnerSwitchRpm!.Value;
        // Below the switch both run the long runner.
        Assert.Equal(SimFactory.At(Sim(db, engine, "gasoline_95", primaryOnly), sw - 1000).AirPerCycle,
            SimFactory.At(Sim(db, engine, "gasoline_95", tune), sw - 1000).AirPerCycle);
        // Above it the short runner moves the filling peak up (fixed cams: the runner term acts in full).
        var shortRunner = SimFactory.At(Sim(db, engine, "gasoline_95", tune), 6500);
        var longRunner = SimFactory.At(Sim(db, engine, "gasoline_95", primaryOnly), 6500);
        Assert.True(shortRunner.SwitchedRunner && !longRunner.SwitchedRunner);
        Assert.True(shortRunner.AirPerCycle > longRunner.AirPerCycle);
    }

    // ---- Identity -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AMultiBankFamilyUnderOtherIdsIsBitIdentical()
    {
        // The twin-turbo V6 with every id of its own renamed: nothing may depend on what it is called.
        var docs = new Docs();
        var file = docs.Files.Single(f => f.Source.EndsWith("syn_v6_tt.json", StringComparison.Ordinal));
        string json = file.Node.ToJsonString().Replace("syn_v6_tt", "zz_engine", StringComparison.Ordinal).Replace("syn.v6tt.", "zz.", StringComparison.Ordinal);
        docs.Files.Add(("renamed.json", JsonNode.Parse(json)!));
        var db = docs.Load();
        var original = SteadyStateSweep(Sim(db, "syn_v6_tt"));
        var renamed = SteadyStateSweep(Sim(db, "zz_engine"));
        Assert.Equal(original, renamed);
    }

    private static IReadOnlyList<EngineTelemetry> SteadyStateSweep(EngineSimulation sim) =>
        CarSim.Core.Dyno.SteadyStateSweep.Run(sim, 1500, 6500, 1000, settleSeconds: 0.5);
}
