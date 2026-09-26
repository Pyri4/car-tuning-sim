using CarSim.Core.Common;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Simulation;

/// <summary>Compressor operating point.</summary>
/// <param name="PressureRatio">Total outlet / inlet pressure.</param>
/// <param name="Efficiency">
/// Effective isentropic efficiency (isentropic work / actual work). Falls to zero at choke and goes
/// negative beyond it, where the compressor is a restriction rather than a pump.
/// </param>
/// <param name="OutletTemperature">Charge temperature at the outlet, K.</param>
/// <param name="SpecificWork">Work done on each kilogram of through-flow, J/kg.</param>
/// <param name="CorrectedFlow">Through-flow corrected to 1 atm, 298.15 K, kg/s.</param>
/// <param name="ChokeRatio">Corrected flow / choke flow at the current speed.</param>
/// <param name="Surge">Operating left of the surge line.</param>
/// <param name="Power">
/// Total shaft power absorbed, W: through-flow work plus recirculation in surge plus disk friction.
/// </param>
/// <param name="SpeedRatio">Corrected tip speed / tip speed at the rated maximum shaft speed.</param>
/// <param name="SurgeDepth">How far left of the surge line: 0 on or right of it, 1 at zero flow.</param>
public readonly record struct CompressorPoint(
    double PressureRatio,
    double Efficiency,
    double OutletTemperature,
    double SpecificWork,
    double CorrectedFlow,
    double ChokeRatio,
    bool Surge,
    double Power,
    double SpeedRatio = 0.0,
    double SurgeDepth = 0.0);

/// <summary>
/// Parametric turbocharger model built from the numbers a compressor map conveys. Closed form: no
/// iteration, so every operating point has exactly one answer and it varies continuously.
///
/// Compressor (all flows corrected to 1 atm, 298.15 K; n = corrected tip speed / tip speed at max rpm):
/// <list type="bullet">
/// <item>Euler work w = σ·U²·(1 − β·x/(1 + x/2)) with x = flow / choke flow. Backswept blades do a
/// little less work per kilogram at high flow, but the work never collapses: past choke the wheel still
/// absorbs power, it just stops building pressure.</item>
/// <item>Choke flow grows with speed: ṁ_choke(n) = ṁ_choke,max·√n. A small compressor can only pass more
/// air by spinning faster — the classic way small turbos over-speed at high engine rpm.</item>
/// <item>Efficiency island η = η_peak / (1 + a_m·Δm² + a_n·Δn²) around the authored peak point, a
/// function of flow and speed only (never of pressure ratio), multiplied by a choke collapse (1 →
/// 0 between 85 % and 100 % of choke flow) and a smooth surge penalty.</item>
/// <item>PR = (1 + η·w/(c_p·T₁))^(γ/(γ−1)) × choke loss, where the choke loss
/// exp(−k·((x − 1)·n)²) for x > 1 makes the wheel a restriction beyond choke, growing with tip speed.
/// PR rises monotonically with speed at any flow up to twice the choke flow.</item>
/// <item>Outlet temperature T₂ = T₁ + w/c_p (all through-flow work heats the charge). Shaft power adds
/// recirculating flow in surge and disk friction, which heat the housing rather than the charge.</item>
/// </list>
///
/// Turbine: a nozzle of the authored effective area (in parallel with the wastegate) sets the exhaust
/// backpressure; power = ṁ_t·η_t·c_p·T₃·(1 − (p₄/p₃)^((γ−1)/γ)) with η_t a function of blade-speed ratio (negative
/// past BSR 1.2: a windmilling wheel does work on the gas).
/// </summary>
public static class TurbochargerModel
{
    /// <summary>Euler work coefficient σ at zero flow (slip and blade backsweep).</summary>
    public const double WorkCoefficient = 0.68;

    /// <summary>Backsweep: fractional loss of work per unit of flow ratio (β).</summary>
    public const double WorkBacksweep = 0.25;

