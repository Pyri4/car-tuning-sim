using CarSim.Core.Common;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;

namespace CarSim.Core.Simulation;

/// <summary>
/// Mean-value engine model. Each <see cref="Step"/> solves the air path and fueling quasi-statically
/// at the current speed, computes combustion and friction, then integrates the slow states
/// (speed when free, temperatures, knock control). Deterministic: identical inputs give identical
/// outputs. See SIMULATION_SPEC.md for the equations.
/// </summary>
public sealed class EngineSimulation
{
    public const double FiringMinRpm = 150.0;
    public const double StallRpm = 250.0;
    public const double RunningRpm = 400.0;
    public const double StarterTorqueNm = 60.0;
    public const double StarterFreeRpm = 300.0;
    public const double IdleBypassAreaFraction = 0.02;
    public const double ThrottleLeakAreaFraction = 0.0008;

    /// <summary>Oil-path leakage conductance of the K20-sized reference engine at 100 °C, m³/(s·Pa).</summary>
    public const double ReferenceOilConductance = 1.157e-9;
    public const double ReferenceBearingClearance = 0.040e-3;
    public const double OilPumpVolumetricEfficiency = 0.9;

    private readonly AirPath _airPath;
    private readonly double _referenceDensity =
        PhysicalConstants.StandardPressure / (PhysicalConstants.AirGasConstant * PhysicalConstants.StandardTemperature);

    public EngineSimulation(EngineConfiguration config, EcuTune tune, EngineState? state = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Ecu = new EcuController(config.Ecu, tune ?? throw new ArgumentNullException(nameof(tune)));
        State = state ?? new EngineState();
        _airPath = new AirPath(config);
    }

    public EngineConfiguration Config { get; }
    public EcuController Ecu { get; }
    public EngineState State { get; }
    public EngineTelemetry? Last { get; private set; }

    public double Rpm => Units.RadPerSecToRpm(State.Omega);

    /// <summary>Butterfly throttle: effective area fraction vs pedal (progressive, 1 − cos).</summary>
    public static double ThrottleAreaFraction(double throttle) =>
        1.0 - Math.Cos(MathUtil.Clamp01(throttle) * Math.PI / 2.0);

    /// <summary>Valve-float speed of the installed valvetrain with current spring wear.</summary>
    public double ValveFloatRpm() =>
        ValvetrainModel.FloatRpm(Config.Cams, Config.Springs, Config.Head, Config.Part(PartCategory.ValveSprings).Wear);

    /// <summary>Oil pressure (Pa) from pump delivery over the leakage conductance, capped by the relief valve.</summary>
    public double OilPressure(double rpm, double oilTemperature, double supplyFactor)
    {
        var c = Config;
        double q = c.OilPump.DisplacementPerRev * rpm / 60.0 * OilPumpVolumetricEfficiency * supplyFactor;
        double mu = OilViscosity.At(oilTemperature);
        double mainC = c.MainBearings.Clearance * (1.0 + c.Part(PartCategory.MainBearings).Wear);
        double rodC = c.RodBearings.Clearance * (1.0 + c.Part(PartCategory.RodBearings).Wear);
        double clearanceTerm = 0.5 * (Math.Pow(mainC / ReferenceBearingClearance, 3) + Math.Pow(rodC / ReferenceBearingClearance, 3));
        double sizeScale = c.Geometry.Displacement / 0.002;
        double conductance = ReferenceOilConductance * sizeScale * (OilViscosity.Reference / mu) * (0.6 * clearanceTerm + 0.4);
        double p = q / conductance;
        double relief = c.OilPump.ReliefPressure;
        if (p > relief) p = relief + 0.05 * (p - relief);
        return p;
    }

    private readonly record struct TurboStep(double TurbinePower, double CompressorPower, double BoostTarget);

