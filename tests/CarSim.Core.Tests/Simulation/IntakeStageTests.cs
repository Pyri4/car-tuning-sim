using System.Text.Json;
using System.Text.Json.Nodes;
using CarSim.Core.Content;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;
using CarSim.Core.Tests.Acceptance;

namespace CarSim.Core.Tests.Simulation;

/// <summary>
/// The intake's runner-stage data (Intake Gas Dynamics 2.0, Phase 1 step 1): an optional runner diameter, any number of
/// switched stages (<c>switched_stages</c>; <c>switched_runner_length_mm</c> is the two-stage shorthand), and the ECU's
/// stage-by-stage switching with hysteresis. Test-only geometry is a generic probe, not data for the engine it is fitted to.
/// </summary>
public class IntakeStageTests
{
    private static JsonArray Stages(params double[] lengths) => new(lengths.Select(l => (JsonNode)new JsonObject { ["runner_length_mm"] = l }).ToArray());

    [Fact]
    public void StagesListThePrimaryFirstAndInheritTheManifoldsDiameter()
    {
        var db = IntakeRig.Content(
            ("syn.r6.intake", "test.intake.three_stage", s =>
            {
                s.Remove("switched_runner_length_mm");
                s["runner_diameter_mm"] = 36.0;
                s["switched_stages"] = new JsonArray(new JsonObject { ["runner_length_mm"] = 330 }, new JsonObject { ["runner_length_mm"] = 220, ["runner_diameter_mm"] = 42.0 });
            }),
            ("syn.r6.intake", "test.intake.two_stage_default", s => { }));
        var three = (IntakeManifoldSpec)db.GetPart("test.intake.three_stage").Spec;
        Assert.Empty(three.Validate());
        Assert.True(three.HasSwitchedStages);
        var stages = three.AllStages();
        Assert.Equal(new[] { 450.0, 330, 220 }, stages.Select(s => s.RunnerLengthMm));
        Assert.Equal(new double?[] { 36, 36, 42 }, stages.Select(s => s.RunnerDiameterMm));

        // The two-stage shorthand is the N = 2 case; without a diameter the stages carry none (the model default applies).
        var two = (IntakeManifoldSpec)db.GetPart("test.intake.two_stage_default").Spec;
        Assert.Equal(new[] { 450.0, 250 }, two.AllStages().Select(s => s.RunnerLengthMm));
        Assert.All(two.AllStages(), s => Assert.Null(s.RunnerDiameterMm));
        var fixedIntake = (IntakeManifoldSpec)db.GetPart("k20.intake.oem").Spec;
        Assert.False(fixedIntake.HasSwitchedStages);
        Assert.Single(fixedIntake.AllStages());
    }