    public const double ReferenceTemperature = 298.15;
    public const double ReferencePressure = PhysicalConstants.StandardPressure;
    public const double JournalBearingDrag = 2.0e-6;
    public const double BallBearingDrag = 1.0e-6;
    public const double OptimumBladeSpeedRatio = 0.7;

    /// <summary>Island width in flow: coefficient on ((ṁ − ṁ_peak)/ṁ_choke,max)².</summary>
    public const double IslandFlowWidth = 1.0;

    /// <summary>Island width in speed: coefficient on (n − n_peak)².</summary>
    public const double IslandSpeedWidth = 0.5;

    /// <summary>Fraction of the choke flow at which efficiency starts to collapse.</summary>
    public const double ChokeOnset = 0.85;

    /// <summary>Strength of the pressure loss beyond choke (k).</summary>
    public const double ChokeLossCoefficient = 10.0;

    /// <summary>Efficiency lost deep in surge, and the surge depth over which the loss builds.</summary>
    public const double SurgeEfficiencyLoss = 0.15, SurgeBand = 0.5;

    /// <summary>Share of the flow deficit below the surge line that recirculates through the wheel.</summary>
    public const double RecirculationFraction = 0.5;

    /// <summary>Disk-friction moment coefficient: P = ½·C·ρ·ω³·r⁵.</summary>
    public const double DiskFrictionCoefficient = 0.005;

    private static readonly double AirExponent = PhysicalConstants.AirGamma / (PhysicalConstants.AirGamma - 1.0);

    public static double CorrectedFlow(double massFlow, double inletPressure, double inletTemperature) =>
        massFlow * Math.Sqrt(inletTemperature / ReferenceTemperature) / (inletPressure / ReferencePressure);

    /// <summary>Corrected tip speed relative to the tip speed at the rated maximum shaft speed.</summary>
    public static double SpeedRatio(TurbochargerSpec spec, double shaftOmega, double inletTemperature) =>
        Math.Max(0.0, shaftOmega) / spec.MaxShaftSpeed * Math.Sqrt(ReferenceTemperature / inletTemperature);

    /// <summary>Corrected flow at which the compressor chokes at speed ratio <paramref name="n"/>.</summary>
    public static double ChokeFlow(TurbochargerSpec spec, double n) => spec.CompressorChokeFlowKgS * Math.Sqrt(Math.Max(0.0, n));

    public static double SurgeFlow(TurbochargerSpec spec, double pressureRatio) =>
        spec.CompressorSurgeFlowAtPr2KgS * Math.Max(0.0, pressureRatio - 1.0);

    /// <summary>Euler-work multiplier for flow ratio x: 1 at zero flow, bounded below by 1 − 2β.</summary>
    public static double WorkFlowFactor(double chokeRatio)
    {
        double x = Math.Max(0.0, chokeRatio);
        return 1.0 - WorkBacksweep * x / (1.0 + 0.5 * x);
    }

    /// <summary>
    /// Speed ratio at which the speed line passes through the authored efficiency-island centre
    /// (peak-efficiency flow and pressure ratio). A contraction mapping: the flow ratio depends only
    /// weakly on speed, so a few iterations converge to machine precision.
    /// </summary>
    public static double IslandSpeedRatio(TurbochargerSpec spec)
    {
        double uMax = spec.MaxShaftSpeed * spec.CompressorTipRadius;
        double isentropic = PhysicalConstants.AirCp * ReferenceTemperature
                            * (Math.Pow(spec.CompressorPeakEfficiencyPressureRatio, 1.0 / AirExponent) - 1.0);
        double work = isentropic / spec.CompressorPeakEfficiency;
        double n = 0.7;
        for (int i = 0; i < 8; i++)
        {
            double x = spec.CompressorPeakEfficiencyFlowKgS / ChokeFlow(spec, n);
            n = Math.Sqrt(work / (WorkCoefficient * WorkFlowFactor(x))) / uMax;
        }
        return n;
    }

