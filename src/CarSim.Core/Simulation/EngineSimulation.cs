using CarSim.Core.Common;
using CarSim.Core.Damage;
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

    /// <summary>Acceleration beyond the oil pan's rating over which oil pressure collapses completely, g.</summary>
    public const double OilSurgeWindowG = 0.15;

    /// <summary>Heat exchange between oil and coolant through the block, head and oil/water cooler, W/K.</summary>
    public const double OilToCoolantConductance = 400.0;

    /// <summary>Boiling point of 50/50 coolant under a ~1.1 bar pressure cap, K (128 °C).</summary>
    public const double CoolantBoilingPoint = 401.15;

    /// <summary>Latent heat of vaporisation used for boiling coolant off, J/kg.</summary>
    public const double CoolantLatentHeat = 2.0e6;

    private readonly AirPath _airPath;
    private readonly double _referenceDensity =
        PhysicalConstants.StandardPressure / (PhysicalConstants.AirGasConstant * PhysicalConstants.StandardTemperature);

    public EngineSimulation(EngineConfiguration config, EcuTune tune, EngineState? state = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Ecu = new EcuController(config.Ecu, tune ?? throw new ArgumentNullException(nameof(tune)));
        State = state ?? new EngineState();
        _airPath = new AirPath(config);
        Damage = new DamageModel(config);
    }

    /// <summary>Fatigue, failures, warnings and operating history for this engine.</summary>
    public DamageModel Damage { get; }

    /// <summary>When false, loads are still evaluated for warnings but no damage accumulates (sandbox/testing).</summary>
    public bool DamageEnabled { get; set; } = true;

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

    private readonly record struct TurboStep(double TurbinePower, double CompressorPower, double BoostTarget, double TurbineInletTemperature);

    /// <summary>Wastegate actuator span: boost above the spring over which the gate goes from shut to fully open, Pa.</summary>
    public const double WastegateActuatorSpan = 20_000.0;

    /// <summary>Integral gain of the ECU boost controller, per second (on the error normalised by the actuator span).</summary>
    public const double BoostControlIntegralGain = 3.0;

    /// <summary>
    /// Wastegate control and shaft-energy integration: dE/dt = P_turbine − P_compressor − P_friction,
    /// E = ½·I·ω². The wastegate actuator opens proportionally once compressor-outlet boost exceeds its
    /// spring; with ECU boost control the solenoid can hold it shut longer (never open it earlier).
    /// </summary>
    private TurboStep UpdateTurbo(double dt, double rpm, in AirPathResult air, double ambientPressure, double ambientTemperature)
    {
        var c = Config;
        var s = State;
        var t = c.Turbo;
        if (t == null) return new TurboStep(0.0, 0.0, 0.0, s.ExhaustGasTemperature);
        double turbineInlet = _airPath.ManifoldOutletTemperature(s.ExhaustGasTemperature, air.ExhaustMassFlow, ambientTemperature);
        if (Damage.HasFailure(FailureMode.TurboOverspeed) || Damage.HasFailure(FailureMode.TurbineOverTemperature))
        {
            // A failed turbo no longer compresses: the damaged wheel just freewheels as a restriction.
            s.TurboOmega = 0.0;
            s.TurbineOutletTemperature = turbineInlet;
            return new TurboStep(0.0, 0.0, t.WastegateSpring, turbineInlet);
        }

        double spring = t.WastegateSpring;
        double boostGauge = air.CompressorOutletPressure - ambientPressure;
        double mechanical = MathUtil.Clamp01((boostGauge - spring) / WastegateActuatorSpan);
        double target = spring;
        double command = mechanical;
        double? ecuTarget = c.Ecu.BoostControl ? Ecu.Tune.BoostTargetKpaAt(rpm) : null;
        if (ecuTarget is double absKpa)
        {
            target = Math.Max(spring, Units.KpaToPa(absKpa) - ambientPressure);
            double error = (boostGauge - target) / WastegateActuatorSpan;
            // Anti-windup: stop integrating while the output is pinned in the direction the error pushes it
            // (gate held shut during spool-up, or at the mechanical limit). Otherwise the integrator winds
            // up during spool and the gate opens late: a 20+ kPa boost spike on every tip-in.
            double unclamped = error + s.BoostControlIntegral;
            bool pinnedShut = unclamped <= 0.0 && error < 0.0;
            bool pinnedOpen = unclamped >= mechanical && error > 0.0;
            if (!pinnedShut && !pinnedOpen) s.BoostControlIntegral += BoostControlIntegralGain * error * dt;
            command = Math.Min(mechanical, MathUtil.Clamp01(error + s.BoostControlIntegral));
        }
        s.WastegateOpening += (command - s.WastegateOpening) * MathUtil.LagFactor(dt, 0.08);

        var (turbinePower, _) = TurbochargerModel.Turbine(t, s.TurboOmega, air.TurbineMassFlow,
            air.TurbineInletPressure, air.TurbineOutletPressure, turbineInlet);
        double compressorPower = air.Compressor.Power;
        double friction = TurbochargerModel.FrictionPower(t, s.TurboOmega, c.Part(PartCategory.Turbocharger).Wear);
        double energy = 0.5 * t.RotorInertia * s.TurboOmega * s.TurboOmega;
        // Kinetic energy cannot go below zero (the shaft stops); there is no upper clamp: the compressor
        // absorbs work in proportion to tip speed squared, so the shaft settles where the powers balance.
        energy = Math.Max(0.0, energy + (turbinePower - compressorPower - friction) * dt);
        s.TurboOmega = Math.Sqrt(2.0 * energy / t.RotorInertia);

        // Exhaust temperature after the turbine, mixed with the wastegate bypass flow.
        if (air.ExhaustMassFlow > 0)
        {
            double tTurbineOut = turbineInlet - (air.TurbineMassFlow > 0 ? turbinePower / (air.TurbineMassFlow * PhysicalConstants.ExhaustCp) : 0.0);
            double bypass = air.ExhaustMassFlow - air.TurbineMassFlow;
            s.TurbineOutletTemperature = (tTurbineOut * air.TurbineMassFlow + turbineInlet * bypass) / air.ExhaustMassFlow;
        }
        else s.TurbineOutletTemperature = turbineInlet;
        return new TurboStep(turbinePower, compressorPower, target, turbineInlet);
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

        bool seized = Damage.Seized;
        if (input.SpeedMode == SpeedMode.Held && !seized)
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
        double evapCooling = CombustionModel.EvaporativeCooling(s.LastFuelAirRatio, fuel.LatentHeat);
        var air = _airPath.Solve(new AirPathConditions(rpm, throttleArea, input.AmbientPressure, input.AmbientTemperature,
            s.ExhaustGasTemperature, s.CoolantTemperature, s.LastFuelAirRatio, evapCooling, floatRpm,
            s.TurboOmega, s.WastegateOpening, input.CoolingAirSpeed, s.TurbineOutletTemperature));

        // ---- ECU fuel and spark ----
        double mapReading = Ecu.ReadMap(air.ManifoldPressure);
        bool mapSaturated = air.ManifoldPressure > c.Ecu.MapSensorMax + 500.0;
        double targetLambda = Ecu.TargetLambda(rpm, mapReading);
        double advance = Ecu.SparkAdvance(rpm, mapReading);

        bool wantsToFire = !seized && input.Ignition && rpm >= FiringMinRpm && (s.Running || input.Starter || rpm >= RunningRpm);
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
            double eta = CombustionModel.IndicatedEfficiency(cr) * (1.0 - 0.10 * ringWear) * DegradedEfficiency();
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

        // ---- Heat flows (energy-conserving: fuel = brake + coolant + oil + exhaust) ----
        double cyclesPerSecond = rpm / 120.0 * g.Cylinders;
        // Released chemical power: a rich mixture leaves CO and H₂ unburned (see RichHeatRelease).
        double fuelPower = firing ? burnedFuel * fuel.LowerHeatingValue * CombustionModel.RichHeatRelease(lambda) * cyclesPerSecond : 0.0;
        double indicatedPower = grossWork * cyclesPerSecond;
        double meanEffectiveToPower = g.Displacement / (4.0 * Math.PI) * omega;
        double frictionPower = fmep * meanEffectiveToPower;
        double pumpingPower = pmep * meanEffectiveToPower;
        double coolantFraction = CombustionModel.CoolantHeatFraction(rpm, knock)
                                 * (Damage.HasFailure(FailureMode.HeadGasketBreach) ? 1.3 : 1.0);
        var heat = CombustionModel.SplitHeat(fuelPower, indicatedPower, coolantFraction, CombustionModel.OilHeatFraction);
        double exhaustFlow = air.MassFlow + (firing ? fuelPerCycle * cyclesPerSecond : 0.0);
        // Pumping work is done on the gas and leaves with it; friction heats coolant and oil.
        double exhaustHeat = heat.ToExhaust + pumpingPower;
        double heatToCoolant = heat.ToCoolant + (1.0 - CombustionModel.FrictionHeatToOil) * frictionPower;
        double heatToOil = heat.ToOil + CombustionModel.FrictionHeatToOil * frictionPower;
        if (!firing && exhaustFlow > 0)
        {
            // Motoring: the gas picks up heat from the chamber walls, cooling the engine.
            double pickup = CombustionModel.MotoringWallHeatPickup * exhaustFlow * PhysicalConstants.ExhaustCp
                            * (s.CoolantTemperature - air.ChargeTemperature);
            exhaustHeat += pickup;
            heatToCoolant -= pickup;
        }
        // Unburned (excess) fuel still has to evaporate: its latent heat comes out of the exhaust gas. The
        // share that evaporated in the intake was already taken from the charge temperature.
        double excessFuelFlow = firing ? Math.Max(0.0, fuelPerCycle - burnedFuel) * cyclesPerSecond : 0.0;
        double latentHeat = (1.0 - CombustionModel.EvaporatedBeforeInletValveCloses) * excessFuelFlow * fuel.LatentHeat;
        double egtTarget = exhaustFlow > 0
            ? air.ChargeTemperature + (exhaustHeat - latentHeat) / (exhaustFlow * PhysicalConstants.ExhaustCp)
            : air.ChargeTemperature;

        // Piston crown temperature (quasi-steady target, lagged).
        double heatFlux = fuelPower / (g.Cylinders * g.PistonArea);
        double crownTarget = s.CoolantTemperature
            + 150.0 * Math.Pow(Math.Max(0.0, heatFlux) / 14.2e6, 0.7) * (1.0 + 1.5 * Math.Max(0.0, (double.IsFinite(lambda) ? lambda : 1.0) - 0.9))
            + 8.0 * knock;

        // ---- Mechanical loads ----
        double inertiaLoad = g.RodInertiaLoad(omega);
        double gasLoad = pcp * g.PistonArea;
        double compressive = Math.Max(0.0, gasLoad - inertiaLoad);
        double bearingLoad = Math.Max(compressive, inertiaLoad);

        // ---- Oil ----
        // Oil surge: once the pickup uncovers, the pump draws air and pressure collapses however much
        // surplus capacity the pump has.
        double aeration = input.SumpAccelerationG > c.OilPan.MaxSustainedG
            ? MathUtil.Clamp01(1.0 - (input.SumpAccelerationG - c.OilPan.MaxSustainedG) / OilSurgeWindowG)
            : 1.0;
        double oilPressure = OilPressure(rpm, s.OilTemperature, 1.0) * aeration;

        // ---- Thermal integration ----
        double coolingAir = Math.Max(0.0, input.CoolingAirSpeed);
        double airFactor = MathUtil.Clamp(Math.Pow(coolingAir / c.Radiator.ReferenceAirSpeedMs, 0.6), 0.1, 2.5);
        double thermostat = MathUtil.SmoothStep(c.Radiator.ThermostatOpen, c.Radiator.ThermostatOpen + 10.0, s.CoolantTemperature);
        double radiatorHeat = c.Radiator.HeatRejectionWPerK * airFactor * thermostat * s.CoolantLevel * (s.CoolantTemperature - input.AmbientTemperature);
        double surfaceLoss = 15.0 * (s.CoolantTemperature - input.AmbientTemperature);
        double oilToCoolant = OilToCoolantConductance * (s.OilTemperature - s.CoolantTemperature);
        double sumpLoss = 12.0 * (1.0 + 0.1 * coolingAir) * (s.OilTemperature - input.AmbientTemperature);
        s.CoolantTemperature += (heatToCoolant - radiatorHeat - surfaceLoss + oilToCoolant) / c.CoolantHeatCapacity * dt;
        s.OilTemperature += (heatToOil - oilToCoolant - sumpLoss) / c.OilHeatCapacity * dt;
        if (input.CoolantTemperatureOverride is double heldCoolant) s.CoolantTemperature = heldCoolant;
        else if (s.CoolantTemperature > CoolantBoilingPoint)
        {
            // Above the boiling point the surplus heat boils coolant off through the pressure cap.
            double surplus = (s.CoolantTemperature - CoolantBoilingPoint) * c.CoolantHeatCapacity;
            double coolantMass = (c.Block.CoolantCapacityL + c.Radiator.CoolantCapacityL) * 1.05;
            s.CoolantLevel = Math.Max(0.0, s.CoolantLevel - surplus / (CoolantLatentHeat * coolantMass));
            s.CoolantTemperature = CoolantBoilingPoint;
        }
        s.ExhaustGasTemperature += (egtTarget - s.ExhaustGasTemperature) * MathUtil.LagFactor(dt, 0.2);
        s.EgtSensor += (s.ExhaustGasTemperature - s.EgtSensor) * MathUtil.LagFactor(dt, 1.5);
        s.PistonCrownTemperature += (crownTarget - s.PistonCrownTemperature) * MathUtil.LagFactor(dt, 3.0);
        s.LastFuelAirRatio = firing ? fuelPerCycle / air.AirPerCycle : 0.0;

        // ---- Turbocharger ----
        var turbo = UpdateTurbo(dt, rpm, air, input.AmbientPressure, input.AmbientTemperature);

        // ---- ECU post-step ----
        Ecu.UpdateKnockControl(knock, dt);

        // ---- Speed ----
        if (seized)
        {
            // A seized engine locks up: whatever was turning it stops almost at once.
            s.Omega = Math.Max(0.0, s.Omega - 3000.0 * dt);
            torque = 0.0;
            power = 0.0;
        }
        else if (input.SpeedMode == SpeedMode.Free)
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
            Seized = seized,
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
            CoolantLevel = s.CoolantLevel,
            OilTemperature = s.OilTemperature,
            OilPressure = oilPressure,
            OilPressureRequired = RequiredOilPressure(rpm),
            ExhaustGasTemperature = s.EgtSensor,
            PistonCrownTemperature = s.PistonCrownTemperature,
            FuelPower = fuelPower,
            HeatToCoolant = heatToCoolant,
            HeatToOil = heatToOil,
            ExhaustHeat = exhaustHeat,
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
            PortGasTemperature = s.ExhaustGasTemperature,
            TurbineInletTemperature = turbo.TurbineInletTemperature,
            CompressorSurgeDepth = air.Compressor.SurgeDepth,
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
        if (DamageEnabled)
        {
            Damage.Update(t, dt, Ecu.EffectiveRevLimit, input.SumpAccelerationG);
            if (Damage.Seized && !seized)
            {
                s.Running = false;
                t = t with { Seized = true };
            }
        }
        Last = t;
        return t;
    }

    /// <summary>Efficiency penalty from degraded (non-catastrophic) failures: compression leaks.</summary>
    private double DegradedEfficiency()
    {
        double f = 1.0;
        if (Damage.HasFailure(FailureMode.HeadGasketBreach)) f *= 0.75;
        if (Damage.HasFailure(FailureMode.CylinderHeadWarp)) f *= 0.9;
        return f;
    }
}
