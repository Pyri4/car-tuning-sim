using System.Text;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Simulation;

namespace CarSim.Core.Damage;

public sealed record ReportLine(string Label, string Value);

/// <summary>
/// Explanation of a failure: what broke, why (measured against ratings), what contributed, and what to
/// do about it. The player should be able to learn the mechanical cause from this alone.
/// </summary>
public sealed record FailureReport(
    double Time,
    FailureMode Mode,
    FailureSeverity Severity,
    string Title,
    string Cause,
    string Slot,
    string PartName,
    IReadOnlyList<ReportLine> Measurements,
    IReadOnlyList<string> ContributingFactors,
    IReadOnlyList<string> Recommendations,
    IReadOnlyList<string> CollateralDamage)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Severity == FailureSeverity.Catastrophic ? $"ENGINE FAILURE — {Title}" : $"COMPONENT FAILURE — {Title}");
        sb.AppendLine($"Component: {Slot} ({PartName})");
        sb.AppendLine($"Cause: {Cause}");
        sb.AppendLine();
        sb.AppendLine("Measured at failure:");
        foreach (var m in Measurements) sb.AppendLine($"  {m.Label}: {m.Value}");
        if (ContributingFactors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Contributing factors:");
            foreach (var f in ContributingFactors) sb.AppendLine($"  - {f}");
        }
        if (Recommendations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Recommendations:");
            foreach (var r in Recommendations) sb.AppendLine($"  - {r}");
        }
        if (CollateralDamage.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Collateral damage:");
            foreach (var c in CollateralDamage) sb.AppendLine($"  - {c}");
        }
        return sb.ToString();
    }
}

public sealed record FailureContext(
    EngineConfiguration Config,
    PartInstance Part,
    EngineSlotDefinition Slot,
    FailureMode Mode,
    EngineTelemetry T,
    IReadOnlyList<StressReading> Readings,
    OperatingHistory History,
    double RevLimitRpm,
    IReadOnlyList<string> Collateral);
