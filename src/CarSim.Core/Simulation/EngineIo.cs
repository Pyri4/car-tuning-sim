using CarSim.Core.Common;

namespace CarSim.Core.Simulation;

/// <summary>How engine speed is determined during a step.</summary>
public enum SpeedMode
{
    /// <summary>Speed is integrated from net torque and inertia (free revving, vehicle clutch open).</summary>
    Free,
    /// <summary>Speed is imposed from outside (dyno brake, drivetrain); the engine reports torque at that speed.</summary>
    Held,
}

/// <summary>External inputs for one simulation step.</summary>
public struct EngineInputs
{
    public EngineInputs() { }

    /// <summary>Pedal / throttle request 0–1.</summary>
    public double Throttle = 0.0;

    public bool Ignition = true;
    public bool Starter = false;

    public SpeedMode SpeedMode = SpeedMode.Free;

    /// <summary>Imposed speed when <see cref="SpeedMode"/> is Held, rpm.</summary>
    public double HeldRpm = 0.0;

    /// <summary>Load torque opposing the crank when Free, N·m.</summary>
    public double LoadTorque = 0.0;

    /// <summary>Extra inertia coupled to the crank when Free, kg·m².</summary>
    public double LoadInertia = 0.0;

    public double AmbientPressure = PhysicalConstants.StandardPressure;
    public double AmbientTemperature = PhysicalConstants.StandardTemperature;

    /// <summary>Air speed through the radiator core (vehicle speed / fan), m/s.</summary>
    public double CoolingAirSpeed = 10.0;

    /// <summary>Sustained horizontal acceleration acting on the oil in the sump, g.</summary>
    public double SumpAccelerationG = 0.0;

    /// <summary>
    /// When set, coolant is held at this temperature (K) by an external heat exchanger, as in an
    /// engine-dyno test cell. The radiator is then irrelevant.
    /// </summary>
    public double? CoolantTemperatureOverride = null;
}

/// <summary>Integrated engine state carried between steps.</summary>
public sealed class EngineState
{
    public double Time;
    public double Omega;
    public double CoolantTemperature = PhysicalConstants.StandardTemperature;
    public double OilTemperature = PhysicalConstants.StandardTemperature;
    public double ExhaustGasTemperature = PhysicalConstants.StandardTemperature;
    public double EgtSensor = PhysicalConstants.StandardTemperature;
    public double PistonCrownTemperature = PhysicalConstants.StandardTemperature;
    public double LastFuelAirRatio = 1.0 / 14.7;
    public bool Running;

    /// <summary>Starts the state warm (fully warmed-up engine), as after a warm-up drive.</summary>
    public static EngineState Warm(double coolantK = 363.15, double oilK = 368.15) => new()
    {
        CoolantTemperature = coolantK,
        OilTemperature = oilK,
        ExhaustGasTemperature = 700.0,
        EgtSensor = 700.0,
        PistonCrownTemperature = coolantK + 30.0,
    };

    public EngineState Clone() => (EngineState)MemberwiseClone();
}

/// <summary>Why the delivered fuel fell short of the ECU's request (if it did).</summary>
public enum FuelLimit
{
    None,
    /// <summary>Injectors are at 100 % duty (static).</summary>
    InjectorCapacity,
    /// <summary>Fuel pump cannot hold regulated pressure; injector flow drops.</summary>
    PumpCapacity,
}

/// <summary>Per-step outputs. SI units unless the name says otherwise.</summary>
public sealed record EngineTelemetry
{
    public double Time { get; init; }
    public double Rpm { get; init; }
    public double Throttle { get; init; }
    public bool Running { get; init; }
    public bool Firing { get; init; }

    public double Torque { get; init; }
    public double Power { get; init; }

    public double ManifoldPressure { get; init; }
    public double BoostPressure { get; init; }
    public double PortPressure { get; init; }
    public double ExhaustBackPressure { get; init; }
    public double ChargeTemperature { get; init; }
    public double AirMassFlow { get; init; }
    public double AirPerCycle { get; init; }

    /// <summary>Delivered air / (displacement × ambient density × cycles): the classic dyno VE figure.</summary>
    public double VolumetricEfficiency { get; init; }

    /// <summary>Tuning (cam/runner/header) VE component, relative to port conditions.</summary>
    public double VeDynamic { get; init; }

    /// <summary>Residual-gas / reversion multiplier from exhaust backpressure (1 = none).</summary>
    public double ResidualFactor { get; init; }

    public double TargetLambda { get; init; }
    public double Lambda { get; init; }
    public double Afr { get; init; }
    public double FuelMassFlow { get; init; }
    public double InjectorDuty { get; init; }
    public double FuelRailPressure { get; init; }
    public FuelLimit FuelLimit { get; init; }
    public double EcuMapReading { get; init; }
    public bool MapSensorSaturated { get; init; }

    public double IgnitionAdvance { get; init; }
    public double MbtAdvance { get; init; }
    public double KnockLimitAdvance { get; init; }
    public double KnockIntensity { get; init; }
    public double KnockRetard { get; init; }

    public double Imep { get; init; }
    public double Pmep { get; init; }
    public double Fmep { get; init; }
    public double Bmep { get; init; }
    public double PeakCylinderPressure { get; init; }

    public double CoolantTemperature { get; init; }
    public double OilTemperature { get; init; }
    public double OilPressure { get; init; }
    public double OilPressureRequired { get; init; }
    public double ExhaustGasTemperature { get; init; }
    public double PistonCrownTemperature { get; init; }
    public double HeatToCoolant { get; init; }
    public double RadiatorHeatRejection { get; init; }

    public double RodTensileLoad { get; init; }
    public double RodCompressiveLoad { get; init; }
    public double BearingLoad { get; init; }
    public double ValveFloatRpm { get; init; }
    public bool ValveFloat { get; init; }
    public bool RevLimiterActive { get; init; }

    public double TorqueLbFt => Units.NmToLbFt(Torque);
    public double PowerHp => Units.WToHp(Power);
    public double PowerKw => Units.WToKw(Power);
    public double BoostKpa => Units.PaToKpa(BoostPressure);
    public double MapKpa => Units.PaToKpa(ManifoldPressure);
    public double CoolantC => Units.KToC(CoolantTemperature);
    public double OilC => Units.KToC(OilTemperature);
    public double EgtC => Units.KToC(ExhaustGasTemperature);
    public double OilPressureBar => Units.PaToBar(OilPressure);
    public double PeakCylinderPressureBar => Units.PaToBar(PeakCylinderPressure);
}
