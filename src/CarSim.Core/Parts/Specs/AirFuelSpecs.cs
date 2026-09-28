using System.Text.Json.Serialization;
using CarSim.Core.Common;

namespace CarSim.Core.Parts.Specs;

public sealed class IntakeManifoldSpec : PartSpec
{
    /// <summary>Validator range of every runner stage's acoustic length, mm.</summary>
    public const double MinRunnerLengthMm = 50, MaxRunnerLengthMm = 1000;

    /// <summary>Validator range of a runner diameter, mm.</summary>
    public const double MinRunnerDiameterMm = 15, MaxRunnerDiameterMm = 120;

    /// <summary>
    /// Acoustic length of the primary runner stage, mm: from the runner's mouth in the plenum (or the airbox) to the
    /// intake valve seat, <b>including the cylinder head's intake port</b>. Longer runners tune to lower speed.
    /// </summary>
    public required double RunnerLengthMm { get; init; }

    /// <summary>
    /// Mean inner diameter of the runners (a round runner of the same mean cross-section), mm, for every stage that
    /// does not state its own. Optional: without it the model assumes 0.40 × bore (assumption A5) and the validator
    /// says so (<c>default_intake_geometry</c>).
    /// </summary>
    public double? RunnerDiameterMm { get; init; }

    /// <summary>
    /// Two-stage variable intake (shorthand for one switched stage): the acoustic length once the ECU switches the
    /// manifold's valve or flap (above the tune's runner switch speed), usually shorter for top-end breathing. Null = a
    /// fixed manifold (or <see cref="SwitchedStages"/>). Needs an ECU with intake-runner control.
    /// </summary>
    public double? SwitchedRunnerLengthMm { get; init; }

    /// <summary>
    /// Variable intake with any number of stages: the stages the ECU switches to, in the order it selects them as engine
    /// speed rises (stage 1, 2, …; the primary runner is stage 0). Mutually exclusive with
    /// <see cref="SwitchedRunnerLengthMm"/>. Needs an ECU with intake-runner control and a switch speed per stage in the
    /// tune.
    /// </summary>
    public IReadOnlyList<IntakeStageSpec>? SwitchedStages { get; init; }

    /// <summary>Total flow capacity (air filter/inlet included), CFM at 28 inH2O.</summary>
    public required double FlowCfm { get; init; }

    [JsonIgnore] public double RunnerLength => Units.MmToM(RunnerLengthMm);

    /// <summary>Whether the manifold has a stage beyond its primary runner (a variable intake).</summary>
    [JsonIgnore] public bool HasSwitchedStages => SwitchedRunnerLengthMm != null || SwitchedStages is { Count: > 0 };

    /// <summary>
    /// Every runner stage, the primary first, then the switched stages in the ECU's order. A stage without its own
    /// diameter takes the manifold's <see cref="RunnerDiameterMm"/> (null when neither is authored). Built on each call
    /// (read when an engine configuration is built, never per step).
    /// </summary>
    public IReadOnlyList<IntakeStageSpec> AllStages()
    {
        var stages = new List<IntakeStageSpec> { new() { RunnerLengthMm = RunnerLengthMm, RunnerDiameterMm = RunnerDiameterMm } };
        if (SwitchedRunnerLengthMm is double switched) stages.Add(new IntakeStageSpec { RunnerLengthMm = switched, RunnerDiameterMm = RunnerDiameterMm });
        foreach (var s in SwitchedStages ?? Array.Empty<IntakeStageSpec>())
            stages.Add(new IntakeStageSpec { RunnerLengthMm = s.RunnerLengthMm, RunnerDiameterMm = s.RunnerDiameterMm ?? RunnerDiameterMm });
        return stages;
    }

    protected override void Validate(SpecChecker check)
    {
        check.Range("runner_length_mm", RunnerLengthMm, MinRunnerLengthMm, MaxRunnerLengthMm);
        if (RunnerDiameterMm is double d) check.Range("runner_diameter_mm", d, MinRunnerDiameterMm, MaxRunnerDiameterMm);
        if (SwitchedRunnerLengthMm is double switched) check.Range("switched_runner_length_mm", switched, MinRunnerLengthMm, MaxRunnerLengthMm);
        if (SwitchedStages != null)
        {
            check.That(SwitchedRunnerLengthMm == null, "switched_runner_length_mm and switched_stages cannot both be given (a two-stage intake is one switched stage).");
            check.That(SwitchedStages.Count > 0, "switched_stages needs at least one stage (leave it out for a fixed manifold).");
            for (int i = 0; i < SwitchedStages.Count; i++)
            {
                if (SwitchedStages[i] is { } stage) stage.Validate(check, $"switched_stages[{i}].");
                else check.That(false, $"switched_stages[{i}] is empty.");
            }
        }
        check.Positive("flow_cfm", FlowCfm);
    }
}

/// <summary>One runner stage of a variable intake manifold (see <see cref="IntakeManifoldSpec.SwitchedStages"/>).</summary>
public sealed class IntakeStageSpec
{
    /// <summary>Acoustic length of this stage, mm (runner mouth to valve seat, head port included).</summary>
    public required double RunnerLengthMm { get; init; }

    /// <summary>Mean inner diameter of this stage's runners, mm (null: the manifold's, else the model default).</summary>
    public double? RunnerDiameterMm { get; init; }

