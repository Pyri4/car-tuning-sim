using CarSim.Core.Common;
using CarSim.Core.Engines;

namespace CarSim.Core.Simulation;

/// <summary>Quasi-steady solution of the air path for one step.</summary>
public readonly record struct AirPathResult(
    double MassFlow,
    double ManifoldPressure,
    double ManifoldTemperature,
    double PortPressure,
    double ChargeTemperature,
    double ExhaustPortPressure,
    double ExhaustManifoldPressure,
    double VeDynamic,
    double ResidualFactor,
    double AirPerCycle);

/// <summary>Conditions the air path is solved for.</summary>
public readonly record struct AirPathConditions(
    double Rpm,
    double ThrottleArea,
    double AmbientPressure,
    double AmbientTemperature,
    double ExhaustGasTemperature,
    double CoolantTemperature,
    double FuelAirRatio,
    double EvaporativeCooling,
    double ValveFloatRpm);

/// <summary>
/// Intake: ambient → intake/filter → throttle → manifold → intake ports → cylinder.
/// Exhaust: cylinder → exhaust ports → manifold → exhaust system → ambient.
/// Every element is a compressible orifice. The engine acts as a pump whose demand is
/// VE · ρ_port · V_d · rpm/120. The through-flow is the root of f(ṁ) = demand(ṁ) − ṁ, found by
/// bisection (f is monotonic because every restriction's pressure drop grows with ṁ).
/// </summary>
public sealed class AirPath
{
    public const double VeCeiling = 1.02;
    public const double VeHighSideCoefficient = 0.25;
    public const double VeLowSideBase = 0.50;
    public const double VeLowSidePerOverlapDeg = 0.004;
    public const double ExhaustPipeCooling = 150.0;

    /// <summary>
    /// Share of the exhaust mass that leaves during blowdown, driven by residual cylinder pressure
    /// rather than pumped out by the piston. Only the remainder loads the exhaust ports during the
    /// exhaust stroke.
    /// </summary>
    public const double BlowdownFraction = 0.5;

    private readonly EngineConfiguration _c;

    public AirPath(EngineConfiguration config) => _c = config;

    /// <summary>
    /// Dynamic (tuning) volumetric efficiency relative to port conditions: cam/runner resonance shape,
    /// header scavenging bump, valve float collapse above the float speed.
    /// </summary>
    public double VeDynamic(double rpm, double floatRpm)
    {
        double x = rpm / _c.VePeakRpm;
        double aLo = VeLowSideBase + VeLowSidePerOverlapDeg * _c.OverlapDeg;
        double shape = x < 1.0 ? 1.0 - aLo * (1.0 - x) * (1.0 - x) : 1.0 - VeHighSideCoefficient * (x - 1.0) * (x - 1.0);
        double ve = VeCeiling * Math.Max(0.25, shape);
        if (_c.ScavengingGain > 0)
        {
            double w = 0.25 * _c.ScavengingRpm;
            double d = (rpm - _c.ScavengingRpm) / w;
            ve += _c.ScavengingGain * Math.Exp(-d * d);
        }
        if (rpm > floatRpm)
            ve *= 1.0 - 0.6 * MathUtil.SmoothStep(floatRpm, floatRpm * 1.08, rpm);
        return ve;
    }

    /// <summary>
    /// Exhaust-to-intake pressure ratio up to which exhaust pulse dynamics keep the port pressure
    /// favourable during valve overlap (no net reversion). Beyond it, overlap pushes exhaust back into
    /// the intake — the classic reason long-overlap race cams suit turbocharged engines badly.
    /// </summary>
    public const double ReversionPressureRatioMargin = 1.25;

    /// <summary>
    /// Residual-gas / reversion factor. Two effects of exhaust pressure above port pressure:
    /// (1) the burnt gas left in the clearance volume expands and displaces fresh charge;
    /// (2) during valve overlap, exhaust flows back into the intake, scaled by the overlap angle.
    /// </summary>
    public double ResidualFactor(double exhaustPortPressure, double portPressure)
    {
        var g = _c.Geometry;
        double clearanceShare = g.ClearanceVolume / (g.ClearanceVolume + g.SweptVolumePerCylinder);
        double ratio = exhaustPortPressure / Math.Max(1.0, portPressure);
        double expansion = Math.Pow(ratio, 1.0 / PhysicalConstants.CompressionPolytropicExponent) - 1.0;
        double reversion = _c.OverlapDeg / 15.0 * Math.Max(0.0, ratio - ReversionPressureRatioMargin);
        double f = 1.0 - clearanceShare * (expansion + reversion);
        return MathUtil.Clamp(f, 0.4, 1.03);
    }

