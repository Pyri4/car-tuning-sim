namespace CarSim.Core.Content;

/// <summary>
/// Authored ECU calibration as stored in content/save files. Tables are rows over the load axis
/// (manifold absolute pressure, kPa) and columns over the RPM axis.
/// </summary>
public sealed class TuneDocument
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";

    public required double[] RpmAxis { get; init; }
    public required double[] LoadAxisKpa { get; init; }

    /// <summary>Target lambda (1.0 = stoichiometric) [load row][rpm column].</summary>
    public required double[][] TargetLambda { get; init; }

    /// <summary>Ignition advance, degrees BTDC [load row][rpm column].</summary>
    public required double[][] IgnitionAdvanceDeg { get; init; }

    /// <summary>
    /// Speed-density fuel map: the volumetric efficiency the ECU assumes, relative to the manifold
    /// pressure and air temperature its sensors read [load row][rpm column]. Its fuel estimate is
    /// VE · MAP · (displacement / cylinders) / (R · IAT), so any engine whose real breathing differs from
    /// this table (cams, head, exhaust, turbo) runs off its target λ until the table is re-tuned.
    /// Required in content; saves from version 1 predate it and are migrated.
    /// </summary>
    public double[][]? VolumetricEfficiency { get; init; }

    /// <summary>Engine displacement the ECU is set up for, cc (a stroker kit needs this or the VE table changed).</summary>
    public double? DisplacementCc { get; init; }

    /// <summary>Boost target (absolute manifold pressure, kPa) per RPM axis point. Used only with ECU boost control.</summary>
    public double[]? BoostTargetKpa { get; init; }

    public required double RevLimitRpm { get; init; }

    /// <summary>Injector flow the ECU believes is installed (injector scaling), cc/min per injector.</summary>
    public required double InjectorFlowCcMin { get; init; }

    /// <summary>Stoichiometric AFR the ECU believes the fuel has (fuel calibration).</summary>
    public required double FuelStoichAfr { get; init; }
    public double IdleRpm { get; init; } = 850;
    public bool KnockControlEnabled { get; init; } = true;

    public string Source { get; init; } = "";

    public IReadOnlyList<string> Validate()
    {
        var p = new List<string>();
        CheckAxis(p, "rpm_axis", RpmAxis);
        CheckAxis(p, "load_axis_kpa", LoadAxisKpa);
        CheckTable(p, "target_lambda", TargetLambda, 0.5, 1.6);
        CheckTable(p, "ignition_advance_deg", IgnitionAdvanceDeg, -20, 60);
        if (VolumetricEfficiency == null) p.Add("volumetric_efficiency missing (the ECU's speed-density fuel map).");
        else CheckTable(p, "volumetric_efficiency", VolumetricEfficiency, 0.05, 3.0);
        if (DisplacementCc is not double cc) p.Add("displacement_cc missing (the engine displacement the ECU is set up for).");
        else if (!(cc >= 50 && cc <= 20000)) p.Add($"displacement_cc out of range: {cc}");
        if (BoostTargetKpa != null)
        {
            if (BoostTargetKpa.Length != RpmAxis.Length) p.Add("boost_target_kpa must have one value per rpm_axis point.");
            if (BoostTargetKpa.Any(v => v < 50 || v > 1000)) p.Add("boost_target_kpa values must be within [50, 1000] kPa absolute.");
        }
        if (!(RevLimitRpm >= 1000 && RevLimitRpm <= 25000)) p.Add($"rev_limit_rpm out of range: {RevLimitRpm}");
        if (!(IdleRpm >= 300 && IdleRpm < RevLimitRpm)) p.Add($"idle_rpm out of range: {IdleRpm}");
        if (!(InjectorFlowCcMin >= 50 && InjectorFlowCcMin <= 5000)) p.Add($"injector_flow_cc_min out of range: {InjectorFlowCcMin}");
        if (!(FuelStoichAfr >= 3 && FuelStoichAfr <= 20)) p.Add($"fuel_stoich_afr out of range: {FuelStoichAfr}");
        return p;
    }

    private static void CheckAxis(List<string> p, string name, double[]? axis)
    {
        if (axis == null || axis.Length < 2) { p.Add($"{name} needs at least 2 points."); return; }
        for (int i = 1; i < axis.Length; i++)
            if (!(axis[i] > axis[i - 1])) { p.Add($"{name} must be strictly increasing."); return; }
    }

    private void CheckTable(List<string> p, string name, double[][]? table, double min, double max)
    {
        if (table == null || LoadAxisKpa == null || RpmAxis == null) { p.Add($"{name} missing."); return; }
        if (table.Length != LoadAxisKpa.Length) { p.Add($"{name} needs {LoadAxisKpa.Length} rows (one per load_axis_kpa point), has {table.Length}."); return; }
        for (int r = 0; r < table.Length; r++)
        {
            if (table[r] == null || table[r].Length != RpmAxis.Length) { p.Add($"{name} row {r} needs {RpmAxis.Length} values."); return; }
            if (table[r].Any(v => !(v >= min && v <= max))) { p.Add($"{name} row {r} has values outside [{min}, {max}]."); return; }
        }
    }
}
