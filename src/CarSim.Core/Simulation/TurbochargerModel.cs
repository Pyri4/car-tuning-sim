using CarSim.Core.Common;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Simulation;

/// <summary>Compressor operating point.</summary>
public readonly record struct CompressorPoint(
    double PressureRatio,
    double Efficiency,
    double OutletTemperature,
    double SpecificWork,
    double CorrectedFlow,
    double ChokeRatio,
    bool Surge,
    double Power);

/// <summary>
/// Map-free turbocharger model built from a handful of spec numbers.
///
/// Compressor: actual specific work w = σ·U²·ψ(x), U = tip speed, σ = work coefficient,
/// ψ(x) = 1 − 0.95·x⁸ with x = corrected flow / choke flow (speed lines droop and collapse at choke).
/// Isentropic work = η·w, so PR = (1 + η·w/(c_p·T₁))^(γ/(γ−1)); outlet temperature T₂ = T₁ + w/c_p;
/// absorbed power ṁ·w. Efficiency is an elliptical island around the authored peak point, falling off
/// towards choke and in surge.
///
/// Turbine: a nozzle of the authored effective area (in parallel with the wastegate) sets the exhaust
/// backpressure; power = ṁ_t·η_t·c_p·T₃·(1 − (p₄/p₃)^((γ−1)/γ)) with η_t a function of blade-speed ratio.
/// </summary>
public static class TurbochargerModel
{
    public const double WorkCoefficient = 0.75;
    public const double ReferenceTemperature = 298.15;
    public const double ReferencePressure = PhysicalConstants.StandardPressure;
    public const double JournalBearingDrag = 2.0e-6;
    public const double BallBearingDrag = 1.0e-6;
    public const double OptimumBladeSpeedRatio = 0.7;

    private static readonly double AirExponent = PhysicalConstants.AirGamma / (PhysicalConstants.AirGamma - 1.0);

    public static double CorrectedFlow(double massFlow, double inletPressure, double inletTemperature) =>
        massFlow * Math.Sqrt(inletTemperature / ReferenceTemperature) / (inletPressure / ReferencePressure);

    public static double SurgeFlow(TurbochargerSpec spec, double pressureRatio) =>
        spec.CompressorSurgeFlowAtPr2KgS * Math.Max(0.0, pressureRatio - 1.0);

    public static double Efficiency(TurbochargerSpec spec, double correctedFlow, double pressureRatio)
    {
        double choke = spec.CompressorChokeFlowKgS;
        double dm = (correctedFlow - spec.CompressorPeakEfficiencyFlowKgS) / (0.5 * choke);
        double dp = (pressureRatio - spec.CompressorPeakEfficiencyPressureRatio) / 1.5;
        double e = spec.CompressorPeakEfficiency * (1.0 - 0.25 * dm * dm - 0.15 * dp * dp);
        e *= 1.0 - 0.5 * MathUtil.SmoothStep(0.9, 1.1, correctedFlow / choke);
        return MathUtil.Clamp(e, 0.40, spec.CompressorPeakEfficiency);
    }

    public static double WorkFactor(double chokeRatio) => Math.Max(0.02, 1.0 - 0.95 * Math.Pow(Math.Max(0.0, chokeRatio), 8));

    public static CompressorPoint Compressor(TurbochargerSpec spec, double shaftOmega, double massFlow, double inletPressure, double inletTemperature)
    {
        double mc = CorrectedFlow(Math.Max(0.0, massFlow), inletPressure, inletTemperature);
        double x = mc / spec.CompressorChokeFlowKgS;
        double tip = Math.Max(0.0, shaftOmega) * spec.CompressorTipRadius;
        double w = WorkCoefficient * tip * tip * WorkFactor(x);
        double cpT = PhysicalConstants.AirCp * inletTemperature;

        double eff = spec.CompressorPeakEfficiency;
        double pr = 1.0;
        for (int i = 0; i < 3; i++)
        {
            pr = Math.Pow(1.0 + eff * w / cpT, AirExponent);
            eff = Efficiency(spec, mc, pr);
        }
        bool surge = w > 0 && mc < SurgeFlow(spec, pr);
        if (surge) eff *= 0.85;
        pr = Math.Pow(1.0 + eff * w / cpT, AirExponent);
        return new CompressorPoint(pr, eff, inletTemperature + w / PhysicalConstants.AirCp, w, mc, x, surge, Math.Max(0.0, massFlow) * w);
    }

    /// <summary>Compressor pressure ratio with no flow (top of the speed line) — bounds the air-path solve.</summary>
    public static double MaxPressureRatio(TurbochargerSpec spec, double shaftOmega, double inletTemperature)
    {
        double tip = Math.Max(0.0, shaftOmega) * spec.CompressorTipRadius;
        double w = WorkCoefficient * tip * tip;
        return Math.Pow(1.0 + spec.CompressorPeakEfficiency * w / (PhysicalConstants.AirCp * inletTemperature), AirExponent);
    }

    /// <summary>Turbine power (W) and efficiency for the flow passing through the turbine wheel.</summary>
    public static (double Power, double Efficiency) Turbine(TurbochargerSpec spec, double shaftOmega, double turbineFlow,
        double inletPressure, double outletPressure, double inletTemperature)
    {
        if (turbineFlow <= 0 || inletPressure <= outletPressure) return (0.0, 0.0);
        const double g = PhysicalConstants.ExhaustGamma;
        double dhs = PhysicalConstants.ExhaustCp * inletTemperature * (1.0 - Math.Pow(outletPressure / inletPressure, (g - 1.0) / g));
        double cs = Math.Sqrt(2.0 * dhs);
        double bsr = cs > 1e-6 ? Math.Max(0.0, shaftOmega) * spec.TurbineTipRadius / cs : 0.0;
        double d = (bsr - OptimumBladeSpeedRatio) / 0.5;
        double eff = spec.TurbinePeakEfficiency * Math.Max(0.25, 1.0 - d * d);
        return (turbineFlow * eff * dhs, eff);
    }

    public static double FrictionPower(TurbochargerSpec spec, double shaftOmega) =>
        (spec.BallBearing ? BallBearingDrag : JournalBearingDrag) * shaftOmega * shaftOmega;

    /// <summary>Intercooler effectiveness at a given flow and cooling-air speed.</summary>
    public static double IntercoolerEffectiveness(IntercoolerSpec spec, double massFlow, double coolingAirSpeed)
    {
        double flowFactor = Math.Pow(spec.ReferenceFlowKgS / Math.Max(0.01, massFlow), 0.15);
        double airFactor = MathUtil.Clamp(Math.Pow(Math.Max(0.0, coolingAirSpeed) / 15.0, 0.25), 0.5, 1.1);
        return Math.Min(0.95, spec.Effectiveness * flowFactor * airFactor);
    }
}