    [Theory]
    [InlineData("both", "cannot both be given")]
    [InlineData("empty", "needs at least one stage")]
    [InlineData("stage_length", "switched_stages[0].runner_length_mm")]
    [InlineData("stage_diameter", "switched_stages[0].runner_diameter_mm")]
    [InlineData("diameter", "runner_diameter_mm")]
    public void TheValidatorRefusesInconsistentStageData(string edit, string problem)
    {
        var s = new JsonObject { ["runner_length_mm"] = 450, ["flow_cfm"] = 620, ["switched_runner_length_mm"] = 250 };
        switch (edit)
        {
            case "both": s["switched_stages"] = Stages(300); break;
            case "empty": s.Remove("switched_runner_length_mm"); s["switched_stages"] = new JsonArray(); break;
            case "stage_length": s.Remove("switched_runner_length_mm"); s["switched_stages"] = Stages(20); break;
            case "stage_diameter":
                s.Remove("switched_runner_length_mm");
                s["switched_stages"] = new JsonArray(new JsonObject { ["runner_length_mm"] = 300, ["runner_diameter_mm"] = 200.0 });
                break;
            case "diameter": s["runner_diameter_mm"] = 5.0; break;
        }
        var spec = JsonSerializer.Deserialize<IntakeManifoldSpec>(s.ToJsonString(), ContentJson.Options)!;
        Assert.Contains(spec.Validate(), p => p.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void TheEcuStepsThroughEveryStageWithHysteresisAtEachSwitch()
    {
        var db = TestContent.Matrix;
        var tune = EcuTune.FromDocument(db.GetTune(db.GetEngine("syn_i6_vis").StockTune));
        tune.IntakeRunnerSwitchRpm = 3000;
        tune.IntakeRunnerUpperSwitchRpm = new[] { 5000.0 };
        var ecu = new EcuController((EcuSpec)db.GetPart("syn.ecu.full").Spec, tune);
        const double h = EcuController.SwitchHysteresisRpm;
        Assert.Equal(0, ecu.IntakeRunnerStage(2900, 0, 3, true));
        Assert.Equal(1, ecu.IntakeRunnerStage(3000, 0, 3, true));
        Assert.Equal(1, ecu.IntakeRunnerStage(3000 - 0.5 * h, 1, 3, true));
        Assert.Equal(0, ecu.IntakeRunnerStage(3000 - 2 * h, 1, 3, true));
        Assert.Equal(2, ecu.IntakeRunnerStage(5000, 1, 3, true));
        Assert.Equal(2, ecu.IntakeRunnerStage(6500, 0, 3, true));        // from rest straight to the stage the speed calls for
        Assert.Equal(2, ecu.IntakeRunnerStage(5000 - 0.5 * h, 2, 3, true));
        Assert.Equal(1, ecu.IntakeRunnerStage(5000 - 2 * h, 2, 3, true));
        Assert.Equal(1, ecu.IntakeRunnerStage(6500, 1, 2, true));        // a two-stage intake never goes beyond stage 1
        Assert.Equal(0, ecu.IntakeRunnerStage(6500, 2, 3, false));       // not running: the primary runner
        tune.IntakeRunnerUpperSwitchRpm = null;
        Assert.Equal(1, ecu.IntakeRunnerStage(6500, 0, 3, true));        // no switch speed for stage 2: never reached
        tune.IntakeRunnerSwitchRpm = null;
        Assert.Equal(0, ecu.IntakeRunnerStage(6500, 0, 3, true));
        var basic = new EcuController((EcuSpec)db.GetPart("syn.ecu.basic").Spec, tune);
        Assert.Equal(0, basic.IntakeRunnerStage(6500, 0, 3, true));      // an ECU without runner control cannot switch
    }

    [Fact]
    public void AThreeStageIntakeRunsEachStageInItsSpeedBand()
    {
        var db = IntakeRig.Content(("syn.r6.intake", "test.intake.three_stage", s =>
        {
            s.Remove("switched_runner_length_mm");
            s["switched_stages"] = Stages(330, 220);
        }));
        var doc = db.GetTune(db.GetEngine("syn_i6_vis").StockTune) with { IntakeRunnerSwitchRpm = 3000, IntakeRunnerUpperSwitchRpm = new[] { 5000.0 } };
        Assert.Empty(doc.Validate());
        var engine = IntakeRig.Build(db, "syn_i6_vis", ("intake_manifold", "test.intake.three_stage"));
        var caps = EngineCapabilities.Resolve(engine);
        Assert.True(caps.SwitchedRunners && caps.VariableIntakeRunner);
        var points = IntakeRig.Sweep(db, engine, "gasoline_95", doc, 2000, 6000, 1000);
        Assert.Equal(new[] { 0, 1, 1, 2, 2 }, points.Select(p => p.RunnerStage));
        Assert.All(points, p => Assert.Equal(p.RunnerStage > 0, p.SwitchedRunner));
        Assert.All(points, p => Assert.Equal(p.RunnerStage, p.Banks[0].RunnerStage));
    }

    [Theory]
    [InlineData(new double[] { 5000 }, true)]
    [InlineData(new double[] { }, true)]
    [InlineData(new double[] { 2500 }, false)]      // below the switch to stage 1
    [InlineData(new double[] { 5000, 4800 }, false)] // not rising
    [InlineData(new double[] { 30000 }, false)]     // out of range
    public void UpperSwitchSpeedsRiseStageByStage(double[] upper, bool valid)
    {
        var db = TestContent.Matrix;
        var doc = db.GetTune(db.GetEngine("syn_i6_vis").StockTune) with { IntakeRunnerSwitchRpm = 3000, IntakeRunnerUpperSwitchRpm = upper };
        Assert.Equal(valid, !doc.Validate().Any(p => p.Contains("intake_runner_upper_switch_rpm", StringComparison.Ordinal)));
        Assert.Contains((doc with { IntakeRunnerSwitchRpm = null, IntakeRunnerUpperSwitchRpm = new[] { 5000.0 } }).Validate(),
            p => p.Contains("needs intake_runner_switch_rpm", StringComparison.Ordinal));
    }
}