    internal void Validate(SpecChecker check, string prefix)
    {
        check.Range(prefix + "runner_length_mm", RunnerLengthMm, IntakeManifoldSpec.MinRunnerLengthMm, IntakeManifoldSpec.MaxRunnerLengthMm);
        if (RunnerDiameterMm is double d)
            check.Range(prefix + "runner_diameter_mm", d, IntakeManifoldSpec.MinRunnerDiameterMm, IntakeManifoldSpec.MaxRunnerDiameterMm);
    }

    public override string ToString() => RunnerDiameterMm is double d
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{RunnerLengthMm:0.#} mm × Ø{d:0.#} mm")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{RunnerLengthMm:0.#} mm");
}

public sealed class ThrottleBodySpec : PartSpec
{
    public required double BoreMm { get; init; }

    /// <summary>Wide-open flow capacity, CFM at 28 inH2O.</summary>
    public required double FlowCfm { get; init; }

    protected override void Validate(SpecChecker check)
    {
        check.Range("bore_mm", BoreMm, 20, 150);
        check.Positive("flow_cfm", FlowCfm);
    }
}

public sealed class InjectorSpec : PartSpec
{
    public required int Count { get; init; }

    /// <summary>Static flow per injector at <see cref="RatedPressureKpa"/> differential pressure.</summary>
    public required double FlowCcMin { get; init; }

    public double RatedPressureKpa { get; init; } = 300.0;

    /// <summary>Recommended maximum duty cycle (0–1). Above it the injector cannot reliably meter.</summary>
    public double MaxDuty { get; init; } = 0.85;

    /// <summary>
    /// Opening delay (dead time): how much of each electrical pulse passes before fuel flows, ms. Held constant
    /// (real dead time grows as battery voltage falls and fuel pressure rises; neither is modelled).
    /// </summary>
    public double DeadTimeMs { get; init; }

    [JsonIgnore] public double RatedPressure => Units.KpaToPa(RatedPressureKpa);

    /// <summary>Dead time, s.</summary>
    [JsonIgnore] public double DeadTime => DeadTimeMs / 1000.0;

    /// <summary>Volumetric flow of one injector at the rated pressure, m³/s.</summary>
    [JsonIgnore] public double RatedFlow => Units.CcPerMinToM3PerSec(FlowCcMin);

    protected override void Validate(SpecChecker check)
    {
        check.PositiveInt("count", Count);
        check.Range("flow_cc_min", FlowCcMin, 50, 5000);
        check.Range("rated_pressure_kpa", RatedPressureKpa, 100, 1000);
        check.Range("max_duty", MaxDuty, 0.3, 1.0);
        check.Range("dead_time_ms", DeadTimeMs, 0.0, 3.0);
    }
}

/// <summary>Fuel supply module: pump plus manifold-referenced pressure regulator.</summary>
public sealed class FuelPumpSpec : PartSpec
{
    /// <summary>Flow with no pressure against the pump.</summary>
    public required double FreeFlowLph { get; init; }

    /// <summary>Pressure at which flow falls to zero (dead-head).</summary>
    public required double MaxPressureKpa { get; init; }

    /// <summary>Base fuel pressure above manifold pressure held by the regulator.</summary>
    public required double RegulatedPressureKpa { get; init; }

    [JsonIgnore] public double FreeFlow => Units.LitresPerHourToM3PerSec(FreeFlowLph);
    [JsonIgnore] public double MaxPressure => Units.KpaToPa(MaxPressureKpa);
    [JsonIgnore] public double RegulatedPressure => Units.KpaToPa(RegulatedPressureKpa);

    protected override void Validate(SpecChecker check)
    {
        check.Range("free_flow_lph", FreeFlowLph, 20, 2000);
        check.Range("max_pressure_kpa", MaxPressureKpa, 150, 2000);
        check.Range("regulated_pressure_kpa", RegulatedPressureKpa, 100, 1000);
        check.That(MaxPressureKpa > RegulatedPressureKpa, "max_pressure_kpa must exceed regulated_pressure_kpa.");
    }
}

public sealed class ExhaustManifoldSpec : PartSpec
{
    /// <summary>Flow capacity, CFM at 28 inH2O (cold-flow equivalent).</summary>
    public required double FlowCfm { get; init; }

    /// <summary>Primary runner length; sets the RPM of the scavenging benefit.</summary>
    public required double PrimaryLengthMm { get; init; }

    /// <summary>
    /// Peak volumetric-efficiency gain from exhaust pulse scavenging at the tuned RPM
    /// (≈0 for a log/turbo manifold, a few percent for a tuned 4-2-1 header).
    /// </summary>
    public double ScavengingGain { get; init; }

    [JsonIgnore] public double PrimaryLength => Units.MmToM(PrimaryLengthMm);

    protected override void Validate(SpecChecker check)
    {
        check.Positive("flow_cfm", FlowCfm);
        check.Range("primary_length_mm", PrimaryLengthMm, 50, 2000);
        check.Range("scavenging_gain", ScavengingGain, 0, 0.15);
    }
}

/// <summary>Exhaust system after the manifold/turbo (downpipe, catalyst, silencers).</summary>
public sealed class ExhaustSpec : PartSpec
{
    public required double FlowCfm { get; init; }
    public double DiameterMm { get; init; } = 57.0;
    public bool HasCatalyst { get; init; } = true;

    protected override void Validate(SpecChecker check)
    {
        check.Positive("flow_cfm", FlowCfm);
        check.Positive("diameter_mm", DiameterMm);
    }
}
