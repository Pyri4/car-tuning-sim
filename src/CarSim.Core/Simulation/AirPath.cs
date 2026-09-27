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
    double AirPerCycle,
    double CompressorInletPressure,
    double CompressorOutletPressure,
    double CompressorOutletTemperature,
    CompressorPoint Compressor,
    double TurbineInletPressure,
    double TurbineOutletPressure,
    double ExhaustMassFlow,
    double TurbineMassFlow,
    double WaveGain = 1.0,
    double RunnerTunedRpm = 0.0);

/// <summary>
/// Conditions the air path is solved for. <see cref="RunnerGasTemperature"/> is the temperature the intake runners' gas
/// had on the previous step (the manifold temperature; 0 = ambient): the runner wave's speed of sound is taken from it so
/// that the gain stays out of the flow root find (a one-step lag under boost, exact on a naturally aspirated engine).
/// </summary>
public readonly record struct AirPathConditions(
    double Rpm,
    double ThrottleArea,
    double AmbientPressure,
    double AmbientTemperature,
    double ExhaustGasTemperature,
    double CoolantTemperature,
    double FuelAirRatio,
    double EvaporativeCooling,
    double ValveFloatRpm,
    double TurboOmega = 0.0,
    double WastegateOpening = 0.0,
    double CoolingAirSpeed = 10.0,
    double TurbineOutletTemperature = 0.0,
    double IntakeCamAdvance = 0.0,
    int CamProfile = 0,
    int RunnerStage = 0,
    double RunnerGasTemperature = 0.0);

/// <summary>
/// One bank's air path.
/// Intake: ambient → intake/filter → [compressor → intercooler] → throttle → manifold → intake ports → cylinder.
/// Exhaust: cylinder → exhaust ports → manifold → [turbine ∥ wastegate] → exhaust system → ambient.
/// Every restriction is a compressible orifice; the compressor adds pressure and heat. The bank's cylinders act
/// as a pump whose demand is VE · ρ_port · V_d,bank · rpm/120. The through-flow is the root of
/// f(ṁ) = demand(ṁ) − ṁ (Brent's method). Elements the bank shares with others enter at its share (see
/// <see cref="BankConfiguration"/>): restrictions as its share of their area, a shared compressor or turbine at the
/// whole device's flow (ṁ / share).
/// </summary>
public sealed class AirPath
{
    /// <summary>
    /// Ceiling of valve-event filling η_ve (relative to port conditions). Fitted: re-anchored with
    /// <see cref="EngineConfiguration.BaseTunedPistonSpeed"/> on the stock K20 (Intake Gas Dynamics 2.0; it was 1.02 while the
    /// runner's ram was folded in). The runner's wave gain multiplies it.
    /// </summary>
    public const double VeCeiling = 0.973;
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

    /// <summary>Charge-pipe heat loss for a turbo without intercooler (fraction of the compressor's temperature rise).</summary>
    public const double ChargePipeCooling = 0.10;

    private readonly BankConfiguration _b;
    private readonly EngineGeometry _g;

    /// <summary>The air path of <paramref name="bank"/>, with that bank's own geometry (its clearance volume sets the residual).</summary>
    public AirPath(BankConfiguration bank)
    {
        _b = bank;
        _g = bank.Geometry;
    }

    /// <summary>The bank this air path belongs to.</summary>
    public BankConfiguration Bank => _b;

    /// <summary>
    /// Dynamic (tuning) volumetric efficiency relative to port conditions: the valve-event filling times the intake's
    /// wave gain, plus the header scavenging bump, with the valve-float collapse above the float speed —
    /// <c>VE_dyn = (η_ve · G_wave + scavenging) · float</c>. <paramref name="intakeCamAdvance"/> is the intake phaser's
    /// advance from the installed centreline (crank degrees); it acts on η_ve only. <paramref name="runnerGasTemperature"/>
    /// sets the wave's speed of sound (K).
    /// </summary>
    public double VeDynamic(double rpm, double floatRpm, double intakeCamAdvance = 0.0, int camProfile = 0, int runnerStage = 0,
        double runnerGasTemperature = IntakeGasDynamics.ReferenceTemperature) =>
        VeDynamic(rpm, floatRpm, intakeCamAdvance, camProfile, WaveGain(rpm, runnerGasTemperature, camProfile, runnerStage));