    /// <summary>
    /// Wastegate control and shaft-energy integration: dE/dt = P_turbine − P_compressor − P_friction,
    /// E = ½·I·ω². The wastegate actuator opens proportionally once compressor-outlet boost exceeds its
    /// spring; with ECU boost control the solenoid can hold it shut longer (never open it earlier).
    /// </summary>
    private TurboStep UpdateTurbo(double dt, double rpm, in AirPathResult air, double ambientPressure)
    {
        var c = Config;
        var s = State;
        var t = c.Turbo;
        if (t == null) return default;

        const double actuatorSpan = 20_000.0;
        double spring = t.WastegateSpring;
        double boostGauge = air.CompressorOutletPressure - ambientPressure;
        double mechanical = MathUtil.Clamp01((boostGauge - spring) / actuatorSpan);
        double target = spring;
        double command = mechanical;
        double? ecuTarget = c.Ecu.BoostControl ? Ecu.Tune.BoostTargetKpaAt(rpm) : null;
        if (ecuTarget is double absKpa)
        {
            target = Math.Max(spring, Units.KpaToPa(absKpa) - ambientPressure);
            double error = (boostGauge - target) / actuatorSpan;
            s.BoostControlIntegral = MathUtil.Clamp(s.BoostControlIntegral + 3.0 * error * dt, -1.0, 1.0);
            command = Math.Min(mechanical, MathUtil.Clamp01(error + s.BoostControlIntegral));
        }
        s.WastegateOpening += (command - s.WastegateOpening) * MathUtil.LagFactor(dt, 0.08);

        var (turbinePower, _) = TurbochargerModel.Turbine(t, s.TurboOmega, air.TurbineMassFlow,
            air.TurbineInletPressure, air.TurbineOutletPressure, s.ExhaustGasTemperature);
        double compressorPower = air.Compressor.Power;
        double friction = TurbochargerModel.FrictionPower(t, s.TurboOmega);
        double energy = 0.5 * t.RotorInertia * s.TurboOmega * s.TurboOmega;
        energy = Math.Max(0.0, energy + (turbinePower - compressorPower - friction) * dt);
        s.TurboOmega = Math.Min(Math.Sqrt(2.0 * energy / t.RotorInertia), 2.0 * t.MaxShaftSpeed);

        // Exhaust temperature after the turbine, mixed with the wastegate bypass flow.
        if (air.ExhaustMassFlow > 0)
        {
            double tTurbineOut = s.ExhaustGasTemperature - (air.TurbineMassFlow > 0 ? turbinePower / (air.TurbineMassFlow * PhysicalConstants.ExhaustCp) : 0.0);
            double bypass = air.ExhaustMassFlow - air.TurbineMassFlow;
            s.TurbineOutletTemperature = (tTurbineOut * air.TurbineMassFlow + s.ExhaustGasTemperature * bypass) / air.ExhaustMassFlow;
        }
        else s.TurbineOutletTemperature = s.ExhaustGasTemperature;
        return new TurboStep(turbinePower, compressorPower, target);
    }

    /// <summary>Minimum oil pressure the bearings need at <paramref name="rpm"/>, Pa.</summary>
    public static double RequiredOilPressure(double rpm) => 30_000.0 + 25_000.0 * rpm / 1000.0;

