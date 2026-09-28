namespace CarSim.Core.Content;

/// <summary>
/// Authored ECU calibration as stored in content/save files. Tables are rows over the load axis
/// (manifold absolute pressure, kPa) and columns over the RPM axis. A record, so the loader, the save migration and
/// tools copy it with <c>with</c> instead of field by field (a new field can no longer be dropped by one copy).
/// </summary>
public sealed record TuneDocument
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

    /// <summary>
    /// Intake cam advance the ECU commands from the phaser's park position, crank degrees [load row][rpm column].
    /// Optional: without it (or without a phaser and an ECU that can drive one) the cams stay where they are installed.
    /// </summary>
    public double[][]? IntakeCamAdvanceDeg { get; set; }

    public required double RevLimitRpm { get; init; }

    /// <summary>Injector flow the ECU believes is installed (injector scaling), cc/min per injector.</summary>
    public required double InjectorFlowCcMin { get; init; }

    /// <summary>Stoichiometric AFR the ECU believes the fuel has (fuel calibration).</summary>
    public required double FuelStoichAfr { get; init; }

    /// <summary>
    /// Injector dead time the ECU adds to every pulse, ms (injector calibration). Wrong by a fraction of a
    /// millisecond, it barely matters at full load and badly at idle, where pulses are short. Required in content;
    /// saves from before version 4 are migrated to the installed injectors' value.
    /// </summary>
    public double? InjectorDeadTimeMs { get; set; }

    /// <summary>
    /// Fuel density the ECU converts fuel mass to injector volume with, kg/L (fuel calibration). Required in
    /// content; saves from before version 4 are migrated to their fuel's density.
    /// </summary>
    public double? FuelDensityKgL { get; set; }
    public double IdleRpm { get; init; } = 850;
    public bool KnockControlEnabled { get; init; } = true;

    /// <summary>
    /// Engine speed above which the ECU switches variable-valve-lift camshafts to their high-lift profile, rpm (back
    /// below it less <see cref="Ecu.EcuController.SwitchHysteresisRpm"/>). Null = never switch.
    /// </summary>
    public double? ValveLiftSwitchRpm { get; set; }

    /// <summary>Engine speed above which the ECU switches a variable intake manifold to its switched runner, rpm. Null = never.</summary>
    public double? IntakeRunnerSwitchRpm { get; set; }

    /// <summary>
    /// For an intake with more than two runner stages: the speeds above which the ECU switches on to stage 2, 3, …, rpm,
    /// ascending and above <see cref="IntakeRunnerSwitchRpm"/> (stage 1). Null or shorter than the intake's stages: it
    /// switches no further.
    /// </summary>
    public double[]? IntakeRunnerUpperSwitchRpm { get; set; }

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
        if (IntakeCamAdvanceDeg != null) CheckTable(p, "intake_cam_advance_deg", IntakeCamAdvanceDeg, 0, 80);
        if (!(RevLimitRpm >= 1000 && RevLimitRpm <= 25000)) p.Add($"rev_limit_rpm out of range: {RevLimitRpm}");
        if (!(IdleRpm >= 300 && IdleRpm < RevLimitRpm)) p.Add($"idle_rpm out of range: {IdleRpm}");
        if (ValveLiftSwitchRpm is double vl && !(vl >= 500 && vl <= 25000)) p.Add($"valve_lift_switch_rpm out of range: {vl}");
        if (IntakeRunnerSwitchRpm is double ir && !(ir >= 500 && ir <= 25000)) p.Add($"intake_runner_switch_rpm out of range: {ir}");
        if (IntakeRunnerUpperSwitchRpm is { Length: > 0 } upper)
        {
            if (IntakeRunnerSwitchRpm is not double first) p.Add("intake_runner_upper_switch_rpm needs intake_runner_switch_rpm (the switch to stage 1).");
            else if (upper.Prepend(first).Zip(upper).Any(v => !(v.Second > v.First))) p.Add("intake_runner_upper_switch_rpm must rise above intake_runner_switch_rpm, stage by stage.");
            if (upper.Any(v => !(v >= 500 && v <= 25000))) p.Add("intake_runner_upper_switch_rpm values must be within [500, 25000] rpm.");
        }
        if (!(InjectorFlowCcMin >= 50 && InjectorFlowCcMin <= 5000)) p.Add($"injector_flow_cc_min out of range: {InjectorFlowCcMin}");
        if (!(FuelStoichAfr >= 3 && FuelStoichAfr <= 20)) p.Add($"fuel_stoich_afr out of range: {FuelStoichAfr}");
        if (InjectorDeadTimeMs is not double dead) p.Add("injector_dead_time_ms missing (the injector dead time the ECU adds to each pulse).");
        else if (!(dead >= 0 && dead <= 3)) p.Add($"injector_dead_time_ms out of range: {dead}");
        if (FuelDensityKgL is not double density) p.Add("fuel_density_kg_l missing (the fuel density the ECU meters with).");
        else if (!(density >= 0.5 && density <= 1.2)) p.Add($"fuel_density_kg_l out of range: {density}");
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
