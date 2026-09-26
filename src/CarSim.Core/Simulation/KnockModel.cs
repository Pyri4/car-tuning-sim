using CarSim.Core.Common;

namespace CarSim.Core.Simulation;

/// <summary>
/// End-gas autoignition (Livengood–Wu): the unburned mixture ahead of the flame autoignites — knocks — if
/// ∫ dt/τ(p, T) reaches 1 before the flame consumes it. τ is the Douaud–Eyzat ignition delay
/// <c>17.68 ms · (ON/100)^3.402 · (p/atm)^−1.7 · exp(3800 K / T)</c>. The pressure comes from a single-zone
/// cycle: polytropic compression from the port pressure and the charge temperature (plus its hot
/// residual gas), heat released on a
/// Wiebe curve whose duration matches the best-torque (MBT) timing model; the end gas is compressed
/// isentropically with it. Every knock factor falls out of one mechanism: octane (τ, with the fuel's
/// sensitivity counting under boost), compression ratio
/// and boost (end-gas pressure and temperature), charge and coolant temperature, rpm (less time), spark
/// advance (earlier, higher pressure), a rich mixture (cooler, less heat, a slower end gas) and poor
/// quench (a slow-burning end gas). There is no light-load patch: at light load the end gas simply never gets hot and dense
/// enough.
/// </summary>
public static class KnockModel
{
    /// <summary>Douaud–Eyzat constants: τ = A·(ON/100)^b·(p/atm)^c·exp(B/T).</summary>
    public const double DelayCoefficient = 17.68e-3, OctaneExponent = 3.402, PressureExponent = -1.7, ActivationTemperature = 3800.0;

    /// <summary>Polytropic exponent of compression and of the unburned end gas.</summary>
    public const double PolytropicExponent = 1.32;

    /// <summary>Heat released per kg of air burned stoichiometrically (Thornton's rule: the same for any fuel), J/kg.</summary>
    public const double HeatPerKgAir = 2.94e6;

    /// <summary>Share of the released heat that raises the cylinder pressure (the rest goes into the walls).</summary>
    public const double HeatReleaseEfficiency = 0.80;

    /// <summary>Wiebe efficiency and shape (a = 5 burns 99.3 % over the duration; m + 1 = 3).</summary>
    public const double WiebeA = 5.0, WiebeExponent = 3.0;

    /// <summary>The end gas is gone once this fraction of the charge has burned.</summary>
    public const double EndGasConsumedAt = 0.90;

    /// <summary>MBT places 50 % burned at this many degrees after top dead centre.</summary>
    public const double Ca50AtMbtDeg = 8.0;

    /// <summary>Extra crank degrees the end gas survives per mm of deck clearance beyond 1 mm (poor squish).</summary>
    public const double QuenchDelayPerMm = 3.0;

    /// <summary>End-gas temperature rise per kelvin of coolant above 90 °C (hot chamber walls).</summary>
    public const double WallHeatingPerKelvin = 0.5;

    /// <summary>Temperature of the burnt gas left in the clearance volume when the inlet valve closes, K.</summary>
    public const double ResidualGasTemperature = 900.0;

    /// <summary>Connecting-rod ratio r/l used for the volume curve.</summary>
    public const double CrankRodRatio = 0.3;

    /// <summary>
    /// Ignition delay × exp(k·(1 − λ)): a rich end gas autoignites later (the excess fuel's heat capacity
    /// and evaporation), a lean one sooner. Douaud–Eyzat has no mixture term; empirical.
    /// </summary>
    public const double MixtureDelayCoefficient = 2.0;

    /// <summary>
    /// Kalghatgi octane index: the fuel's effective octane OI = RON − K·(RON − MON). K is 0 for a naturally
    /// aspirated engine (RON alone) and falls by this much per bar of boost to <see cref="MinOctaneIndexK"/>:
    /// boosted engines compress their end gas cooler for its pressure, where high-sensitivity fuels
    /// (ethanol blends) resist autoignition beyond their RON. Empirical, after Kalghatgi (2001–2005).
    /// </summary>
    public const double OctaneIndexKPerBar = -0.5, MinOctaneIndexK = -1.0;

