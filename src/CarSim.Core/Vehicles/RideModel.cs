using CarSim.Core.Common;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Vehicles;

/// <summary>
/// Mechanical grip on a rough road: a linear quarter-car per axle (sprung mass on spring and damper,
/// unsprung mass on the tyre's vertical stiffness) driven by an ISO 8608 road profile. The chassis model
/// is planar, so rather than integrating wheel hop in time, the tyre-load fluctuation is computed once
/// per set-up from the frequency response and scaled at run time: for the ISO displacement spectrum
/// G(n) = G₀·(n/n₀)⁻², road vertical velocity is white noise of intensity (2π)²·G₀·n₀²·v, so every
/// variance is exactly proportional to speed and to the surface's roughness G₀.
///
/// The consequences are the set-up trade-offs: stiff springs and big anti-roll bars pass bumps into the
/// tyre load (anti-roll bars stiffen the roll mode against uncorrelated left/right bumps); too little
/// damping lets the wheel hop, too much makes the ride harsh (an interior optimum); heavy wheels,
/// tyres and brakes (unsprung mass) cost grip; lowering eats bump travel, and a suspension that runs
/// onto its bump stops — soft springs, low ride height, heavy load transfer — fluctuates far more.
/// Grip falls with the fluctuation as <c>1/(1 + β·(σ_Fz/Fz)²)</c>.
/// </summary>
public sealed class RideModel
{
    /// <summary>ISO 8608 reference spatial frequency, cycles/m.</summary>
    public const double ReferenceWaveNumber = 0.1;

    /// <summary>
    /// Surface roughness G₀ = G_d(n₀), m³: circuit asphalt (smooth end of ISO class A), painted
    /// kerbs (ISO class C, rumble strips), grass run-off (class D).
    /// </summary>
    public static double Roughness(Surface s) => s switch
    {
        Surface.Asphalt => 4e-6,
        Surface.Kerb => 256e-6,
        _ => 1024e-6,
    };

    /// <summary>
    /// Grip lost per unit of (σ_Fz/Fz)²: the concave force-vs-load curve (load sensitivity) plus the
    /// tyre's lag in rebuilding force after a load dip. Empirical.
    /// </summary>
    public const double GripVariancePenalty = 0.6;

    /// <summary>Rate the rubber bump stop adds to the wheel rate once the suspension reaches it, N/m.</summary>
    public const double BumpStopRate = 300_000;

    /// <summary>Hub, upright, bearing and half the links per corner, kg (wheels, tyres, brakes come from the parts).</summary>
    public const double HubMassKg = 12.0;

    /// <summary>Share of the suspension part's mass (springs, dampers, bars) that moves with the wheels.</summary>
    public const double SuspensionUnsprungShare = 0.5;

    /// <summary>
    /// Tyre radial stiffness per unit of (hot gauge pressure × tread width), 1/m: the air spring carries the
    /// load, so the stiffness grows with both (≈ 200 N/mm for a 205 mm tyre at 220 kPa).
    /// </summary>
    public const double TyreStiffnessCoefficient = 4.4;

    /// <summary>Floor on the suspension-deflection spread that decides bottoming, m (static load transfer alone).</summary>
    public const double MinDeflectionSpread = 0.002;

    private readonly Axle[] _axles = new Axle[2];

    public RideModel(VehicleConfiguration c, double unsprungTyresFront, double unsprungTyresRear, double brakesMass, double suspensionMass, double bumpTravelM)
    {
        var s = c.Suspension;
        double weight = c.Mass * PhysicalConstants.Gravity;
        double axleLoadFront = weight * c.CgToRear / c.Wheelbase, axleLoadRear = weight * c.CgToFront / c.Wheelbase;
        double sharedUnsprung = HubMassKg + brakesMass / 4.0 + SuspensionUnsprungShare * suspensionMass / 4.0;
        _axles[0] = new Axle(axleLoadFront / 2, unsprungTyresFront + sharedUnsprung, s.FrontSpringNMm * 1000, s.FrontDamperNsM,
            AntiRollBarWheelRate(s.FrontArbNmDeg, c.TrackFront), TyreStiffness(c.TiresFront),
            bumpTravelM + Units.MmToM(s.RideHeightOffsetMm));
        _axles[1] = new Axle(axleLoadRear / 2, unsprungTyresRear + sharedUnsprung, s.RearSpringNMm * 1000, s.RearDamperNsM,
            AntiRollBarWheelRate(s.RearArbNmDeg, c.TrackRear), TyreStiffness(c.TiresRear),
            bumpTravelM + Units.MmToM(s.RideHeightOffsetMm));
    }

    /// <summary>Unsprung mass per corner, kg (0 = front, 1 = rear).</summary>
    public double UnsprungMass(int axle) => _axles[axle].UnsprungMass;

    /// <summary>Bump travel left at static ride height, m.</summary>
    public double BumpTravel(int axle) => _axles[axle].BumpTravel;

    /// <summary>Tyre radial stiffness of this axle, N/m.</summary>
    public double TyreRate(int axle) => _axles[axle].TyreRate;

    /// <summary>Wheel rate an anti-roll bar adds in pure roll (one wheel up, the other down), N/m.</summary>
    public static double AntiRollBarWheelRate(double arbNmDeg, double track) => 2.0 * arbNmDeg * 180.0 / Math.PI / (track * track);

    /// <summary>Radial stiffness of the tyre at its hot operating pressure, N/m.</summary>
    public static double TyreStiffness(TireSpec t) =>
        TyreStiffnessCoefficient * Units.KpaToPa(TireModel.OperatingPressureKpa(t)) * Units.MmToM(t.WidthMm);