    public AirPathResult Solve(in AirPathConditions k)
    {
        var g = _c.Geometry;
        if (k.Rpm <= 1.0)
        {
            return new AirPathResult(0, k.AmbientPressure, k.AmbientTemperature, k.AmbientPressure, k.AmbientTemperature,
                k.AmbientPressure, k.AmbientPressure, 0, 1, 0);
        }
        double veDyn = VeDynamic(k.Rpm, k.ValveFloatRpm);
        double cyclesPerSecond = k.Rpm / 120.0;

        // Upper bound: engine demand at ambient conditions with generous VE.
        double hi = 1.3 * k.AmbientPressure / (PhysicalConstants.AirGasConstant * k.AmbientTemperature)
                    * g.Displacement * cyclesPerSecond;
        var conditions = k;
        double flow = RootFinder.Brent(m => Evaluate(m, veDyn, in conditions).MassFlow - m, 0.0, hi, 1e-9 * hi);
        return Evaluate(flow, veDyn, in k);
    }

    /// <summary>
    /// For an assumed through-flow, walk the restrictions and return the engine's demand
    /// (<see cref="AirPathResult.MassFlow"/>) and the pressures along the way.
    /// </summary>
    private AirPathResult Evaluate(double massFlow, double veDyn, in AirPathConditions k)
    {
        var g = _c.Geometry;
        const double gamma = PhysicalConstants.AirGamma;
        const double rAir = PhysicalConstants.AirGasConstant;
        int n = g.Cylinders;

        // Intake side.
        double tIn = k.AmbientTemperature;
        double p1 = CompressibleFlow.DownstreamPressure(_c.IntakeCdA, k.AmbientPressure, tIn, massFlow, gamma, rAir, out _);
        double pMan = CompressibleFlow.DownstreamPressure(k.ThrottleArea, p1, tIn, massFlow, gamma, rAir, out _);
        double tMan = tIn;
        double portEventFlow = massFlow / n / _c.IntakeEventFraction;
        double pPort = CompressibleFlow.DownstreamPressure(_c.IntakePortCdA, pMan, tMan, portEventFlow, gamma, rAir, out _);

        double tCharge = tMan + 0.12 * Math.Max(0.0, k.CoolantTemperature - tMan) - k.EvaporativeCooling;
        tCharge = Math.Max(200.0, tCharge);

        // Exhaust side, solved backwards from ambient.
        double exhaustFlow = massFlow * (1.0 + k.FuelAirRatio);
        double tExh = Math.Max(k.AmbientTemperature, k.ExhaustGasTemperature);
        double tPipe = Math.Max(k.AmbientTemperature, tExh - ExhaustPipeCooling);
        const double gE = PhysicalConstants.ExhaustGamma;
        const double rE = PhysicalConstants.ExhaustGasConstant;
        double pSystemIn = CompressibleFlow.UpstreamPressure(_c.ExhaustSystemCdA, k.AmbientPressure, tPipe, exhaustFlow, gE, rE);
        double pExhManifold = CompressibleFlow.UpstreamPressure(_c.ExhaustManifoldCdA, pSystemIn, tExh, exhaustFlow, gE, rE);
        double exhaustEventFlow = exhaustFlow * (1.0 - BlowdownFraction) / n / _c.ExhaustEventFraction;
        double pExhPort = CompressibleFlow.UpstreamPressure(_c.ExhaustPortCdA, pExhManifold, tExh, exhaustEventFlow, gE, rE);

        double residual = ResidualFactor(pExhPort, pPort);
        double airPerCycle = veDyn * residual * pPort * g.SweptVolumePerCylinder / (rAir * tCharge);
        double demand = airPerCycle * n * k.Rpm / 120.0;
        return new AirPathResult(demand, pMan, tMan, pPort, tCharge, pExhPort, pExhManifold, veDyn, residual, airPerCycle);
    }
}