    /// <summary>Effective octane under these conditions.</summary>
    public static double OctaneIndex(in CombustionModel.KnockConditions c)
    {
        double boostBar = Math.Max(0.0, c.PortPressurePa / PhysicalConstants.StandardPressure - 1.0);
        double k = Math.Max(MinOctaneIndexK, OctaneIndexKPerBar * boostBar);
        return c.Octane - k * c.OctaneSensitivity;
    }

    /// <summary>Crank-angle step of the cycle integration, degrees.</summary>
    public const double StepDeg = 2.0;

    /// <summary>Where the integration starts (degrees after TDC): before this the end gas is far too cold to matter.</summary>
    private const double StartDeg = -70.0;

    private static readonly double Ca50Fraction = Math.Pow(Math.Log(2.0) / WiebeA, 1.0 / WiebeExponent);

    /// <summary>Douaud–Eyzat autoignition delay, s.</summary>
    public static double AutoignitionDelay(double pressurePa, double temperatureK, double octane) =>
        DelayCoefficient * Math.Pow(octane / 100.0, OctaneExponent)
        * Math.Pow(pressurePa / PhysicalConstants.StandardPressure, PressureExponent) * Math.Exp(ActivationTemperature / temperatureK);

    /// <summary>Burn duration (crank degrees over which the Wiebe curve burns the charge) consistent with MBT timing.</summary>
    public static double BurnDurationDeg(double rpm, double chargeDensityRatio) =>
        (CombustionModel.MbtAdvance(rpm, chargeDensityRatio) + Ca50AtMbtDeg) / Ca50Fraction;

    /// <summary>
    /// Livengood–Wu knock integral at <paramref name="advanceDeg"/> (BTDC): ≥ 1 means the end gas autoignites
    /// before the flame reaches it.
    /// </summary>
    public static double KnockIntegral(in CombustionModel.KnockConditions c, double advanceDeg) => Cycle(c, advanceDeg).Integral;

    /// <summary>Peak pressure of the single-zone cycle the knock integral runs on, Pa (for calibration checks).</summary>
    public static double CyclePeakPressure(in CombustionModel.KnockConditions c, double advanceDeg) => Cycle(c, advanceDeg).PeakPressure;

    private static (double Integral, double PeakPressure) Cycle(in CombustionModel.KnockConditions c, double advanceDeg)
    {
        if (c.Rpm <= 1 || c.PortPressurePa <= 0 || c.ChargeTemperatureK <= 0) return (0.0, 0.0);
        double cr = c.CompressionRatio;
        double density = c.PortPressurePa / (PhysicalConstants.AirGasConstant * c.ChargeTemperatureK) / ReferenceDensity;
        double duration = BurnDurationDeg(c.Rpm, density);
        double spark = -advanceDeg;
        // Charge (per unit clearance volume): air mass and the heat it can release.
        double lambda = c.Lambda;
        double heatShare = lambda >= 1.0 ? 1.0 / lambda : CombustionModel.RichHeatRelease(lambda);
        double chargeTemperature = CompressionStartTemperature(c);
        double vIvc = cr; // BDC volume / clearance volume
        double airMass = c.PortPressurePa * vIvc / (PhysicalConstants.AirGasConstant * chargeTemperature);
        double heat = airMass * HeatPerKgAir * heatShare * HeatReleaseEfficiency;
        double wallHeating = WallHeatingPerKelvin * Math.Max(0.0, c.CoolantTemperatureK - 363.15);
        double lnOctane = OctaneExponent * Math.Log(OctaneIndex(c) / 100.0)
                          + MixtureDelayCoefficient * (1.0 - Math.Clamp(lambda, 0.6, 1.3));
        double secondsPerDeg = 1.0 / (6.0 * c.Rpm);
        double n = PolytropicExponent;
        double quenchDelay = QuenchDelayPerMm * Math.Max(0.0, c.DeckClearanceMm - 1.0);
        double portPressure = c.PortPressurePa;

        // Pressure at the start of the integration (polytropic from BDC).
        double v = Volume(StartDeg, cr);
        double p = c.PortPressurePa * Math.Pow(vIvc / v, n);
        double burned = 0.0, integral = 0.0, lastRate = Rate(p), endAt = double.PositiveInfinity, peak = p;
        for (double theta = StartDeg + StepDeg; theta <= 180.0; theta += StepDeg)
        {
            double vNew = Volume(theta, cr);
            double xb = theta > spark ? 1.0 - Math.Exp(-WiebeA * Math.Pow((theta - spark) / duration, WiebeExponent)) : 0.0;
            double dQ = heat * (xb - burned);
            // Single zone: dp = (−n·p·dV + (n − 1)·dQ) / V, trapezoidal in V.
            double vMid = 0.5 * (v + vNew);
            p = Math.Max(1.0, p + (-n * p * (vNew - v) + (n - 1.0) * dQ) / vMid);
            peak = Math.Max(peak, p);
            v = vNew;
            burned = xb;
            if (theta <= endAt)
            {
                double rate = Rate(p);
                integral += 0.5 * (rate + lastRate) * StepDeg * secondsPerDeg;
                lastRate = rate;
                if (double.IsPositiveInfinity(endAt) && xb >= EndGasConsumedAt) endAt = theta + quenchDelay;
            }
            // The knock integral ends with the end gas; the loop runs on only to find the peak pressure.
            if (theta >= endAt && (theta > 30.0 || p < peak)) break;
        }
        return (integral, peak);

        double Rate(double pressure)
        {
            // End gas: isentropic with the cylinder pressure from the charge state, plus hot-wall heating.
            double lnP = Math.Log(pressure / portPressure);
            double tEnd = chargeTemperature * Math.Exp((n - 1.0) / n * lnP) + wallHeating;
            double lnTau = Math.Log(DelayCoefficient) + lnOctane
                           + PressureExponent * Math.Log(pressure / PhysicalConstants.StandardPressure) + ActivationTemperature / tEnd;
            return Math.Exp(-lnTau);
        }
    }

