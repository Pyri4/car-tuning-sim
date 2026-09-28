using CarSim.Core.Common;

namespace CarSim.Core.Simulation;

/// <summary>
/// Isentropic nozzle (orifice) flow used for every air-path restriction:
/// ṁ = CdA · p_up / √(R·T_up) · Ψ(p_down / p_up), choked below the critical pressure ratio.
/// For small pressure drops this reduces to the incompressible Δp = ṁ² / (2ρ·CdA²).
/// </summary>
public static class CompressibleFlow
{
    public static double CriticalPressureRatio(double gamma) => Math.Pow(2.0 / (gamma + 1.0), gamma / (gamma - 1.0));

    /// <summary>Dimensionless flow function Ψ for pressure ratio pr = p_down/p_up (clamped to the choked value).</summary>
    public static double FlowFunction(double pr, double gamma)
    {
        double crit = CriticalPressureRatio(gamma);
        if (pr >= 1.0) return 0.0;
        if (pr < crit) pr = crit;
        double a = Math.Pow(pr, 2.0 / gamma);
        double b = Math.Pow(pr, (gamma + 1.0) / gamma);
        return Math.Sqrt(2.0 * gamma / (gamma - 1.0) * Math.Max(0.0, a - b));
    }

    public static double MassFlow(double cdA, double pUp, double tUp, double pDown, double gamma, double gasConstant)
    {
        if (cdA <= 0 || pUp <= 0 || pDown >= pUp) return 0.0;
        return cdA * pUp / Math.Sqrt(gasConstant * tUp) * FlowFunction(pDown / pUp, gamma);
    }

    /// <summary>Largest mass flow the restriction can pass from p_up (choked flow).</summary>
    public static double ChokedMassFlow(double cdA, double pUp, double tUp, double gamma, double gasConstant) =>
        MassFlow(cdA, pUp, tUp, pUp * CriticalPressureRatio(gamma) * 0.999999, gamma, gasConstant);

    /// <summary>
    /// Downstream pressure that passes <paramref name="massFlow"/> from p_up. If the flow exceeds the
    /// choked capacity the result keeps falling below the critical ratio (∝ (ṁ_choke/ṁ)²), which keeps
    /// callers' root-finding monotonic; <paramref name="choked"/> reports that case.
    /// </summary>
    public static double DownstreamPressure(double cdA, double pUp, double tUp, double massFlow, double gamma, double gasConstant, out bool choked)
    {
        choked = false;
        if (massFlow <= 0) return pUp;
        if (cdA <= 0) { choked = true; return 0.0; }
        double crit = CriticalPressureRatio(gamma);
        double chokeFlow = ChokedMassFlow(cdA, pUp, tUp, gamma, gasConstant);
        if (massFlow >= chokeFlow)
        {
            choked = true;
            double r = chokeFlow / massFlow;
            return pUp * crit * r * r;
        }
        // Ψ is monotonic decreasing in pr on [crit, 1].
        double target = massFlow * Math.Sqrt(gasConstant * tUp) / (cdA * pUp);
        double pr = RootFinder.Brent(new FlowRatioResidual(gamma, target), crit, 1.0, 1e-12);
        return pUp * pr;
    }

    /// <summary>Upstream pressure needed to push <paramref name="massFlow"/> into p_down.</summary>
    public static double UpstreamPressure(double cdA, double pDown, double tUp, double massFlow, double gamma, double gasConstant)
    {
        if (massFlow <= 0) return pDown;
        if (cdA <= 0) return double.PositiveInfinity;
        // Bracket from the incompressible estimate Δp ≈ ṁ² / (2ρ·CdA²).
        double rho = pDown / (gasConstant * tUp);
        double dp0 = massFlow * massFlow / (2.0 * rho * cdA * cdA);
        double hi = pDown + 2.0 * dp0 + 1.0;
        int guard = 0;
        while (MassFlow(cdA, hi, tUp, pDown, gamma, gasConstant) < massFlow && guard++ < 60) hi = pDown + (hi - pDown) * 2.0;
        return RootFinder.Brent(new UpstreamFlowResidual(cdA, tUp, pDown, gamma, gasConstant, massFlow), pDown, hi, 1e-6);
    }

    /// <summary>
    /// Effective flow area (CdA, m²) of a part rated at <paramref name="cfm"/> on a flow bench
    /// (28 inH₂O depression, standard air).
    /// </summary>
    public static double EffectiveAreaFromCfm(double cfm)
    {
        double q = Units.CfmToM3PerSec(cfm);
        return q / Math.Sqrt(2.0 * PhysicalConstants.FlowBenchPressureDrop / PhysicalConstants.FlowBenchAirDensity);
    }

    public static double CfmFromEffectiveArea(double cdA) =>
        cdA * Math.Sqrt(2.0 * PhysicalConstants.FlowBenchPressureDrop / PhysicalConstants.FlowBenchAirDensity) / Units.CfmToCubicMetresPerSecond;

    /// <summary>Ψ(pr) − target, whose root is the pressure ratio of <see cref="DownstreamPressure"/>.</summary>
    private readonly struct FlowRatioResidual(double gamma, double target) : IRootFunction
    {
        // Not inlined into the solver: measured on .NET 8 with dynamic PGO (the default), inlining Ψ's three Math.Pow
        // calls into Brent's loop made the whole engine step ≈ 65 % slower (57 → 97 µs on the K20; equal with PGO off).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public double Evaluate(double x) => FlowFunction(x, gamma) - target;
    }

    /// <summary>ṁ(p_up) − ṁ, whose root is the upstream pressure of <see cref="UpstreamPressure"/>.</summary>
    private readonly struct UpstreamFlowResidual(double cdA, double tUp, double pDown, double gamma, double gasConstant, double massFlow) : IRootFunction
    {
        public double Evaluate(double p) => MassFlow(cdA, p, tUp, pDown, gamma, gasConstant) - massFlow;
    }
}