    /// <summary>Efficiency-island value at a corrected flow and speed ratio (before choke and surge).</summary>
    public static double IslandEfficiency(TurbochargerSpec spec, double correctedFlow, double speedRatio, double islandSpeedRatio)
    {
        double dm = (correctedFlow - spec.CompressorPeakEfficiencyFlowKgS) / spec.CompressorChokeFlowKgS;
        double dn = speedRatio - islandSpeedRatio;
        return spec.CompressorPeakEfficiency / (1.0 + IslandFlowWidth * dm * dm + IslandSpeedWidth * dn * dn);
    }

    public static CompressorPoint Compressor(TurbochargerSpec spec, double shaftOmega, double massFlow, double inletPressure, double inletTemperature)
    {
        double m = Math.Max(0.0, massFlow);
        double mc = CorrectedFlow(m, inletPressure, inletTemperature);
        double omega = Math.Max(0.0, shaftOmega);
        double tip = omega * spec.CompressorTipRadius;
        if (tip <= 1e-6)
            return new CompressorPoint(1.0, 0.0, inletTemperature, 0.0, mc, 0.0, false, 0.0, 0.0);

        double n = SpeedRatio(spec, omega, inletTemperature);
        double c = mc / spec.CompressorChokeFlowKgS;
        double sqrtN = Math.Sqrt(n);
        double x = c / sqrtN;
        double w = WorkCoefficient * tip * tip * WorkFlowFactor(x);
        double cpT = PhysicalConstants.AirCp * inletTemperature;

        double eta = IslandEfficiency(spec, mc, n, IslandSpeedRatio(spec)) * (1.0 - MathUtil.SmoothStep(ChokeOnset, 1.0, x));

        // Surge margin against the pressure ratio the speed line makes before surge losses.
        double pr0 = Math.Pow(1.0 + eta * w / cpT, AirExponent);
        double surgeFlow = SurgeFlow(spec, pr0);
        double depth = surgeFlow > 0 ? MathUtil.Clamp01((surgeFlow - mc) / surgeFlow) : 0.0;
        eta *= 1.0 - SurgeEfficiencyLoss * MathUtil.SmoothStep(0.0, SurgeBand, depth);

        // Beyond choke the wheel is a restriction; (x − 1)·n = c·√n − n keeps the loss finite as n → 0.
        double overChoke = Math.Max(0.0, c * sqrtN - n);
        double pr = Math.Pow(1.0 + eta * w / cpT, AirExponent) * Math.Exp(-ChokeLossCoefficient * overChoke * overChoke);
        double effective = (Math.Pow(pr, 1.0 / AirExponent) - 1.0) * cpT / w;

        double actualPerCorrected = (inletPressure / ReferencePressure) / Math.Sqrt(inletTemperature / ReferenceTemperature);
        double recirculating = RecirculationFraction * Math.Max(0.0, surgeFlow - mc) * actualPerCorrected;
        double density = inletPressure / (PhysicalConstants.AirGasConstant * inletTemperature);
        double r = spec.CompressorTipRadius;
        double disk = 0.5 * DiskFrictionCoefficient * density * omega * omega * omega * r * r * r * r * r;
        double power = (m + recirculating) * w + disk;
        return new CompressorPoint(pr, effective, inletTemperature + w / PhysicalConstants.AirCp, w, mc, x, mc < surgeFlow, power, n, depth);
    }

    /// <summary>Upper bound on the pressure ratio at this shaft speed (peak efficiency, zero flow) — brackets the air-path solve.</summary>
    public static double MaxPressureRatio(TurbochargerSpec spec, double shaftOmega, double inletTemperature)
    {
        double tip = Math.Max(0.0, shaftOmega) * spec.CompressorTipRadius;
        double w = WorkCoefficient * tip * tip;
        return Math.Pow(1.0 + spec.CompressorPeakEfficiency * w / (PhysicalConstants.AirCp * inletTemperature), AirExponent);
    }