    public EngineTelemetry Step(double dt, in EngineInputs input)
    {
        if (!(dt > 0)) throw new ArgumentOutOfRangeException(nameof(dt));
        var c = Config;
        var s = State;
        var g = c.Geometry;
        var fuel = c.Fuel;

        if (input.SpeedMode == SpeedMode.Held)
            s.Omega = Units.RpmToRadPerSec(Math.Max(0.0, input.HeldRpm));
        double rpm = Units.RadPerSecToRpm(s.Omega);
        double omega = s.Omega;

        // ---- ECU pre-step: rev limiter and idle air ----
        bool revCut = Ecu.UpdateRevLimiter(rpm);
        double idleValve = Ecu.UpdateIdle(rpm, input.Throttle, s.Running, dt);
        double throttleArea = c.ThrottleCdA * (ThrottleAreaFraction(input.Throttle)
                               + IdleBypassAreaFraction * idleValve + ThrottleLeakAreaFraction);

        // ---- Air path ----
        double floatRpm = ValveFloatRpm();
        double evapCooling = 5.0 * fuel.ChargeCoolingFactor * s.LastFuelAirRatio * fuel.StoichiometricAfr;
        var air = _airPath.Solve(new AirPathConditions(rpm, throttleArea, input.AmbientPressure, input.AmbientTemperature,
            s.ExhaustGasTemperature, s.CoolantTemperature, s.LastFuelAirRatio, evapCooling, floatRpm,
            s.TurboOmega, s.WastegateOpening, input.CoolingAirSpeed, s.TurbineOutletTemperature));

        // ---- ECU fuel and spark ----
        double mapReading = Ecu.ReadMap(air.ManifoldPressure);
        bool mapSaturated = air.ManifoldPressure > c.Ecu.MapSensorMax + 500.0;
        double targetLambda = Ecu.TargetLambda(rpm, mapReading);
        double advance = Ecu.SparkAdvance(rpm, mapReading);

        bool wantsToFire = input.Ignition && rpm >= FiringMinRpm && (s.Running || input.Starter || rpm >= RunningRpm);
        double cycleTime = rpm > 1 ? 120.0 / rpm : 0.0;
        FuelDelivery delivery = default;
        if (wantsToFire && !revCut && air.AirPerCycle > 0)
        {
            double estAir = Ecu.EstimatedAirPerCycle(air.AirPerCycle, air.ManifoldPressure);
            double fuelCmd = Ecu.CommandedFuelPerCycle(estAir, targetLambda);
            double pw = Ecu.PulseWidth(fuelCmd, fuel.Density);
            delivery = FuelSystem.Deliver(c.Injectors, c.Part(PartCategory.Injectors).Wear, c.FuelPump,
                c.Part(PartCategory.FuelPump).Wear, fuel.Density, pw, cycleTime, air.ManifoldPressure, input.AmbientPressure);
        }
        double fuelPerCycle = delivery.FuelPerCycle;
        bool firing = wantsToFire && !revCut && fuelPerCycle > 0 && air.AirPerCycle > 0;

        // ---- Combustion ----
        double lambda = firing ? air.AirPerCycle / (fuelPerCycle * fuel.StoichiometricAfr) : double.PositiveInfinity;
        double chargeDensityRatio = air.PortPressure / (PhysicalConstants.AirGasConstant * air.ChargeTemperature) / _referenceDensity;
        double mbt = CombustionModel.MbtAdvance(rpm, chargeDensityRatio);
        double ringWear = c.Part(PartCategory.Pistons).Wear;
        double cr = g.CompressionRatio;
        double knockLimit = CombustionModel.KnockLimitedAdvance(new CombustionModel.KnockConditions(
            rpm, fuel.OctaneRon, cr, air.PortPressure, air.ChargeTemperature, s.CoolantTemperature,
            double.IsFinite(lambda) ? lambda : 1.0, Units.MToMm(g.DeckClearance)));
        double knock = firing ? CombustionModel.KnockIntensity(advance, knockLimit) : 0.0;

        double burnedFuel = 0.0, grossWork = 0.0;
        if (firing)
        {
            burnedFuel = Math.Min(fuelPerCycle, air.AirPerCycle / fuel.StoichiometricAfr);
            double eta = CombustionModel.IndicatedEfficiency(cr) * (1.0 - 0.10 * ringWear);
            grossWork = burnedFuel * fuel.LowerHeatingValue * eta
                        * CombustionModel.MixtureFactor.Evaluate(lambda)
                        * CombustionModel.SparkFactor(advance, mbt)
                        * CombustionModel.KnockTorqueFactor(knock);
        }
        double imep = grossWork / g.SweptVolumePerCylinder;
        double pmep = air.ExhaustPortPressure - air.PortPressure;
        double pcp = firing
            ? CombustionModel.PeakCylinderPressure(air.PortPressure, cr, imep, advance, mbt, knock)
            : air.PortPressure * Math.Pow(cr, PhysicalConstants.CompressionPolytropicExponent);
        double pistonSpeed = g.MeanPistonSpeed(rpm);
        double fmep = rpm > 1 ? CombustionModel.FrictionMep(pistonSpeed, OilViscosity.At(s.OilTemperature), c.Springs.OpenForceN, pcp) : 0.0;
        double bmep = imep - pmep - fmep;
        double torque = rpm > 1 ? bmep * g.Displacement / (4.0 * Math.PI) : 0.0;
        double power = torque * omega;

        // ---- Heat flows ----
        double cyclesPerSecond = rpm / 120.0 * g.Cylinders;
        double fuelPower = burnedFuel * fuel.LowerHeatingValue * cyclesPerSecond;
        double indicatedPower = grossWork * cyclesPerSecond;
        double frictionPower = Math.Max(0.0, fmep * g.Displacement / (4.0 * Math.PI) * omega);
        double coolantFraction = CombustionModel.CoolantHeatFraction(rpm, knock);
        double heatToCoolant = fuelPower * coolantFraction + 0.5 * frictionPower;
        double heatToOil = fuelPower * CombustionModel.OilHeatFraction + 0.5 * frictionPower;
        double exhaustFlow = air.MassFlow + (firing ? fuelPerCycle * cyclesPerSecond : 0.0);
        double exhaustEnergy = Math.Max(0.0, fuelPower - indicatedPower - fuelPower * (coolantFraction + CombustionModel.OilHeatFraction));
        double egtTarget = air.ChargeTemperature + 50.0;
        if (firing && exhaustFlow > 0)
        {
            egtTarget = air.ChargeTemperature + exhaustEnergy / (exhaustFlow * PhysicalConstants.ExhaustCp)
                        - 300.0 * Math.Max(0.0, 1.0 - lambda);
        }

        // Piston crown temperature (quasi-steady target, lagged).
        double heatFlux = fuelPower / (g.Cylinders * g.PistonArea);
        double crownTarget = s.CoolantTemperature
            + 170.0 * Math.Pow(Math.Max(0.0, heatFlux) / 14.2e6, 0.7) * (1.0 + 1.5 * Math.Max(0.0, (double.IsFinite(lambda) ? lambda : 1.0) - 0.9))
            + 8.0 * knock;

        // ---- Mechanical loads ----
        double inertiaLoad = g.RodInertiaLoad(omega);
        double gasLoad = pcp * g.PistonArea;
        double compressive = Math.Max(0.0, gasLoad - inertiaLoad);
        double bearingLoad = Math.Max(compressive, inertiaLoad);

        // ---- Oil ----
        double supply = input.SumpAccelerationG > c.OilPan.MaxSustainedG
            ? MathUtil.Clamp01(1.0 - (input.SumpAccelerationG - c.OilPan.MaxSustainedG) / 0.3)
            : 1.0;
        double oilPressure = OilPressure(rpm, s.OilTemperature, supply);

        // ---- Thermal integration ----
        double coolingAir = Math.Max(0.0, input.CoolingAirSpeed);
        double airFactor = MathUtil.Clamp(Math.Pow(coolingAir / c.Radiator.ReferenceAirSpeedMs, 0.6), 0.1, 2.5);
        double thermostat = MathUtil.SmoothStep(c.Radiator.ThermostatOpen, c.Radiator.ThermostatOpen + 10.0, s.CoolantTemperature);
        double radiatorHeat = c.Radiator.HeatRejectionWPerK * airFactor * thermostat * (s.CoolantTemperature - input.AmbientTemperature);
        double surfaceLoss = 15.0 * (s.CoolantTemperature - input.AmbientTemperature);
        double oilToCoolant = 120.0 * (s.OilTemperature - s.CoolantTemperature);
        double sumpLoss = 12.0 * (1.0 + 0.1 * coolingAir) * (s.OilTemperature - input.AmbientTemperature);
        s.CoolantTemperature += (heatToCoolant - radiatorHeat - surfaceLoss + oilToCoolant) / c.CoolantHeatCapacity * dt;
        s.OilTemperature += (heatToOil - oilToCoolant - sumpLoss) / c.OilHeatCapacity * dt;
        if (input.CoolantTemperatureOverride is double heldCoolant) s.CoolantTemperature = heldCoolant;
        s.ExhaustGasTemperature += (egtTarget - s.ExhaustGasTemperature) * MathUtil.LagFactor(dt, 0.2);
        s.EgtSensor += (s.ExhaustGasTemperature - s.EgtSensor) * MathUtil.LagFactor(dt, 1.5);
        s.PistonCrownTemperature += (crownTarget - s.PistonCrownTemperature) * MathUtil.LagFactor(dt, 3.0);
        s.LastFuelAirRatio = firing ? fuelPerCycle / air.AirPerCycle : 0.0;

        // ---- Turbocharger ----
        var turbo = UpdateTurbo(dt, rpm, air, input.AmbientPressure);

        // ---- ECU post-step ----
        Ecu.UpdateKnockControl(knock, dt);

        // ---- Speed ----
        if (input.SpeedMode == SpeedMode.Free)
        {
            double starter = input.Starter ? StarterTorqueNm * Math.Max(0.0, 1.0 - rpm / StarterFreeRpm) : 0.0;
            double inertia = c.RotatingInertia + Math.Max(0.0, input.LoadInertia);
            s.Omega = Math.Max(0.0, s.Omega + (torque + starter - input.LoadTorque) / inertia * dt);
        }
        double newRpm = Units.RadPerSecToRpm(s.Omega);
        if (!input.Ignition || newRpm < StallRpm && !input.Starter) s.Running = false;
        else if (firing && newRpm >= RunningRpm) s.Running = true;
        s.Time += dt;

        var t = new EngineTelemetry
        {
            Time = s.Time,
            Rpm = rpm,
            Throttle = input.Throttle,
            Running = s.Running,
            Firing = firing,
            Torque = torque,
            Power = power,
            ManifoldPressure = air.ManifoldPressure,
            BoostPressure = air.ManifoldPressure - input.AmbientPressure,
            PortPressure = air.PortPressure,
            ExhaustBackPressure = air.ExhaustManifoldPressure - input.AmbientPressure,
            ChargeTemperature = air.ChargeTemperature,
            AirMassFlow = air.MassFlow,
            AirPerCycle = air.AirPerCycle,
            VolumetricEfficiency = air.AirPerCycle / (input.AmbientPressure / (PhysicalConstants.AirGasConstant * input.AmbientTemperature) * g.SweptVolumePerCylinder),
            VeDynamic = air.VeDynamic,
            ResidualFactor = air.ResidualFactor,
            TargetLambda = targetLambda,
            Lambda = firing ? lambda : 0.0,
            Afr = firing ? lambda * fuel.StoichiometricAfr : 0.0,
            FuelMassFlow = firing ? fuelPerCycle * cyclesPerSecond : 0.0,
            InjectorDuty = delivery.Duty,
            FuelRailPressure = delivery.RailPressure,
            FuelLimit = delivery.Limit,
            EcuMapReading = mapReading,
            MapSensorSaturated = mapSaturated,
            IgnitionAdvance = advance,
            MbtAdvance = mbt,
            KnockLimitAdvance = knockLimit,
            KnockIntensity = knock,
            KnockRetard = Ecu.KnockRetard,
            Imep = imep,
            Pmep = pmep,
            Fmep = fmep,
            Bmep = bmep,
            PeakCylinderPressure = pcp,
            CoolantTemperature = s.CoolantTemperature,
            OilTemperature = s.OilTemperature,
            OilPressure = oilPressure,
            OilPressureRequired = RequiredOilPressure(rpm),
            ExhaustGasTemperature = s.EgtSensor,
            PistonCrownTemperature = s.PistonCrownTemperature,
            HeatToCoolant = heatToCoolant,
            RadiatorHeatRejection = radiatorHeat,
            TurboRpm = Units.RadPerSecToRpm(s.TurboOmega),
            CompressorPressureRatio = c.Turbo != null ? air.Compressor.PressureRatio : 1.0,
            CompressorEfficiency = air.Compressor.Efficiency,
            CompressorCorrectedFlow = air.Compressor.CorrectedFlow,
            CompressorChokeRatio = air.Compressor.ChokeRatio,
            CompressorSurge = air.Compressor.Surge,
            CompressorOutletTemperature = air.CompressorOutletTemperature,
            CompressorOutletPressure = air.CompressorOutletPressure,
            CompressorPower = turbo.CompressorPower,
            TurbinePower = turbo.TurbinePower,
            TurbineInletPressure = air.TurbineInletPressure,
            TurbineInletTemperature = s.ExhaustGasTemperature,
            WastegateOpening = s.WastegateOpening,
            BoostTarget = turbo.BoostTarget,
            TurboOverspeed = c.Turbo != null && s.TurboOmega > c.Turbo.MaxShaftSpeed,
            RodTensileLoad = inertiaLoad,
            RodCompressiveLoad = compressive,
            BearingLoad = bearingLoad,
            ValveFloatRpm = floatRpm,
            ValveFloat = rpm > floatRpm,
            RevLimiterActive = revCut,
        };
        Last = t;
        return t;
    }
}
