using System.Text.Json.Serialization;
using CarSim.Core.Common;

namespace CarSim.Core.Parts.Specs;

/// <summary>Positive-displacement oil pump with a pressure relief valve.</summary>
public sealed class OilPumpSpec : PartSpec
{
    /// <summary>Oil delivered per crank revolution.</summary>
    public required double DisplacementCcPerRev { get; init; }

    public required double ReliefPressureKpa { get; init; }

    [JsonIgnore] public double DisplacementPerRev => Units.CcToM3(DisplacementCcPerRev);
    [JsonIgnore] public double ReliefPressure => Units.KpaToPa(ReliefPressureKpa);

    protected override void Validate(SpecChecker check)
    {
        check.Range("displacement_cc_per_rev", DisplacementCcPerRev, 1, 100);
        check.Range("relief_pressure_kpa", ReliefPressureKpa, 150, 1500);
    }
}

public sealed class OilPanSpec : PartSpec
{
    public required double CapacityL { get; init; }

    /// <summary>Sustained lateral/longitudinal acceleration before the pickup uncovers.</summary>
    public required double MaxSustainedG { get; init; }

    public bool Baffled { get; init; }

    protected override void Validate(SpecChecker check)
    {
        check.Range("capacity_l", CapacityL, 1, 20);
        check.Range("max_sustained_g", MaxSustainedG, 0.3, 5.0);
    }
}

public sealed class RadiatorSpec : PartSpec
{
    /// <summary>
    /// Heat rejection per kelvin of coolant-to-ambient difference at the reference airflow
    /// (UA value), W/K.
    /// </summary>
    public required double HeatRejectionWPerK { get; init; }

    /// <summary>Air speed through the core at which <see cref="HeatRejectionWPerK"/> is rated, m/s.</summary>
    public double ReferenceAirSpeedMs { get; init; } = 15.0;

    public double CoolantCapacityL { get; init; } = 2.5;

    /// <summary>Thermostat opening temperature.</summary>
    public double ThermostatOpenC { get; init; } = 88.0;

    [JsonIgnore] public double ThermostatOpen => Units.CToK(ThermostatOpenC);

    protected override void Validate(SpecChecker check)
    {
        check.Range("heat_rejection_w_per_k", HeatRejectionWPerK, 50, 20000);
        check.Range("reference_air_speed_ms", ReferenceAirSpeedMs, 1, 80);
        check.Positive("coolant_capacity_l", CoolantCapacityL);
        check.Range("thermostat_open_c", ThermostatOpenC, 50, 110);
    }
}

/// <summary>Engine control unit hardware capabilities (the calibration itself is a tune).</summary>
public sealed class EcuSpec : PartSpec
{
    /// <summary>Highest absolute manifold pressure the MAP sensor can read. Above it the ECU is blind.</summary>
    public required double MapSensorMaxKpa { get; init; }

    public required bool BoostControl { get; init; }
    public required bool KnockControl { get; init; }
    public required double MaxRevLimitRpm { get; init; }

    /// <summary>Whether fuel/ignition tables can be edited by the player.</summary>
    public bool TablesEditable { get; init; } = true;

    /// <summary>
    /// Whether the ECU can drive cam phasers (from its tune's intake cam-advance table). Without it a phased cam
    /// stays at its park position.
    /// </summary>
    public bool CamPhaseControl { get; init; }

    /// <summary>Whether the ECU can switch a variable-valve-lift camshaft to its high-lift profile (tune: valve_lift_switch_rpm).</summary>
    public bool ValveLiftControl { get; init; }

    /// <summary>Whether the ECU can switch a variable intake manifold's runner (tune: intake_runner_switch_rpm).</summary>
    public bool IntakeRunnerControl { get; init; }

    [JsonIgnore] public double MapSensorMax => Units.KpaToPa(MapSensorMaxKpa);

    protected override void Validate(SpecChecker check)
    {
        check.Range("map_sensor_max_kpa", MapSensorMaxKpa, 90, 1000);
        check.Range("max_rev_limit_rpm", MaxRevLimitRpm, 1000, 25000);
    }
}
