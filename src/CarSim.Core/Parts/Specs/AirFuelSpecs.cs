using System.Text.Json.Serialization;
using CarSim.Core.Common;

namespace CarSim.Core.Parts.Specs;

public sealed class IntakeManifoldSpec : PartSpec
{
    /// <summary>Runner length; longer runners tune the torque peak to lower RPM.</summary>
    public required double RunnerLengthMm { get; init; }

    /// <summary>Total flow capacity (air filter/inlet included), CFM at 28 inH2O.</summary>
    public required double FlowCfm { get; init; }

    [JsonIgnore] public double RunnerLength => Units.MmToM(RunnerLengthMm);

    protected override void Validate(SpecChecker check)
    {
        check.Range("runner_length_mm", RunnerLengthMm, 50, 1000);
        check.Positive("flow_cfm", FlowCfm);
    }
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

    [JsonIgnore] public double RatedPressure => Units.KpaToPa(RatedPressureKpa);

    /// <summary>Volumetric flow of one injector at the rated pressure, m³/s.</summary>
    [JsonIgnore] public double RatedFlow => Units.CcPerMinToM3PerSec(FlowCcMin);

    protected override void Validate(SpecChecker check)
    {
        check.PositiveInt("count", Count);
        check.Range("flow_cc_min", FlowCcMin, 50, 5000);
        check.Range("rated_pressure_kpa", RatedPressureKpa, 100, 1000);
        check.Range("max_duty", MaxDuty, 0.3, 1.0);
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