    /// <summary>
    /// Standard deviation of the tyre's vertical load, N, at <paramref name="speed"/> on <paramref name="surface"/>,
    /// with <paramref name="bottomingProbability"/> the share of the time the suspension sits on its bump stop.
    /// </summary>
    public double TyreLoadSigma(int axle, double speed, Surface surface, double bottomingProbability)
    {
        var a = _axles[axle];
        double scale = InputIntensity(speed, surface);
        double p = Math.Clamp(bottomingProbability, 0.0, 1.0);
        return Math.Sqrt(scale * ((1 - p) * a.ForceVariance + p * a.BottomedForceVariance));
    }

    /// <summary>Standard deviation of suspension travel from road roughness, m.</summary>
    public double DeflectionSigma(int axle, double speed, Surface surface) =>
        Math.Sqrt(InputIntensity(speed, surface) * _axles[axle].DeflectionVariance);

    /// <summary>
    /// Share of the time a corner compressed <paramref name="compression"/> m by load transfer spends on its
    /// bump stop, given the roughness-induced spread of its travel (Gaussian tail beyond the travel).
    /// </summary>
    public double BottomingProbability(int axle, double compression, double speed, Surface surface)
    {
        double spread = Math.Sqrt(Math.Pow(DeflectionSigma(axle, speed, surface), 2) + MinDeflectionSpread * MinDeflectionSpread);
        return MathUtil.NormalCdf((compression - _axles[axle].BumpTravel) / spread);
    }

    /// <summary>Grip multiplier for a wheel carrying <paramref name="fz"/> whose load fluctuates by <paramref name="sigma"/>.</summary>
    public static double GripFactor(double fz, double sigma)
    {
        if (fz <= 0) return 0.0;
        double r = sigma / fz;
        return 1.0 / (1.0 + GripVariancePenalty * r * r);
    }

    /// <summary>One-sided road-velocity PSD per unit of transfer-function variance, (m/s)²/Hz.</summary>
    private static double InputIntensity(double speed, Surface surface) =>
        4.0 * Math.PI * Math.PI * Roughness(surface) * ReferenceWaveNumber * ReferenceWaveNumber * Math.Abs(speed);

    private sealed class Axle
    {
        public Axle(double cornerLoad, double unsprungMass, double springRate, double damping, double arbWheelRate, double tyreRate, double bumpTravel)
        {
            UnsprungMass = unsprungMass;
            TyreRate = tyreRate;
            BumpTravel = bumpTravel;
            double sprungMass = Math.Max(20.0, cornerLoad / PhysicalConstants.Gravity - unsprungMass);
            // Uncorrelated left/right bumps are half heave (anti-roll bar idle) and half roll (bar fully wound).
            var heave = Integrate(sprungMass, unsprungMass, springRate, damping, tyreRate);
            var roll = Integrate(sprungMass, unsprungMass, springRate + arbWheelRate, damping, tyreRate);
            ForceVariance = 0.5 * (heave.Force + roll.Force);
            DeflectionVariance = 0.5 * (heave.Deflection + roll.Deflection);
            var heaveStop = Integrate(sprungMass, unsprungMass, springRate + BumpStopRate, damping, tyreRate);
            var rollStop = Integrate(sprungMass, unsprungMass, springRate + arbWheelRate + BumpStopRate, damping, tyreRate);
            BottomedForceVariance = 0.5 * (heaveStop.Force + rollStop.Force);
        }

        public double UnsprungMass { get; }
        public double TyreRate { get; }
        public double BumpTravel { get; }

        /// <summary>Tyre-load and suspension-travel variances per unit of road-velocity PSD (N²·Hz/(m/s)², m²·Hz/(m/s)²).</summary>
        public double ForceVariance { get; }
        public double BottomedForceVariance { get; }
        public double DeflectionVariance { get; }
    }

    /// <summary>
    /// ∫|H(2πf)|² df over 0.05–200 Hz (log grid, trapezoid) for the quarter car with road velocity input:
    /// A = m_s s² + c s + k, B = c s + k, D = m_u s² + c s + k + k_t; z_u/z_r = k_t·A/(A·D − B²);
    /// F_t = k_t(z_u − z_r); deflection = z_u·m_s s²/A; velocity input divides by s.
    /// </summary>
    public static (double Force, double Deflection) Integrate(double ms, double mu, double k, double c, double kt)
    {
        const int n = 600;
        double fLo = 0.05, fHi = 200.0, ratio = Math.Pow(fHi / fLo, 1.0 / n);
        double force = 0, deflection = 0, prevF = 0, prevD = 0, prevFreq = 0;
        for (int i = 0; i <= n; i++)
        {
            double f = fLo * Math.Pow(ratio, i);
            double w = 2 * Math.PI * f;
            var s = new System.Numerics.Complex(0, w);
            var a = ms * s * s + c * s + k;
            var b = c * s + k;
            var d = mu * s * s + c * s + k + kt;
            var zu = kt * a / (a * d - b * b);
            double hF = (kt * (zu - 1.0) / s).Magnitude;
            double hD = (zu * ms * s * s / a / s).Magnitude;
            double fF = hF * hF, fD = hD * hD;
            if (i > 0)
            {
                force += 0.5 * (fF + prevF) * (f - prevFreq);
                deflection += 0.5 * (fD + prevD) * (f - prevFreq);
            }
            prevF = fF;
            prevD = fD;
            prevFreq = f;
        }
        return (force, deflection);
    }
}