    /// <summary>
    /// Floor of the turbine efficiency parabola below its optimum, as a share of the peak. The parabola
    /// 1 − ((BSR − 0.7)/0.5)² reaches zero at blade-speed ratio 0.2; a real radial turbine still extracts work from a
    /// slow wheel (the stalled wheel turns the flow), and the energy form of the shaft equation needs power at zero
    /// speed to spool from rest. Reached spooling up from standstill and at idle, never on boost (tested). Above the
    /// optimum there is no floor: past BSR 1.2 the efficiency goes negative and a wheel spinning faster than its gas
    /// windmills — it does work on the gas instead (bounded: ≈ 2·η_peak·ṁ·U² as BSR → ∞), which is what slows a turbo
    /// on overrun, when a few g/s trickle through it.
    /// </summary>
    public const double TurbineEfficiencyFloor = 0.25;

    /// <summary>Turbine power (W), efficiency and blade-speed ratio for the flow passing through the turbine wheel.</summary>
    public static (double Power, double Efficiency, double BladeSpeedRatio) Turbine(TurbochargerSpec spec, double shaftOmega, double turbineFlow,
        double inletPressure, double outletPressure, double inletTemperature)
    {
        if (turbineFlow <= 0 || inletPressure <= outletPressure) return (0.0, 0.0, 0.0);
        const double g = PhysicalConstants.ExhaustGamma;
        double dhs = PhysicalConstants.ExhaustCp * inletTemperature * (1.0 - Math.Pow(outletPressure / inletPressure, (g - 1.0) / g));
        double cs = Math.Sqrt(2.0 * dhs);
        double bsr = cs > 1e-6 ? Math.Max(0.0, shaftOmega) * spec.TurbineTipRadius / cs : 0.0;
        double d = (bsr - OptimumBladeSpeedRatio) / 0.5;
        double shape = 1.0 - d * d;
        double eff = spec.TurbinePeakEfficiency * (bsr < OptimumBladeSpeedRatio ? Math.Max(TurbineEfficiencyFloor, shape) : shape);
        return (turbineFlow * eff * dhs, eff, bsr);
    }

    /// <summary>Whether the turbine efficiency is held up by <see cref="TurbineEfficiencyFloor"/> at this blade-speed ratio.</summary>
    public static bool TurbineOnEfficiencyFloor(double bladeSpeedRatio)
    {
        double d = (bladeSpeedRatio - OptimumBladeSpeedRatio) / 0.5;
        return bladeSpeedRatio < OptimumBladeSpeedRatio && 1.0 - d * d < TurbineEfficiencyFloor;
    }

    /// <summary>Extra bearing drag of a fully worn bearing (shaft play lets the wheels rub): drag × (1 + 3·wear).</summary>
    public const double WornBearingDragIncrease = 3.0;

    public static double FrictionPower(TurbochargerSpec spec, double shaftOmega, double bearingWear = 0.0) =>
        (spec.BallBearing ? BallBearingDrag : JournalBearingDrag) * (1.0 + WornBearingDragIncrease * Math.Clamp(bearingWear, 0.0, 1.0))
        * shaftOmega * shaftOmega;

    /// <summary>Intercooler effectiveness at a given flow and cooling-air speed.</summary>
    public static double IntercoolerEffectiveness(IntercoolerSpec spec, double massFlow, double coolingAirSpeed)
    {
        double flowFactor = Math.Pow(spec.ReferenceFlowKgS / Math.Max(0.01, massFlow), 0.15);
        double airFactor = MathUtil.Clamp(Math.Pow(Math.Max(0.0, coolingAirSpeed) / 15.0, 0.25), 0.5, 1.1);
        return Math.Min(0.95, spec.Effectiveness * flowFactor * airFactor);
    }
}