    /// <summary><see cref="VeDynamic(double, double, double, int, int, double)"/> with the wave gain already evaluated.</summary>
    private double VeDynamic(double rpm, double floatRpm, double intakeCamAdvance, int camProfile, double waveGain)
    {
        double ve = ValveEventFilling(rpm, intakeCamAdvance, camProfile) * waveGain;
        if (_b.ScavengingGain > 0)
        {
            double w = 0.25 * _b.ScavengingRpm;
            double d = (rpm - _b.ScavengingRpm) / w;
            ve += _b.ScavengingGain * Math.Exp(-d * d);
        }
        if (rpm > floatRpm)
            ve *= 1.0 - 0.6 * MathUtil.SmoothStep(floatRpm, floatRpm * 1.08, rpm);
        return ve;
    }

    /// <summary>
    /// Lower bound of the cam/runner VE shape. The parabolas are fits around the tuned speed and would go negative
    /// far from it; the floor keeps VE positive there. It lies outside every shipped cam's rev range (tested); a
    /// very long-overlap modded cam can reach it at idle, where it reads as "breathes badly", not as zero air.
    /// </summary>
    public const double VeShapeFloor = 0.25;

    /// <summary>
    /// Valve-event filling η_ve (what the cams do): the ceiling times the VE shape, above its floor. Cam phase, the cam
    /// profile and the installed centrelines act here and only here.
    /// </summary>
    public double ValveEventFilling(double rpm, double intakeCamAdvance = 0.0, int camProfile = 0) =>
        VeCeiling * Math.Max(VeShapeFloor, VeShape(rpm, intakeCamAdvance, camProfile));

    /// <summary>
    /// The intake's wave gain G_wave on runner stage <paramref name="runnerStage"/> with the runner gas at
    /// <paramref name="runnerGasTemperature"/> (K), in [1, 1 + A_max] (<see cref="IntakeGasDynamics"/>). The tuned speed
    /// has no cam input; the cam profile enters only through the intake event's length in the runner's mean flow speed.
    /// </summary>
    public double WaveGain(double rpm, double runnerGasTemperature, int camProfile = 0, int runnerStage = 0) =>
        WaveGain(rpm, runnerGasTemperature, camProfile, runnerStage, out _);

    /// <summary><see cref="WaveGain(double, double, int, int)"/>, also giving the stage's tuned speed at that temperature (rpm).</summary>
    public double WaveGain(double rpm, double runnerGasTemperature, int camProfile, int runnerStage, out double tunedRpm)
    {
        var stage = _b.RunnerStages[runnerStage];
        double a = IntakeGasDynamics.SpeedOfSound(runnerGasTemperature);
        tunedRpm = stage.TunedRpmAtReference * (a / IntakeGasDynamics.ReferenceSpeedOfSound);
        double mach = IntakeGasDynamics.RunnerMach(rpm, _g.SweptVolumePerCylinder, stage.Area, _b.Profiles[camProfile].IntakeEventFraction, a);
        return IntakeGasDynamics.Gain(rpm, tunedRpm, mach);
    }

    /// <summary>
    /// Valve-event VE shape before the floor: an inverted parabola around the tuned piston speed (steeper below it with
    /// more overlap), for the intake cam advance and cam profile the bank is running.
    /// </summary>
    public double VeShape(double rpm, double intakeCamAdvance = 0.0, int camProfile = 0)
    {
        double x = rpm / _b.VePeakRpmAt(intakeCamAdvance, camProfile);
        double aLo = VeLowSideBase + VeLowSidePerOverlapDeg * _b.OverlapAt(intakeCamAdvance, camProfile);
        return x < 1.0 ? 1.0 - aLo * (1.0 - x) * (1.0 - x) : 1.0 - VeHighSideCoefficient * (x - 1.0) * (x - 1.0);
    }

    /// <summary>
    /// Exhaust-to-intake pressure ratio up to which exhaust pulse dynamics keep the port pressure
    /// favourable during valve overlap (no net reversion). Beyond it, overlap pushes exhaust back into
    /// the intake — the classic reason long-overlap race cams suit turbocharged engines badly.
    /// </summary>
    public const double ReversionPressureRatioMargin = 1.25;