    /// <summary>
    /// Mixture temperature when compression starts: the fresh charge mixed with the hot residual gas the
    /// clearance volume keeps at exhaust pressure, x_r = 1/(1 + (CR − 1)·(p_in/p_ex)·(T_res/T_charge))
    /// (≈ 4 % and +20 K at full throttle; more at part throttle, less when boost blows the chamber through).
    /// </summary>
    public static double CompressionStartTemperature(in CombustionModel.KnockConditions c)
    {
        double ratio = Math.Max(0.1, c.ExhaustToIntakePressureRatio);
        double residual = 1.0 / (1.0 + (c.CompressionRatio - 1.0) / ratio * ResidualGasTemperature / c.ChargeTemperatureK);
        return (1.0 - residual) * c.ChargeTemperatureK + residual * ResidualGasTemperature;
    }

    /// <summary>
    /// Knock-limited spark advance, degrees BTDC: where the knock integral reaches 1. Found from the integral
    /// at <paramref name="advanceDeg"/> and 4° less (ln I is nearly linear in advance), so whether the engine
    /// knocks is decided exactly at the timing it runs, and the degrees past the limit are accurate near it.
    /// Returns +∞ when the end gas cannot autoignite at all (light load, no charge).
    /// </summary>
    public static double KnockLimitedAdvance(in CombustionModel.KnockConditions c, double advanceDeg)
    {
        const double probe = 4.0;
        double i1 = KnockIntegral(c, advanceDeg);
        double i0 = KnockIntegral(c, advanceDeg - probe);
        if (!(i1 > 0) || !(i0 > 0)) return double.PositiveInfinity;
        double slope = (Math.Log(i1) - Math.Log(i0)) / probe;
        if (!(slope > 1e-6)) return i1 >= 1.0 ? advanceDeg : double.PositiveInfinity;
        return advanceDeg - Math.Log(i1) / slope;
    }

    private static readonly double ReferenceDensity = PhysicalConstants.StandardPressure / (PhysicalConstants.AirGasConstant * 298.15);

    /// <summary>Cylinder volume over the clearance volume at crank angle θ (degrees after TDC).</summary>
    private static double Volume(double thetaDeg, double cr)
    {
        double t = thetaDeg * Math.PI / 180.0;
        double s = Math.Sin(t);
        double x = 1.0 - Math.Cos(t) + (1.0 - Math.Sqrt(1.0 - CrankRodRatio * CrankRodRatio * s * s)) / CrankRodRatio;
        return 1.0 + 0.5 * (cr - 1.0) * x;
    }
}
