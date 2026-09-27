using CarSim.Core.Engines;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;

namespace CarSim.Verification.Calibration;

/// <summary>The result of <see cref="RunnerStageDerivation.LowerStage"/>.</summary>
/// <param name="UpperTunedRpm">Tuned speed of the known (upper, shorter) stage, rpm.</param>
/// <param name="LowerTunedRpm">Tuned speed the lower stage needs so the two cross at the switch speed, rpm.</param>
/// <param name="LowerLengthMm">Acoustic length that gives the lower stage that tuned speed at the same diameter, mm (null: none in the validator's range).</param>
/// <param name="CrossoverRpm">The crossover of the two gain curves with the derived length, rpm (the target, to the length's precision).</param>
public sealed record StageDerivation(double UpperTunedRpm, double LowerTunedRpm, double? LowerLengthMm, double CrossoverRpm);

/// <summary>
/// Derives an effective lower runner stage from a sourced switch speed (assumption A-D1: the manufacturer switches at the
/// full-load crossover of the two stages; docs/milestones/INTAKE_GAS_DYNAMICS_2_PHASE1_PROPOSAL.md, section 4). Generic:
/// any two-stage intake whose switch speed is sourced and whose upper stage's geometry is known or estimated.
/// <list type="number">
/// <item>The upper stage's tuned speed N_u from its geometry (the model's formula, runner gas at the reference ambient).</item>
/// <item>Solve R̃(x/N_l) = R̃(x/N_u) for N_l &lt; x at the switch speed x. The amplitude cancels: both stages share the runner
/// area and so the Mach number. Unique: R̃ rises to 1 at r = 1 and falls after it, so the lower stage's ratio x/N_l is the
/// one point above 1 with the upper stage's response.</item>
/// <item>Invert the tuned-speed formula for the lower stage's acoustic length at the same diameter (the tuned speed falls
/// monotonically with length).</item>
/// </list>
/// Arithmetic on the gain functions only: no engine is run, so no torque output can enter the derivation.
/// K and ζ are parameters for sensitivity studies; the defaults are the model's pre-registered values.
/// </summary>
public static class RunnerStageDerivation
{
    public static StageDerivation LowerStage(double upperLengthMm, double? diameterMm, EngineGeometry bankGeometry, double crossoverRpm,
        double tunedFrequencyRatio = IntakeGasDynamics.TunedFrequencyRatio, double damping = IntakeGasDynamics.Damping,
        double temperature = IntakeGasDynamics.ReferenceTemperature)
    {
        double Tuned(double lengthMm)
        {
            var stage = new RunnerStageConfiguration(new IntakeStageSpec { RunnerLengthMm = lengthMm, RunnerDiameterMm = diameterMm }, bankGeometry);
            return IntakeGasDynamics.TunedRpm(stage.Fundamental, stage.EffectiveLength, IntakeGasDynamics.SpeedOfSound(temperature), tunedFrequencyRatio);
        }
        double upper = Tuned(upperLengthMm);
        double target = IntakeGasDynamics.Response(crossoverRpm / upper, damping);
        if (!(crossoverRpm < upper)) throw new ArgumentException($"The switch speed {crossoverRpm:F0} rpm is not below the upper stage's tuned speed {upper:F0} rpm: no lower stage crosses it there.");

        // x/N_l in (1, ∞): R̃ falls from 1 toward 0 there.
        double lo = 1.0, hi = 1.0;
        while (IntakeGasDynamics.Response(hi, damping) > target) hi *= 2;
        for (int i = 0; i < 200 && hi - lo > 1e-15; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (IntakeGasDynamics.Response(mid, damping) > target) lo = mid; else hi = mid;
        }
        double lower = crossoverRpm / (0.5 * (lo + hi));

        double? length = null;
        double lMin = IntakeManifoldSpec.MinRunnerLengthMm, lMax = IntakeManifoldSpec.MaxRunnerLengthMm;
        if (Tuned(lMin) >= lower && Tuned(lMax) <= lower)
        {
            for (int i = 0; i < 200 && lMax - lMin > 1e-12; i++)
            {
                double mid = 0.5 * (lMin + lMax);
                if (Tuned(mid) > lower) lMin = mid; else lMax = mid;
            }
            length = 0.5 * (lMin + lMax);
        }
        return new StageDerivation(upper, lower, length, length is double l ? Crossover(Tuned(l), upper, damping) : double.NaN);
    }

    /// <summary>
    /// Where the gain curves of two stages sharing a runner area cross, rpm: the one speed between their tuned speeds at which
    /// R̃(N/N_lower) = R̃(N/N_upper).
    /// </summary>
    public static double Crossover(double lowerTunedRpm, double upperTunedRpm, double damping = IntakeGasDynamics.Damping)
    {
        double lo = lowerTunedRpm, hi = upperTunedRpm;
        for (int i = 0; i < 200 && hi - lo > 1e-9; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (IntakeGasDynamics.Response(mid / lowerTunedRpm, damping) > IntakeGasDynamics.Response(mid / upperTunedRpm, damping)) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }
}
