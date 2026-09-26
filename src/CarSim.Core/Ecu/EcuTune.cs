using CarSim.Core.Common;
using CarSim.Core.Content;

namespace CarSim.Core.Ecu;

/// <summary>
/// A live, editable ECU calibration. Table axes: X = engine speed (rpm), Y = manifold absolute
/// pressure as read by the ECU's MAP sensor (kPa). The ECU looks tables up with the pressure it can
/// <em>measure</em>, which is capped by its sensor range.
/// </summary>
public sealed class EcuTune
{
    public EcuTune(string id, string name, Table2D targetLambda, Table2D ignitionAdvance, Table2D volumetricEfficiency, Table2D? boostTarget,
        double revLimitRpm, double idleRpm, bool knockControlEnabled, double injectorFlowCcMin, double fuelStoichAfr, double displacementCc,
        double injectorDeadTimeMs, double fuelDensityKgL)
    {
        InjectorDeadTimeMs = injectorDeadTimeMs;
        FuelDensityKgL = fuelDensityKgL;
        Id = id;
        Name = name;
        TargetLambda = targetLambda;
        IgnitionAdvance = ignitionAdvance;
        VolumetricEfficiency = volumetricEfficiency;
        DisplacementCc = displacementCc;
        BoostTarget = boostTarget;
        RevLimitRpm = revLimitRpm;
        IdleRpm = idleRpm;
        KnockControlEnabled = knockControlEnabled;
        InjectorFlowCcMin = injectorFlowCcMin;
        FuelStoichAfr = fuelStoichAfr;
    }

    public string Id { get; set; }
    public string Name { get; set; }

    /// <summary>Target λ [MAP kPa row][rpm column].</summary>
    public Table2D TargetLambda { get; }

    /// <summary>Spark advance, degrees BTDC [MAP kPa row][rpm column].</summary>
    public Table2D IgnitionAdvance { get; }

    /// <summary>
    /// Speed-density fuel map: volumetric efficiency relative to the MAP and intake air temperature the
    /// ECU measures [MAP kPa row][rpm column].
    /// </summary>
    public Table2D VolumetricEfficiency { get; }

    /// <summary>Engine displacement the ECU is set up for, cc.</summary>
    public double DisplacementCc { get; set; }

    /// <summary>Boost target (absolute kPa) vs rpm, single row. Null when the calibration has none.</summary>
    public Table2D? BoostTarget { get; }

    public double RevLimitRpm { get; set; }
    public double IdleRpm { get; set; }
    public bool KnockControlEnabled { get; set; }

    /// <summary>Injector scaling: flow the ECU assumes each injector delivers, cc/min at rated pressure.</summary>
    public double InjectorFlowCcMin { get; set; }

    /// <summary>Fuel calibration: the stoichiometric AFR the ECU assumes.</summary>
    public double FuelStoichAfr { get; set; }

    /// <summary>Injector calibration: the dead time the ECU adds to each pulse, ms.</summary>
    public double InjectorDeadTimeMs { get; set; }

    /// <summary>Fuel calibration: the fuel density the ECU meters with, kg/L.</summary>
    public double FuelDensityKgL { get; set; }

    public double LambdaAt(double rpm, double mapKpa) => TargetLambda.Evaluate(rpm, mapKpa);
    public double AdvanceAt(double rpm, double mapKpa) => IgnitionAdvance.Evaluate(rpm, mapKpa);
    public double VolumetricEfficiencyAt(double rpm, double mapKpa) => VolumetricEfficiency.Evaluate(rpm, mapKpa);
    public double? BoostTargetKpaAt(double rpm) => BoostTarget?.Evaluate(rpm, 0.0);

    /// <summary>Highest boost target in the table (absolute kPa), or null without a boost table.</summary>
    public double? MaxBoostTargetKpa => BoostTarget == null ? null : Enumerable.Range(0, BoostTarget.Columns).Max(c => BoostTarget[0, c]);

    public static EcuTune FromDocument(TuneDocument d)
    {
        var lambda = new Table2D(d.RpmAxis, d.LoadAxisKpa, d.TargetLambda);
        var ign = new Table2D(d.RpmAxis, d.LoadAxisKpa, d.IgnitionAdvanceDeg);
        var ve = new Table2D(d.RpmAxis, d.LoadAxisKpa, d.VolumetricEfficiency
            ?? throw new InvalidDataException($"Tune '{d.Id}' has no volumetric_efficiency table."));
        double displacement = d.DisplacementCc ?? throw new InvalidDataException($"Tune '{d.Id}' has no displacement_cc.");
        Table2D? boost = d.BoostTargetKpa == null ? null : new Table2D(d.RpmAxis, new[] { 0.0 }, new[] { d.BoostTargetKpa });
        double deadTime = d.InjectorDeadTimeMs ?? throw new InvalidDataException($"Tune '{d.Id}' has no injector_dead_time_ms.");
        double density = d.FuelDensityKgL ?? throw new InvalidDataException($"Tune '{d.Id}' has no fuel_density_kg_l.");
        return new EcuTune(d.Id, d.Name, lambda, ign, ve, boost, d.RevLimitRpm, d.IdleRpm, d.KnockControlEnabled,
            d.InjectorFlowCcMin, d.FuelStoichAfr, displacement, deadTime, density);
    }

    public TuneDocument ToDocument() => new()
    {
        Id = Id,
        Name = Name,
        RpmAxis = TargetLambda.XAxis.ToArray(),
        LoadAxisKpa = TargetLambda.YAxis.ToArray(),
        TargetLambda = TargetLambda.ToRows(),
        IgnitionAdvanceDeg = IgnitionAdvance.ToRows(),
        VolumetricEfficiency = VolumetricEfficiency.ToRows(),
        DisplacementCc = DisplacementCc,
        BoostTargetKpa = BoostTarget?.ToRows()[0],
        RevLimitRpm = RevLimitRpm,
        IdleRpm = IdleRpm,
        KnockControlEnabled = KnockControlEnabled,
        InjectorFlowCcMin = InjectorFlowCcMin,
        FuelStoichAfr = FuelStoichAfr,
        InjectorDeadTimeMs = InjectorDeadTimeMs,
        FuelDensityKgL = FuelDensityKgL,
    };

    public EcuTune Clone() => FromDocument(ToDocument());

    /// <summary>Adds <paramref name="degrees"/> to every ignition cell at or above <paramref name="minMapKpa"/>.</summary>
    public void OffsetIgnition(double degrees, double minMapKpa = 0)
    {
        for (int r = 0; r < IgnitionAdvance.Rows; r++)
        {
            if (IgnitionAdvance.YAxis[r] < minMapKpa) continue;
            for (int c = 0; c < IgnitionAdvance.Columns; c++) IgnitionAdvance[r, c] += degrees;
        }
    }

    /// <summary>Sets every λ cell at or above <paramref name="minMapKpa"/> to <paramref name="lambda"/>.</summary>
    public void SetLambda(double lambda, double minMapKpa = 0)
    {
        for (int r = 0; r < TargetLambda.Rows; r++)
        {
            if (TargetLambda.YAxis[r] < minMapKpa) continue;
            for (int c = 0; c < TargetLambda.Columns; c++) TargetLambda[r, c] = lambda;
        }
    }
}
