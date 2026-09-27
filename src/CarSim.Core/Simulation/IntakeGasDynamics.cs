using CarSim.Core.Common;

namespace CarSim.Core.Simulation;

/// <summary>
/// Intake gas dynamics (Intake Gas Dynamics 2.0, Phase 1; SIMULATION_SPEC.md, "Intake gas dynamics"; locked in
/// docs/milestones/INTAKE_GAS_DYNAMICS_2_PHASE1_PROPOSAL.md, section 2). The runner of the active stage and the cylinder
/// behind the open valve form one acoustic mode, excited once per cycle. Its tuned speed comes from the runner's geometry,
/// the cylinder volume and the speed of sound of the runner gas — never from cam timing. Its response is a resonance
/// curve in engine speed, and its amplitude grows with the runner's Mach number. The gain multiplies the valve-event
/// filling: <c>VE_dyn = η_ve(N, cams) · G_wave(N, T_man, stage) + scavenging</c>, before valve float.
/// Closed form: per bank and step one square root (the speed of sound) and one rational function; the mode's
/// fundamental is solved once per stage when the engine is built.
/// </summary>
public static class IntakeGasDynamics
{
    // ---- Pre-registered parameters (proposal, section 3). Never fitted to an engine; a miss is reported, not retuned. ----

    /// <summary>
    /// K, the ratio of the runner–cylinder natural frequency to the speed it tunes (Engelman's design rule, secondary
    /// sources 2.0–2.1): an <b>empirical, shared</b> placement parameter, band 2.0–2.2 (U1).
    /// </summary>
    public const double TunedFrequencyRatio = 2.1;

    /// <summary>
    /// ζ, the damping of the resonance curve: the floor set by the finite intake event (0.33–0.35), band 0.33–0.45 (U3).
    /// </summary>
    public const double Damping = 0.35;

    /// <summary>κ, relative wave amplitude per unit runner Mach number: a shared engineering estimate, band 0.2–1.0 (U3; unsourced).</summary>
    public const double AmplitudePerMach = 0.5;

    /// <summary>A_max, the bound on the wave amplitude (assumption A3): η_ve · G stays ≤ <see cref="AirPath.VeCeiling"/> × 1.15.</summary>
    public const double MaxAmplitude = 0.15;

    /// <summary>δ, the end correction of a runner mouth in a plenum wall (flanged pipe, Norris &amp; Sheng 1989), in runner radii.</summary>
    public const double EndCorrection = 0.8216;

    /// <summary>
    /// β_ref: the runner-to-cylinder volume ratio at which the distributed fundamental is normalised to Engelman's lumped
    /// (Helmholtz) form, so that K keeps the meaning it has in the literature (assumption A7).
    /// </summary>
    public const double ReferenceVolumeRatio = 1.0;

    /// <summary>Temperature at which each stage's tuned speed is precomputed, K; per step it scales with √(T_man / T_ref) exactly.</summary>
    public const double ReferenceTemperature = PhysicalConstants.StandardTemperature;

    /// <summary>c₁ = c(β_ref) = x(β_ref)/√β_ref, the fundamental at the reference volume ratio (0.8603 at β_ref = 1).</summary>
    public static readonly double ReferenceFundamental = Fundamental(ReferenceVolumeRatio) / Math.Sqrt(ReferenceVolumeRatio);

    /// <summary>Speed of sound of air at <see cref="ReferenceTemperature"/>, m/s.</summary>
    public static readonly double ReferenceSpeedOfSound = SpeedOfSound(ReferenceTemperature);

    /// <summary>r_p, where the quadrature curve R(r) peaks (just below 1; 0.9389 at ζ = 0.35).</summary>
    public static readonly double ResponsePeakRatio = QuadraturePeak(Damping);

    private static readonly double ResponsePeak = Quadrature(ResponsePeakRatio, Damping);

    /// <summary>Speed of sound of the runner gas, a = √(γ·R·T), m/s.</summary>
    public static double SpeedOfSound(double temperature) =>
        Math.Sqrt(PhysicalConstants.AirGamma * PhysicalConstants.AirGasConstant * temperature);

