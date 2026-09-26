using System.Text.Json.Serialization;
using CarSim.Core.Common;

namespace CarSim.Core.Parts.Specs;

/// <summary>
/// Turbocharger. The compressor is described by the few numbers a compressor map conveys (wheel size,
/// choke flow, efficiency island, surge line); the turbine by its effective nozzle area (the A/R
/// trade-off between spool and backpressure) and wheel size.
/// </summary>
public sealed class TurbochargerSpec : PartSpec
{
    /// <summary>Compressor exducer (outer) diameter; sets tip speed and therefore pressure ratio.</summary>
    public required double CompressorWheelDiameterMm { get; init; }

    /// <summary>Corrected mass flow at which the compressor chokes.</summary>
    public required double CompressorChokeFlowKgS { get; init; }

    public required double CompressorPeakEfficiency { get; init; }

    /// <summary>Centre of the efficiency island (corrected flow).</summary>
    public required double CompressorPeakEfficiencyFlowKgS { get; init; }

    /// <summary>Centre of the efficiency island (pressure ratio).</summary>
    public required double CompressorPeakEfficiencyPressureRatio { get; init; }

    /// <summary>Corrected flow on the surge line at pressure ratio 2.0 (surge line passes through PR 1 at zero flow).</summary>
    public required double CompressorSurgeFlowAtPr2KgS { get; init; }

    /// <summary>Turbine effective flow area. Small = quick spool, high backpressure; large = lag, free-flowing.</summary>
    public required double TurbineFlowAreaCm2 { get; init; }

    public required double TurbineWheelDiameterMm { get; init; }
    public required double TurbinePeakEfficiency { get; init; }

    /// <summary>Rotating assembly inertia (both wheels + shaft).</summary>
    public required double RotorInertiaKgCm2 { get; init; }

    public required double MaxShaftRpm { get; init; }

    /// <summary>Boost (gauge, at the compressor outlet) at which the wastegate actuator starts to open.</summary>
    public required double WastegateSpringKpa { get; init; }

    /// <summary>Effective flow area of the fully open wastegate.</summary>
    public required double WastegateFlowAreaCm2 { get; init; }

    public double MaxTurbineInletTemperatureC { get; init; } = 950.0;

    /// <summary>Ball-bearing cartridges have less drag than journal bearings (quicker spool).</summary>
    public bool BallBearing { get; init; }

    [JsonIgnore] public double CompressorTipRadius => Units.MmToM(CompressorWheelDiameterMm) / 2.0;
    [JsonIgnore] public double TurbineTipRadius => Units.MmToM(TurbineWheelDiameterMm) / 2.0;
    [JsonIgnore] public double TurbineFlowArea => TurbineFlowAreaCm2 * 1e-4;
    [JsonIgnore] public double WastegateFlowArea => WastegateFlowAreaCm2 * 1e-4;
    [JsonIgnore] public double RotorInertia => RotorInertiaKgCm2 * 1e-4;
    [JsonIgnore] public double MaxShaftSpeed => Units.RpmToRadPerSec(MaxShaftRpm);
    [JsonIgnore] public double WastegateSpring => Units.KpaToPa(WastegateSpringKpa);
    [JsonIgnore] public double MaxTurbineInletTemperature => Units.CToK(MaxTurbineInletTemperatureC);

    protected override void Validate(SpecChecker check)
    {
        check.Range("compressor_wheel_diameter_mm", CompressorWheelDiameterMm, 20, 200);
        check.Range("compressor_choke_flow_kg_s", CompressorChokeFlowKgS, 0.02, 3.0);
        check.Range("compressor_peak_efficiency", CompressorPeakEfficiency, 0.4, 0.9);
        check.Positive("compressor_peak_efficiency_flow_kg_s", CompressorPeakEfficiencyFlowKgS);
        check.That(CompressorPeakEfficiencyFlowKgS < CompressorChokeFlowKgS, "compressor_peak_efficiency_flow_kg_s must be below the choke flow.");
        check.Range("compressor_peak_efficiency_pressure_ratio", CompressorPeakEfficiencyPressureRatio, 1.1, 5.0);
        check.Positive("compressor_surge_flow_at_pr2_kg_s", CompressorSurgeFlowAtPr2KgS);
        check.That(CompressorSurgeFlowAtPr2KgS < CompressorPeakEfficiencyFlowKgS, "surge flow must be below the efficiency island.");
        check.Range("turbine_flow_area_cm2", TurbineFlowAreaCm2, 1, 60);
        check.Range("turbine_wheel_diameter_mm", TurbineWheelDiameterMm, 20, 200);
        check.Range("turbine_peak_efficiency", TurbinePeakEfficiency, 0.4, 0.9);
        check.Range("rotor_inertia_kg_cm2", RotorInertiaKgCm2, 0.02, 20);
        check.Range("max_shaft_rpm", MaxShaftRpm, 20_000, 400_000);
        check.Range("wastegate_spring_kpa", WastegateSpringKpa, 10, 300);
        check.Range("wastegate_flow_area_cm2", WastegateFlowAreaCm2, 0.5, 40);
        check.Range("max_turbine_inlet_temperature_c", MaxTurbineInletTemperatureC, 600, 1200);
    }
}

public sealed class IntercoolerSpec : PartSpec
{
    /// <summary>Fraction of the charge-to-ambient temperature difference removed at the reference flow.</summary>
    public required double Effectiveness { get; init; }

    public required double ReferenceFlowKgS { get; init; }

    /// <summary>Core + piping flow capacity (pressure drop), CFM at 28 inH2O.</summary>
    public required double FlowCfm { get; init; }

    protected override void Validate(SpecChecker check)
    {
        check.Range("effectiveness", Effectiveness, 0.1, 0.98);
        check.Positive("reference_flow_kg_s", ReferenceFlowKgS);
        check.Positive("flow_cfm", FlowCfm);
    }
}
