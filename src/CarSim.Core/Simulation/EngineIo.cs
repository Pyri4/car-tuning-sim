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

    /// <summary>Fraction of the coolant still in the system (boiling vents it through the cap).</summary>
    public double CoolantLevel = 1.0;

    /// <summary>Turbocharger shaft speed, rad/s.</summary>
    public double TurboOmega;

    /// <summary>Wastegate valve opening 0–1.</summary>
    public double WastegateOpening;

    /// <summary>Integrator of the ECU's closed-loop boost controller.</summary>
    public double BoostControlIntegral;

    /// <summary>Exhaust temperature after the turbine (mixed with wastegate flow), K.</summary>
    public double TurbineOutletTemperature;

    /// <summary>Intake cam phaser position: advance from the park position, crank degrees.</summary>
    public double IntakeCamAdvance;

    /// <summary>Manifold pressure of the previous step, Pa (what the ECU's MAP sensor last reported, before clipping).</summary>
    public double LastManifoldPressure = PhysicalConstants.StandardPressure;

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

    /// <summary>A catastrophic failure has locked the engine.</summary>
    public bool Seized { get; init; }

    public double Torque { get; init; }
    public double Power { get; init; }

    public double ManifoldPressure { get; init; }
    public double BoostPressure { get; init; }
    public double PortPressure { get; init; }
    public double ExhaustBackPressure { get; init; }
    /// <summary>Manifold air temperature (what the ECU's intake-air-temperature sensor reads), K.</summary>
    public double ManifoldTemperature { get; init; }

    /// <summary>Trapped charge temperature (manifold air after heat pick-up and fuel evaporation), K.</summary>
    public double ChargeTemperature { get; init; }
    public double AirMassFlow { get; init; }
    public double AirPerCycle { get; init; }

    /// <summary>Delivered air / (displacement × ambient density × cycles): the classic dyno VE figure.</summary>
    public double VolumetricEfficiency { get; init; }

    /// <summary>Tuning (cam/runner/header) VE component, relative to port conditions.</summary>
    public double VeDynamic { get; init; }

    /// <summary>Residual-gas / reversion multiplier from exhaust backpressure (1 = none).</summary>
    public double ResidualFactor { get; init; }

    /// <summary>Intake cam phaser position, crank degrees advanced from park (0 for fixed cams). A cam-position sensor reads it.</summary>
    public double IntakeCamAdvance { get; init; }

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

    // Model internals (debug and tests only): no player-facing screen or dyno log shows these. Players find best
    // timing from torque and the knock limit from knock onset.
    /// <summary>Best-torque (MBT) spark advance at this operating point, degrees BTDC. Debug only.</summary>
    public double MbtAdvance { get; init; }
    /// <summary>Knock-limited spark advance, degrees BTDC. Debug only.</summary>
    public double KnockLimitAdvance { get; init; }
    /// <summary>Degrees of advance past the knock limit (0 = no knock). Debug only: players see <see cref="KnockSensorLevel"/>.</summary>
    public double KnockIntensity { get; init; }

    /// <summary>What the knock sensor reports (a coarse level, not the degrees past the limit).</summary>
    public Ecu.KnockLevel KnockSensorLevel => Ecu.KnockSensor.Level(KnockIntensity);

    public double KnockRetard { get; init; }

    public double Imep { get; init; }
    public double Pmep { get; init; }
    public double Fmep { get; init; }
    public double Bmep { get; init; }
    public double PeakCylinderPressure { get; init; }

    public double CoolantTemperature { get; init; }
    public double CoolantLevel { get; init; }
    public double OilTemperature { get; init; }
    public double OilPressure { get; init; }
    public double OilPressureRequired { get; init; }
    public double ExhaustGasTemperature { get; init; }
    public double PistonCrownTemperature { get; init; }
    /// <summary>Chemical power released by the fuel that burns, W.</summary>
    public double FuelPower { get; init; }

    /// <summary>Net heat into the coolant, W (negative while motoring: the gas picks heat up from the walls).</summary>
    public double HeatToCoolant { get; init; }

    public double HeatToOil { get; init; }

    /// <summary>
    /// Enthalpy the exhaust gas carries away relative to the charge temperature, W (including the latent
    /// heat absorbed by unburned fuel). FuelPower = Power + HeatToCoolant + HeatToOil + ExhaustHeat.
    /// </summary>
    public double ExhaustHeat { get; init; }

    /// <summary>
    /// Heat the exhaust gas gives to the coolant-jacketed port walls on its way out, W (already included in
    /// <see cref="HeatToCoolant"/>; negative when cold motored gas picks heat up from the walls).
    /// </summary>
    public double PortWallHeat { get; init; }

    public double RadiatorHeatRejection { get; init; }

    // The rest of the thermal network, W (debug: lets a test close the first law over time with stored heat).
    /// <summary>Heat the coolant loses from the block and hoses to the surroundings.</summary>
    public double CoolantSurfaceLoss { get; init; }
    /// <summary>Heat flowing from the oil into the coolant (negative while the oil is cooler).</summary>
    public double OilToCoolantHeat { get; init; }
    /// <summary>Heat the oil loses from the sump to the surroundings.</summary>
    public double OilSumpLoss { get; init; }

    // Forced induction (zero for naturally aspirated builds).
    public double TurboRpm { get; init; }
    public double CompressorPressureRatio { get; init; }
    public double CompressorEfficiency { get; init; }
    public double CompressorCorrectedFlow { get; init; }
    public double CompressorChokeRatio { get; init; }
    public bool CompressorSurge { get; init; }
    public double CompressorOutletTemperature { get; init; }
    public double CompressorOutletPressure { get; init; }
    public double CompressorPower { get; init; }
    public double TurbinePower { get; init; }
    public double TurbineInletPressure { get; init; }
    /// <summary>Exhaust gas temperature leaving the ports (the gas itself, not the lagging sensor), K.</summary>
    public double PortGasTemperature { get; init; }

    /// <summary>Gas temperature at the turbine inlet, after the manifold's heat loss (= port gas temperature without a turbo), K.</summary>
    public double TurbineInletTemperature { get; init; }

    /// <summary>Debug: the wall heat shares were scaled down because indicated work left too little (never in normal running; tested).</summary>
    public bool WallHeatLimited { get; init; }

    /// <summary>Debug: the cam/runner VE shape sits on <see cref="AirPath.VeShapeFloor"/> at this speed.</summary>
    public bool VeFloorActive { get; init; }

    /// <summary>Debug: the intake closes so early that the tuned-speed correlation sits on <see cref="EngineConfiguration.MinTunedPistonSpeed"/>.</summary>
    public bool CamTuningFloorActive { get; init; }

    /// <summary>Turbine blade-speed ratio U/c_s (debug: 0.7 is the efficiency peak; see <see cref="TurbochargerModel.TurbineEfficiencyFloor"/>).</summary>
    public double TurbineBladeSpeedRatio { get; init; }

    /// <summary>How deep into surge the compressor is (0 = not surging, 1 = no through-flow).</summary>
    public double CompressorSurgeDepth { get; init; }
    public double WastegateOpening { get; init; }

    /// <summary>Boost target in force (gauge, Pa): the wastegate spring, or the ECU target if it controls boost.</summary>
    public double BoostTarget { get; init; }
    public bool TurboOverspeed { get; init; }

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