    /// <summary>
    /// The runner–cylinder fundamental x ∈ (0, π/2), the root of x·tan x = β: a uniform runner with a pressure node at the
    /// plenum and the cylinder's compliance at the valve. x → √β (Helmholtz) as β → 0 and x → π/2 (quarter wave) as β → ∞.
    /// Solved by bisection when a stage is built, never per step.
    /// </summary>
    public static double Fundamental(double beta)
    {
        if (!(beta > 0) || double.IsInfinity(beta)) throw new ArgumentOutOfRangeException(nameof(beta), beta, "The runner-to-cylinder volume ratio must be positive and finite.");
        double lo = 0.0, hi = Math.PI / 2;
        for (int i = 0; i < 200 && hi - lo > 1e-15; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid * Math.Tan(mid) < beta) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>
    /// Tuned engine speed of a runner stage, rpm: N_t = 60·f₁/(K·c₁), f₁ = x·a/(2π·L_eff). Equal to Engelman's
    /// 60·f_Helmholtz/K at β = β_ref, and corrected for the distributed runner elsewhere.
    /// </summary>
    public static double TunedRpm(double fundamental, double effectiveLength, double speedOfSound) =>
        TunedRpm(fundamental, effectiveLength, speedOfSound, TunedFrequencyRatio);

    /// <summary><see cref="TunedRpm(double, double, double)"/> with an explicit K (sensitivity studies and tools; the simulation uses the constant).</summary>
    public static double TunedRpm(double fundamental, double effectiveLength, double speedOfSound, double tunedFrequencyRatio) =>
        60.0 * fundamental * speedOfSound / (2.0 * Math.PI * effectiveLength) / (tunedFrequencyRatio * ReferenceFundamental);

    /// <summary>
    /// The normalised response R̃(r) = R(r·r_p)/R(r_p) of the quadrature curve R(r) = 4ζ²r/((1 − r²)² + 4ζ²r²), the
    /// forced oscillator's response in phase with the flow: exactly 1 at the tuned speed (r = 1), positive everywhere
    /// above zero speed, falling off on both sides.
    /// </summary>
    public static double Response(double speedRatio) => Quadrature(speedRatio * ResponsePeakRatio, Damping) / ResponsePeak;

    /// <summary><see cref="Response(double)"/> with an explicit ζ (sensitivity studies and tools; the simulation uses the constant).</summary>
    public static double Response(double speedRatio, double damping)
    {
        double peak = QuadraturePeak(damping);
        return Quadrature(speedRatio * peak, damping) / Quadrature(peak, damping);
    }

    /// <summary>Wave amplitude A = min(A_max, κ·M_r) at runner Mach number <paramref name="runnerMach"/>.</summary>
    public static double Amplitude(double runnerMach) => Math.Min(MaxAmplitude, AmplitudePerMach * runnerMach);

    /// <summary>
    /// Mean runner Mach number during the intake event, M_r = ū_r/a with ū_r = V_d,cyl·(N/120)/(A_r·f_event): the
    /// cylinder's charge through one runner in the fraction of the cycle the valve is open.
    /// </summary>
    public static double RunnerMach(double rpm, double sweptVolumePerCylinder, double runnerArea, double intakeEventFraction, double speedOfSound) =>
        sweptVolumePerCylinder * (rpm / 120.0) / (runnerArea * intakeEventFraction) / speedOfSound;

    /// <summary>
    /// Wave gain G_wave = 1 + A·R̃(N/N_t), in [1, 1 + A_max] by construction (A ∈ [0, A_max], R̃ ∈ [0, 1]). No cam timing
    /// enters: a phaser moves the valve event against the wave, not the wave.
    /// </summary>
    public static double Gain(double rpm, double tunedRpm, double runnerMach) =>
        1.0 + Amplitude(runnerMach) * Response(rpm / tunedRpm);

    /// <summary>The quadrature (absorption) resonance curve R(r) = 4ζ²r/((1 − r²)² + 4ζ²r²).</summary>
    public static double Quadrature(double r, double damping)
    {
        double c = 4.0 * damping * damping, d = 1.0 - r * r;
        return c * r / (d * d + c * r * r);
    }

    /// <summary>
    /// Where R(r) peaks: dR/dr = 0 ⇔ (1 − u)(1 + 3u) = c·u, u = r², c = 4ζ², so u = ((2 − c) + √((2 − c)² + 12))/6.
    /// </summary>
    public static double QuadraturePeak(double damping)
    {
        double c = 4.0 * damping * damping;
        return Math.Sqrt(((2.0 - c) + Math.Sqrt((2.0 - c) * (2.0 - c) + 12.0)) / 6.0);
    }
}