    /// <summary>
    /// Residual-gas / reversion factor (fresh charge relative to a cylinder with no residuals). Two effects
    /// of exhaust pressure above port pressure displace fresh charge, x = c·[(r^(1/n) − 1) + (overlap/15)·
    /// max(0, r − 1.25)]: (1) the burnt gas left in the clearance volume expands; (2) during valve overlap,
    /// exhaust flows back into the intake, scaled by the overlap angle. f = 1/(1 + x) — the same as 1 − x
    /// to first order, but the fresh charge only tends to zero as the backflow grows (reverted gas raises
    /// the port pressure and chokes its own backflow), so there is no floor to hit: at part load a
    /// long-overlap cam's VE falls smoothly instead of kinking onto a limit no fuel map can follow.
    /// Below 1 exhaust-to-intake ratio the residual shrinks (f slightly above 1, at most 1/(1 − c)).
    /// </summary>
    public double ResidualFactor(double exhaustPortPressure, double portPressure, double intakeCamAdvance = 0.0, int camProfile = 0)
    {
        var g = _g;
        double clearanceShare = g.ClearanceVolume / (g.ClearanceVolume + g.SweptVolumePerCylinder);
        double ratio = exhaustPortPressure / Math.Max(1.0, portPressure);
        double expansion = Math.Pow(ratio, 1.0 / PhysicalConstants.CompressionPolytropicExponent) - 1.0;
        double reversion = _b.OverlapAt(intakeCamAdvance, camProfile) / 15.0 * Math.Max(0.0, ratio - ReversionPressureRatioMargin);
        return 1.0 / (1.0 + clearanceShare * (expansion + reversion));
    }

    /// <summary>
    /// Gas temperature leaving the exhaust manifold (turbine inlet): the port gas cools towards ambient
    /// through the manifold's heat-loss conductance UA, T = T_amb + (T_port − T_amb)·exp(−UA/(ṁ·c_p)) —
    /// the exact solution for a pipe of uniform wall conductance, so no flow makes it undershoot ambient.
    /// </summary>
    public double ManifoldOutletTemperature(double portGasTemperature, double exhaustFlow, double ambientTemperature)
    {
        double excess = Math.Max(0.0, portGasTemperature - ambientTemperature);
        if (exhaustFlow <= 0) return ambientTemperature;
        return ambientTemperature + excess * Math.Exp(-_b.ExhaustManifoldHeatLoss / (exhaustFlow * PhysicalConstants.ExhaustCp));
    }

    public AirPathResult Solve(in AirPathConditions k)
    {
        var g = _g;
        double runnerGas = k.RunnerGasTemperature > 0 ? k.RunnerGasTemperature : k.AmbientTemperature;
        double waveGain = WaveGain(k.Rpm, runnerGas, k.CamProfile, k.RunnerStage, out double tunedRpm);
        if (k.Rpm <= 1.0)
        {
            return new AirPathResult(0, k.AmbientPressure, k.AmbientTemperature, k.AmbientPressure, k.AmbientTemperature,
                k.AmbientPressure, k.AmbientPressure, 0, 1, 0, k.AmbientPressure, k.AmbientPressure, k.AmbientTemperature,
                default, k.AmbientPressure, k.AmbientPressure, 0, 0, 1.0, tunedRpm);
        }
        double veDyn = VeDynamic(k.Rpm, k.ValveFloatRpm, k.IntakeCamAdvance, k.CamProfile, waveGain);
        double cyclesPerSecond = k.Rpm / 120.0;
        var turbo = _b.Turbo?.Spec;
        double prBound = turbo == null ? 1.0 : TurbochargerModel.MaxPressureRatio(turbo, k.TurboOmega, k.AmbientTemperature);

        // Upper bound: the bank's demand at the highest pressure the intake could reach, with generous VE.
        double hi = 1.3 * prBound * k.AmbientPressure / (PhysicalConstants.AirGasConstant * k.AmbientTemperature)
                    * _b.SweptVolume * cyclesPerSecond;
        double flow = RootFinder.Brent(static (m, s) => s.Path.Evaluate(m, s.VeDyn, in s.Conditions).MassFlow - m,
            (Path: this, VeDyn: veDyn, Conditions: k), 0.0, hi, 1e-9 * hi);
        return Evaluate(flow, veDyn, in k) with { WaveGain = waveGain, RunnerTunedRpm = tunedRpm };
    }

