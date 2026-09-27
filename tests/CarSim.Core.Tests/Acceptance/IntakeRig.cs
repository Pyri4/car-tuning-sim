using System.Text.Json;
using System.Text.Json.Nodes;
using CarSim.Core.Content;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;

namespace CarSim.Core.Tests.Acceptance;

/// <summary>
/// An acceptance test of Intake Gas Dynamics 2.0 (docs/milestones/INTAKE_GAS_DYNAMICS_2.md, "Acceptance tests"). Defined
/// in Phase 0, before the model exists: each one fails on today's single-hump VE model (recorded in the milestone
/// document), so it is skipped unless <c>CARSIM_RUN_PENDING_ACCEPTANCE=1</c>. Phase 1 turns them into plain facts.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PendingAcceptanceFactAttribute : FactAttribute
{
    public const string Variable = "CARSIM_RUN_PENDING_ACCEPTANCE";

    public PendingAcceptanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"Intake Gas Dynamics 2.0 acceptance (Phase 1): fails on the current single-hump VE model by design. Run with {Variable}=1.";
    }
}

/// <summary>
/// Test rig for intake behaviour at system level, with only today's public API: full-load sweeps with the cam phase and
/// the runner stage held, test-only intake and ECU variants made as JSON clones (never written to <c>content/</c>), and
/// the speed at which one configuration starts to out-fill another. Comparing two runners on the same cams at the same
/// cam phase cancels the valve-event part of filling, so what is left is the runner's own response.
/// </summary>
internal static class IntakeRig
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The base game (and the synthetic matrix) with clones of parts, specs edited: (source part, new id, edit).</summary>
    public static ContentDatabase Content(params (string Part, string NewId, Action<JsonObject> Edit)[] clones)
    {
        var files = new[] { TestContent.BaseContentPath, Path.Combine(TestContent.MatrixLayersPath, "engine-matrix") }
            .SelectMany(root => Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            .Select(f => (Source: f, Node: JsonNode.Parse(File.ReadAllText(f), documentOptions: Options)!)).ToList();
        var parts = new JsonArray();
        foreach (var (part, newId, edit) in clones)
        {
            var source = files.SelectMany(d => d.Node["parts"]?.AsArray() ?? new JsonArray()).Single(n => (string?)n!["id"] == part)!;
            var copy = source.DeepClone().AsObject();
            copy["id"] = newId;
            edit(copy["spec"]!.AsObject());
            parts.Add(copy);
        }
        files.Add(("acceptance-variants.json", new JsonObject { ["parts"] = parts }));
        return ContentLoader.LoadFromStrings(files.Select(d => (d.Source, d.Node.ToJsonString()))).GetOrThrow();
    }

    /// <summary>Stock engine with parts replaced by id (whatever is in the way comes out and goes back).</summary>
    public static EngineAssembly Build(ContentDatabase db, string engine, params (string Slot, string Part)[] swaps)
    {
        var a = EngineAssembly.CreateStock(db.GetEngine(engine), db, new PartInstanceFactory());
        var factory = new PartInstanceFactory(88_000_000);
        foreach (var (slot, part) in swaps)
        {
            var removed = new Stack<(string, PartInstance)>();
            foreach (var s in a.RemovalSequenceFor(slot))
            {
                Assert.True(a.Remove(s, out var p).Ok);
                removed.Push((s, p!));
            }
            if (a.IsInstalled(slot)) Assert.True(a.Remove(slot, out _).Ok);
            var r = a.Install(slot, factory.Create(db.GetPart(part)));
            Assert.True(r.Ok, r.Message);
            while (removed.Count > 0)
            {
                var (s, p) = removed.Pop();
                Assert.True(a.Install(s, p).Ok);
            }
        }
        return a;
    }

    /// <summary>The engine's stock tune with the intake cam held at <paramref name="advanceDeg"/> everywhere.</summary>
    public static TuneDocument WithCamAt(TuneDocument tune, double advanceDeg) =>
        tune with { IntakeCamAdvanceDeg = tune.LoadAxisKpa.Select(_ => tune.RpmAxis.Select(_ => advanceDeg).ToArray()).ToArray() };

    /// <summary>Full-load steady-state points (1 s settle, test-cell coolant) at an ambient temperature, damage off.</summary>
    public static IReadOnlyList<EngineTelemetry> Sweep(ContentDatabase db, EngineAssembly engine, string fuel, TuneDocument tune,
        double from, double to, double step, double ambientK = 298.15)
    {
        var ecu = EcuTune.FromDocument(tune);
        var config = EngineConfiguration.Build(engine, db.GetFuel(fuel), new ValidationContext(ecu.RevLimitRpm, ecu.MaxBoostTargetKpa)).GetOrThrow();
        var sim = new EngineSimulation(config, ecu, EngineState.Warm()) { DamageEnabled = false };
        var points = new List<EngineTelemetry>();
        for (double rpm = from; rpm <= to + 1e-6; rpm += step)
        {
            var input = new EngineInputs
            {
                Throttle = 1.0, SpeedMode = SpeedMode.Held, HeldRpm = rpm, CoolantTemperatureOverride = 363.15, AmbientTemperature = ambientK,
            };
            EngineTelemetry t = sim.Step(0.005, input);
            for (int i = 1; i < 200; i++) t = sim.Step(0.005, input);
            points.Add(t);
        }
        return points;
    }

    /// <summary>
    /// The speed above which <paramref name="upper"/> beats <paramref name="lower"/> on <paramref name="channel"/> for good
    /// (the last sign change of upper − lower from negative to positive, interpolated), or null if it never does.
    /// </summary>
    public static double? Crossover(IReadOnlyList<EngineTelemetry> upper, IReadOnlyList<EngineTelemetry> lower, Func<EngineTelemetry, double> channel)
    {
        double? crossing = null;
        for (int i = 1; i < upper.Count; i++)
        {
            double d0 = channel(upper[i - 1]) - channel(lower[i - 1]), d1 = channel(upper[i]) - channel(lower[i]);
            if (d0 < 0 && d1 >= 0) crossing = upper[i - 1].Rpm + (upper[i].Rpm - upper[i - 1].Rpm) * d0 / (d0 - d1);
            if (d0 >= 0 && d1 < 0) crossing = null;
        }
        return crossing;
    }

    /// <summary>Mean of a channel over the points within ±<paramref name="band"/> rpm of <paramref name="rpm"/>.</summary>
    public static double Near(IReadOnlyList<EngineTelemetry> points, double rpm, Func<EngineTelemetry, double> channel, double band = 300) =>
        points.Where(p => Math.Abs(p.Rpm - rpm) <= band).Average(channel);
}