    /// <summary>
    /// For an assumed through-flow, walk the restrictions and return the engine's demand
    /// (<see cref="AirPathResult.MassFlow"/>) and the pressures along the way.
    /// </summary>
    internal AirPathResult Evaluate(double massFlow, double veDyn, in AirPathConditions k)
    {
        var g = _g;
        const double gamma = PhysicalConstants.AirGamma;
        const double rAir = PhysicalConstants.AirGasConstant;
        int n = _b.Cylinders;
        var turbo = _b.Turbo?.Spec;
        var profile = _b.Profiles[k.CamProfile];

        // Intake side.
        double tIn = k.AmbientTemperature;
        double p1 = CompressibleFlow.DownstreamPressure(_b.IntakeCdA, k.AmbientPressure, tIn, massFlow, gamma, rAir, out _);
        double pBoost = p1, tBoost = tIn;
        CompressorPoint comp = default;
        if (turbo != null)
        {
            // A compressor shared with other banks runs at the whole device's flow (ṁ / share).
            comp = TurbochargerModel.Compressor(turbo, k.TurboOmega, massFlow / _b.TurboShare, p1, tIn);
            double p2 = p1 * comp.PressureRatio;
            double t2 = comp.OutletTemperature;
            if (_b.Intercooler != null)
            {
                pBoost = CompressibleFlow.DownstreamPressure(_b.IntercoolerCdA, p2, t2, massFlow, gamma, rAir, out _);
                double eff = TurbochargerModel.IntercoolerEffectiveness(_b.Intercooler, massFlow / _b.IntercoolerShare, k.CoolingAirSpeed);
                tBoost = t2 - eff * (t2 - k.AmbientTemperature);
            }
            else
            {
                pBoost = p2;
                tBoost = t2 - ChargePipeCooling * (t2 - tIn);
            }
        }
        double pMan = CompressibleFlow.DownstreamPressure(k.ThrottleArea, pBoost, tBoost, massFlow, gamma, rAir, out _);
        double tMan = tBoost;
        double portEventFlow = massFlow / n / profile.IntakeEventFraction;
        double pPort = CompressibleFlow.DownstreamPressure(profile.IntakePortCdA, pMan, tMan, portEventFlow, gamma, rAir, out _);

        double tCharge = tMan + 0.12 * Math.Max(0.0, k.CoolantTemperature - tMan) - k.EvaporativeCooling;
        tCharge = Math.Max(200.0, tCharge);

        // Exhaust side, solved backwards from ambient.
        double exhaustFlow = massFlow * (1.0 + k.FuelAirRatio);
        double tExh = Math.Max(k.AmbientTemperature, k.ExhaustGasTemperature);
        const double gE = PhysicalConstants.ExhaustGamma;
        const double rE = PhysicalConstants.ExhaustGasConstant;
        double pSystemIn, pTurbineIn, turbineFlow = 0.0;
        if (turbo != null)
        {
            double tAfterTurbine = k.TurbineOutletTemperature > 0 ? k.TurbineOutletTemperature : tExh;
            double tPipe = Math.Max(k.AmbientTemperature, tAfterTurbine - ExhaustPipeCooling);
            pSystemIn = CompressibleFlow.UpstreamPressure(_b.ExhaustSystemCdA, k.AmbientPressure, tPipe, exhaustFlow, gE, rE);
            // This bank's share of a turbine (and wastegate) it shares with other banks.
            double turbineArea = turbo.TurbineFlowArea * _b.TurboShare;
            double totalArea = turbineArea + turbo.WastegateFlowArea * _b.TurboShare * MathUtil.Clamp01(k.WastegateOpening);
            double tTurbineIn = ManifoldOutletTemperature(tExh, exhaustFlow, k.AmbientTemperature);
            pTurbineIn = CompressibleFlow.UpstreamPressure(totalArea, pSystemIn, tTurbineIn, exhaustFlow, gE, rE);
            turbineFlow = exhaustFlow * turbineArea / totalArea;
        }
        else
        {
            double tPipe = Math.Max(k.AmbientTemperature, tExh - ExhaustPipeCooling);
            pSystemIn = CompressibleFlow.UpstreamPressure(_b.ExhaustSystemCdA, k.AmbientPressure, tPipe, exhaustFlow, gE, rE);
            pTurbineIn = pSystemIn;
        }
        double pExhManifold = CompressibleFlow.UpstreamPressure(_b.ExhaustManifoldCdA, pTurbineIn, tExh, exhaustFlow, gE, rE);
        double exhaustEventFlow = exhaustFlow * (1.0 - BlowdownFraction) / n / profile.ExhaustEventFraction;
        double pExhPort = CompressibleFlow.UpstreamPressure(profile.ExhaustPortCdA, pExhManifold, tExh, exhaustEventFlow, gE, rE);

        double residual = ResidualFactor(pExhPort, pPort, k.IntakeCamAdvance, k.CamProfile);
        double airPerCycle = veDyn * residual * pPort * g.SweptVolumePerCylinder / (rAir * tCharge);
        double demand = airPerCycle * n * k.Rpm / 120.0;
        return new AirPathResult(demand, pMan, tMan, pPort, tCharge, pExhPort, pExhManifold, veDyn, residual, airPerCycle,
            p1, turbo != null ? p1 * comp.PressureRatio : p1, turbo != null ? comp.OutletTemperature : tIn, comp,
            pTurbineIn, pSystemIn, exhaustFlow, turbineFlow);
    }
}
