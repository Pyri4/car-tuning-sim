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

    /// <summary>Heat exchange between oil and coolant through the block, head and oil/water cooler, W/K, for a 2.0 L engine.</summary>
    public const double OilToCoolantConductance = 400.0;

    /// <summary>Block/hose surface loss (coolant) and sump convection (oil, still air) for a 2.0 L engine, W/K.</summary>
    public const double CoolantSurfaceLoss = 15.0, SumpLoss = 12.0;

    /// <summary>Surface areas scale with size: conductances above × (displacement / 2.0 L)^(2/3).</summary>
    private double SurfaceScale => Math.Pow(Config.Geometry.Displacement / 0.002, 2.0 / 3.0);

    /// <summary>Boiling point of 50/50 coolant under a ~1.1 bar pressure cap, K (128 °C).</summary>
    public const double CoolantBoilingPoint = 401.15;

    /// <summary>Latent heat of vaporisation used for boiling coolant off, J/kg.</summary>
    public const double CoolantLatentHeat = 2.0e6;

    /// <summary>
    /// Time constant of an oil-pressure cam phaser following its target, s (hydraulic phasers sweep their range in a
    /// few tenths of a second). Oil-pressure dependence is not modelled: a running engine can always move them.
    /// </summary>
    public const double CamPhaserTimeConstant = 0.15;

    private readonly AirPath[] _airPaths;
    private readonly AirPathResult[] _air;
    private readonly BankStep[] _bank;
    private readonly double _referenceDensity =
        PhysicalConstants.StandardPressure / (PhysicalConstants.AirGasConstant * PhysicalConstants.StandardTemperature);

    public EngineSimulation(EngineConfiguration config, EcuTune tune, EngineState? state = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Ecu = new EcuController(config.Ecu, tune ?? throw new ArgumentNullException(nameof(tune)));
        State = state ?? new EngineState();
        State.EnsureShape(config.Banks.Count, config.Turbos.Count);
        _airPaths = config.Banks.Select(b => new AirPath(b)).ToArray();
        _air = new AirPathResult[config.Banks.Count];
        _bank = new BankStep[config.Banks.Count];
        Damage = new DamageModel(config);
    }

    /// <summary>The air path of bank <paramref name="bank"/> (VE shape, residuals and the flow solve for its cylinders).</summary>
    public AirPath AirPathOf(int bank) => _airPaths[bank];

    /// <summary>Per-bank results of the combustion and heat calculation within one step.</summary>
    private struct BankStep
    {
        public bool Firing;
        public double Lambda, Mbt, KnockLimit, Knock, BurnedFuel, GrossWork, Imep, Pmep, Pcp, Fmep, Bmep, Torque;
        public double FuelPower, ExhaustFlow, ExhaustHeat, HeatToCoolant, HeatToOil, PortWallHeat, EgtTarget, CrownTarget, FloatRpm;
        public bool WallsLimited;
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

    /// <summary>Valve-float speed of the engine now: the lowest bank's, on the cam profile each bank is running, with current spring wear.</summary>
    public double ValveFloatRpm()
    {
        double min = double.PositiveInfinity;
        for (int b = 0; b < Config.Banks.Count; b++)
            min = Math.Min(min, Config.Banks[b].ValveFloatRpm(State.Banks[b].HighValveLift ? 1 : 0));
        return min;
    }

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

    private readonly record struct TurboStep(double TurbinePower, double CompressorPower, double BoostTarget, double TurbineInletTemperature,
        double TurbineBladeSpeedRatio = 0.0);

    /// <summary>Wastegate actuator span: boost above the spring over which the gate goes from shut to fully open, Pa.</summary>
    public const double WastegateActuatorSpan = 20_000.0;

    /// <summary>Integral gain of the ECU boost controller, per second (on the error normalised by the actuator span).</summary>
    public const double BoostControlIntegralGain = 3.0;

    /// <summary>
    /// Pedal position above which the ECU closes the boost loop. Below it the solenoid releases and the wastegate
    /// spring alone sets boost (holding the gate shut against a part-open throttle would spin the turbo up behind it).
    /// </summary>
    public const double BoostControlMinThrottle = 0.8;

    /// <summary>
    /// Wastegate control and shaft-energy integration for one turbocharger: dE/dt = P_turbine − P_compressor −
    /// P_friction, E = ½·I·ω², the turbine and compressor powers summed over the banks it serves (each bank's share of
    /// the device, evaluated at the device's flow). The wastegate actuator opens proportionally once compressor-outlet
    /// boost (its pneumatic reference) exceeds its spring; with ECU boost control the solenoid can hold it shut longer
    /// (never open it earlier). The ECU closes that loop on its own MAP sensor — clipped at the sensor's range, so a
    /// target above the range is never seen as reached and the gate stays shut.
    /// </summary>
    private TurboStep UpdateTurbo(TurboConfiguration tc, double dt, double rpm, double throttle, double ambientPressure, double ambientTemperature)
    {
        var c = Config;
        var s = State;
        var ts = s.Turbos[tc.Index];
        var t = tc.Spec;
        int first = tc.Banks[0];
        var firstAir = _air[first];
        double firstInlet = _airPaths[first].ManifoldOutletTemperature(s.Banks[first].ExhaustGasTemperature, firstAir.ExhaustMassFlow, ambientTemperature);
        if (tc.Part.IsFailed && tc.Part.Damage.Failure!.Mode is FailureMode.TurboOverspeed or FailureMode.TurbineOverTemperature)
        {
            // A failed turbo no longer compresses: the damaged wheel just freewheels as a restriction.
            ts.Omega = 0.0;
            foreach (int b in tc.Banks)
                s.Banks[b].TurbineOutletTemperature = _airPaths[b].ManifoldOutletTemperature(s.Banks[b].ExhaustGasTemperature, _air[b].ExhaustMassFlow, ambientTemperature);
            return new TurboStep(0.0, 0.0, t.WastegateSpring, firstInlet);
        }

        double spring = t.WastegateSpring;
        double boostGauge = firstAir.CompressorOutletPressure - ambientPressure;
        double mechanical = MathUtil.Clamp01((boostGauge - spring) / WastegateActuatorSpan);
        double target = spring;
        double command = mechanical;
        double? ecuTarget = c.Ecu.BoostControl ? Ecu.Tune.BoostTargetKpaAt(rpm) : null;
        if (ecuTarget is double absKpa && throttle >= BoostControlMinThrottle)
        {
            target = Math.Max(spring, Units.KpaToPa(absKpa) - ambientPressure);
            // One MAP sensor: the ECU reads the plenum of the first bank's air path.
            double measuredBoost = Ecu.ReadMap(_air[0].ManifoldPressure) - ambientPressure;
            double error = (measuredBoost - target) / WastegateActuatorSpan;
            // Anti-windup: stop integrating while the output is pinned in the direction the error pushes it
            // (gate held shut during spool-up, or at the mechanical limit). Otherwise the integrator winds
            // up during spool and the gate opens late: a 20+ kPa boost spike on every tip-in.
            double unclamped = error + ts.BoostControlIntegral;
            bool pinnedShut = unclamped <= 0.0 && error < 0.0;
            bool pinnedOpen = unclamped >= mechanical && error > 0.0;
            if (!pinnedShut && !pinnedOpen) ts.BoostControlIntegral += BoostControlIntegralGain * error * dt;
            command = Math.Min(mechanical, MathUtil.Clamp01(error + ts.BoostControlIntegral));
        }
        else ts.BoostControlIntegral = 0.0;
        ts.WastegateOpening += (command - ts.WastegateOpening) * MathUtil.LagFactor(dt, 0.08);

        double turbinePower = 0.0, compressorPower = 0.0, firstBladeSpeedRatio = 0.0;
        foreach (int b in tc.Banks)
        {
            var air = _air[b];
            double share = c.Banks[b].TurboShare;
            double turbineInlet = _airPaths[b].ManifoldOutletTemperature(s.Banks[b].ExhaustGasTemperature, air.ExhaustMassFlow, ambientTemperature);
            var (devicePower, _, bladeSpeedRatio) = TurbochargerModel.Turbine(t, ts.Omega, air.TurbineMassFlow / share,
                air.TurbineInletPressure, air.TurbineOutletPressure, turbineInlet);
            double bankTurbinePower = devicePower * share;
            turbinePower += bankTurbinePower;
            compressorPower += air.Compressor.Power * share;
            if (b == first) firstBladeSpeedRatio = bladeSpeedRatio;

            // Exhaust temperature after this bank's share of the turbine, mixed with its wastegate bypass flow.
            if (air.ExhaustMassFlow > 0)
            {
                double tTurbineOut = turbineInlet - (air.TurbineMassFlow > 0 ? bankTurbinePower / (air.TurbineMassFlow * PhysicalConstants.ExhaustCp) : 0.0);
                double bypass = air.ExhaustMassFlow - air.TurbineMassFlow;
                s.Banks[b].TurbineOutletTemperature = (tTurbineOut * air.TurbineMassFlow + turbineInlet * bypass) / air.ExhaustMassFlow;
            }
            else s.Banks[b].TurbineOutletTemperature = turbineInlet;
        }
        double friction = TurbochargerModel.FrictionPower(t, ts.Omega, tc.Part.Wear);
        double energy = 0.5 * t.RotorInertia * ts.Omega * ts.Omega;
        // Kinetic energy cannot go below zero (the shaft stops); there is no upper clamp: the compressor
        // absorbs work in proportion to tip speed squared, so the shaft settles where the powers balance.
        energy = Math.Max(0.0, energy + (turbinePower - compressorPower - friction) * dt);
        ts.Omega = Math.Sqrt(2.0 * energy / t.RotorInertia);
        return new TurboStep(turbinePower, compressorPower, target, firstInlet, firstBladeSpeedRatio);
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
        int banks = c.Banks.Count;

        bool seized = Damage.Seized;
        if (input.SpeedMode == SpeedMode.Held && !seized)
            s.Omega = Units.RpmToRadPerSec(Math.Max(0.0, input.HeldRpm));
        double rpm = Units.RadPerSecToRpm(s.Omega);
        double omega = s.Omega;

        // ---- ECU pre-step: rev limiter and idle air ----
        bool revCut = Ecu.UpdateRevLimiter(rpm);
        double idleValve = Ecu.UpdateIdle(rpm, input.Throttle, s.Running, dt);
        double throttleFraction = ThrottleAreaFraction(input.Throttle) + IdleBypassAreaFraction * idleValve + ThrottleLeakAreaFraction;

        // ---- Air path, bank by bank ----
        // The ECU drives every bank's cam phaser from one table at the MAP it last read (parked unless running: no oil
        // pressure), and switches valve lift and intake runners by engine speed.
        double camMap = Ecu.ReadMap(s.LastManifoldPressure);
        bool anyAir = false;
        for (int b = 0; b < banks; b++)
        {
            var bank = c.Banks[b];
            var bs = s.Banks[b];
            double camTarget = s.Running ? Ecu.IntakeCamAdvanceTarget(rpm, camMap, bank.IntakePhaserRange) : 0.0;
            bs.IntakeCamAdvance += (camTarget - bs.IntakeCamAdvance) * MathUtil.LagFactor(dt, CamPhaserTimeConstant);
            bs.HighValveLift = bank.HasVariableLift && Ecu.HighValveLift(rpm, bs.HighValveLift, s.Running);
            bs.RunnerStage = bank.HasSwitchedRunner ? Ecu.IntakeRunnerStage(rpm, bs.RunnerStage, bank.RunnerStages.Count, s.Running) : 0;
            int profile = bs.HighValveLift ? 1 : 0;
            double floatRpm = bank.ValveFloatRpm(profile);
            _bank[b].FloatRpm = floatRpm;
            double evapCooling = CombustionModel.EvaporativeCooling(bs.LastFuelAirRatio, fuel.LatentHeat);
            var turbo = bank.Turbo == null ? null : s.Turbos[bank.Turbo.Index];
            _air[b] = _airPaths[b].Solve(new AirPathConditions(rpm, bank.ThrottleCdA * throttleFraction, input.AmbientPressure, input.AmbientTemperature,
                bs.ExhaustGasTemperature, s.CoolantTemperature, bs.LastFuelAirRatio, evapCooling, floatRpm,
                turbo?.Omega ?? 0.0, turbo?.WastegateOpening ?? 0.0, input.CoolingAirSpeed, bs.TurbineOutletTemperature, bs.IntakeCamAdvance,
                profile, bs.RunnerStage, _air[b].ManifoldTemperature)); // the runner gas of the previous step (0 on the first: ambient)
            anyAir |= _air[b].AirPerCycle > 0;
        }
        // One MAP sensor: on the plenum of the first bank's air path.
        var ecuAir = _air[0];
        s.LastManifoldPressure = ecuAir.ManifoldPressure;

        // ---- ECU fuel and spark (one injection and ignition pattern for every cylinder) ----
        double mapReading = Ecu.ReadMap(ecuAir.ManifoldPressure);
        bool mapSaturated = ecuAir.ManifoldPressure > c.Ecu.MapSensorMax + 500.0;
        double targetLambda = Ecu.TargetLambda(rpm, mapReading);
        double advance = Ecu.SparkAdvance(rpm, mapReading);

        bool wantsToFire = !seized && input.Ignition && rpm >= FiringMinRpm && (s.Running || input.Starter || rpm >= RunningRpm);
        double cycleTime = rpm > 1 ? 120.0 / rpm : 0.0;
        FuelDelivery delivery = default;
        if (wantsToFire && !revCut && anyAir)
        {
            double estAir = Ecu.EstimatedAirPerCycle(rpm, ecuAir.ManifoldPressure, ecuAir.ManifoldTemperature, g.Cylinders);
            double fuelCmd = Ecu.CommandedFuelPerCycle(estAir, targetLambda);
            double pw = Ecu.PulseWidth(fuelCmd);
            delivery = FuelSystem.Deliver(c.Injectors, c.Part(PartCategory.Injectors).Wear, c.FuelPump,
                c.Part(PartCategory.FuelPump).Wear, fuel.Density, pw, cycleTime, ecuAir.ManifoldPressure, input.AmbientPressure);
        }
        double fuelPerCycle = delivery.FuelPerCycle;
        bool fuelled = wantsToFire && !revCut && fuelPerCycle > 0;

        // ---- Combustion and heat, bank by bank ----
        double ringWear = c.Part(PartCategory.Pistons).Wear;
        double pistonSpeed = g.MeanPistonSpeed(rpm);
        bool firing = false;
        double torque = 0.0, fuelPower = 0.0, heatToCoolant = 0.0, heatToOil = 0.0, exhaustHeat = 0.0, portWallHeat = 0.0;
        double crownTarget = double.NegativeInfinity;
        bool wallsLimited = false;
        for (int b = 0; b < banks; b++)
        {
            var bank = c.Banks[b];
            var air = _air[b];
            ref var r = ref _bank[b];
            double cr = bank.Geometry.CompressionRatio;
            r.Firing = fuelled && air.AirPerCycle > 0;
            firing |= r.Firing;
            r.Lambda = r.Firing ? air.AirPerCycle / (fuelPerCycle * fuel.StoichiometricAfr) : double.PositiveInfinity;
            double chargeDensityRatio = air.PortPressure / (PhysicalConstants.AirGasConstant * air.ChargeTemperature) / _referenceDensity;
            r.Mbt = CombustionModel.MbtAdvance(rpm, chargeDensityRatio);
            r.KnockLimit = r.Firing
                ? KnockModel.KnockLimitedAdvance(new CombustionModel.KnockConditions(
                    rpm, fuel.OctaneRon, cr, air.PortPressure, air.ChargeTemperature, s.CoolantTemperature,
                    r.Lambda, Units.MToMm(bank.Geometry.DeckClearance), air.ExhaustPortPressure / air.PortPressure, fuel.OctaneSensitivity), advance)
                : double.PositiveInfinity;
            r.Knock = r.Firing ? CombustionModel.KnockIntensity(advance, r.KnockLimit) : 0.0;

            r.BurnedFuel = 0.0;
            r.GrossWork = 0.0;
            if (r.Firing)
            {
                r.BurnedFuel = Math.Min(fuelPerCycle, air.AirPerCycle / fuel.StoichiometricAfr);
                double eta = CombustionModel.IndicatedEfficiency(cr) * (1.0 - 0.10 * ringWear) * DegradedEfficiency(bank);
                r.GrossWork = r.BurnedFuel * fuel.LowerHeatingValue * eta
                              * CombustionModel.MixtureFactor.Evaluate(r.Lambda)
                              * CombustionModel.SparkFactor(advance, r.Mbt)
                              * CombustionModel.KnockTorqueFactor(r.Knock);
            }
            r.Imep = r.GrossWork / g.SweptVolumePerCylinder;
            r.Pmep = air.ExhaustPortPressure - air.PortPressure;
            r.Pcp = r.Firing
                ? CombustionModel.PeakCylinderPressure(air.PortPressure, cr, r.Imep, advance, r.Mbt, r.Knock)
                : air.PortPressure * Math.Pow(cr, PhysicalConstants.CompressionPolytropicExponent);
            r.Fmep = rpm > 1
                ? CombustionModel.FrictionMep(pistonSpeed, OilViscosity.At(s.OilTemperature), bank.Springs.OpenForceN, r.Pcp, bank.Head.ValvesPerCylinder, g.SweptVolumePerCylinder)
                : 0.0;
            r.Bmep = r.Imep - r.Pmep - r.Fmep;
            r.Torque = rpm > 1 ? r.Bmep * bank.SweptVolume / (4.0 * Math.PI) : 0.0;
            torque += r.Torque;

            // Heat flows (energy-conserving: fuel = brake + coolant + oil + exhaust).
            double cyclesPerSecond = rpm / 120.0 * bank.Cylinders;
            // Released chemical power: a rich mixture leaves CO and H₂ unburned (see RichHeatRelease).
            r.FuelPower = r.Firing ? r.BurnedFuel * fuel.LowerHeatingValue * CombustionModel.RichHeatRelease(r.Lambda) * cyclesPerSecond : 0.0;
            double indicatedPower = r.GrossWork * cyclesPerSecond;
            double meanEffectiveToPower = bank.SweptVolume / (4.0 * Math.PI) * omega;
            double frictionPower = r.Fmep * meanEffectiveToPower;
            double pumpingPower = r.Pmep * meanEffectiveToPower;
            double coolantFraction = CombustionModel.CoolantHeatFraction(rpm, r.Knock)
                                     * (bank.HeadGasketPart.Damage.Failure?.Mode == FailureMode.HeadGasketBreach ? 1.3 : 1.0);
            var heat = CombustionModel.SplitHeat(r.FuelPower, indicatedPower, coolantFraction, CombustionModel.OilHeatFraction);
            r.WallsLimited = heat.WallsLimited;
            r.ExhaustFlow = air.MassFlow + (r.Firing ? fuelPerCycle * cyclesPerSecond : 0.0);
            // Pumping work is done on the gas and leaves with it; friction heats coolant and oil.
            r.ExhaustHeat = heat.ToExhaust + pumpingPower;
            r.HeatToCoolant = heat.ToCoolant + (1.0 - CombustionModel.FrictionHeatToOil) * frictionPower;
            r.HeatToOil = heat.ToOil + CombustionModel.FrictionHeatToOil * frictionPower;
            // Unburned (excess) fuel still has to evaporate: its latent heat comes out of the exhaust gas. The
            // share that evaporated in the intake was already taken from the charge temperature.
            double excessFuelFlow = r.Firing ? Math.Max(0.0, fuelPerCycle - r.BurnedFuel) * cyclesPerSecond : 0.0;
            double latentHeat = (1.0 - CombustionModel.EvaporatedBeforeInletValveCloses) * excessFuelFlow * fuel.LatentHeat;
            r.EgtTarget = air.ChargeTemperature;
            r.PortWallHeat = 0.0;
            if (r.ExhaustFlow > 0)
            {
                // On its way out through the coolant-jacketed port the gas exchanges heat with the walls (exact
                // uniform-pipe solution, like the manifold). At full load that is a few percent of its excess heat;
                // on closed-throttle overrun a few grams per second carry the whole pumping work, and without the
                // walls they would leave at thousands of kelvin. Motoring with an open throttle, cold gas picks heat
                // up from the walls by the same law.
                double capacityRate = r.ExhaustFlow * PhysicalConstants.ExhaustCp;
                double adiabatic = air.ChargeTemperature + (r.ExhaustHeat - latentHeat) / capacityRate;
                double exchanged = 1.0 - Math.Exp(-bank.ExhaustPortHeatTransfer / capacityRate);
                r.PortWallHeat = exchanged * capacityRate * (adiabatic - s.CoolantTemperature);
                r.ExhaustHeat -= r.PortWallHeat;
                r.HeatToCoolant += r.PortWallHeat;
                r.EgtTarget = adiabatic - r.PortWallHeat / capacityRate;
            }

            // Piston crown temperature (quasi-steady target): the pistons are one set, so the hottest bank sets it.
            double heatFlux = r.FuelPower / (bank.Cylinders * g.PistonArea);
            r.CrownTarget = s.CoolantTemperature
                + 150.0 * Math.Pow(Math.Max(0.0, heatFlux) / 14.2e6, 0.7) * (1.0 + 1.5 * Math.Max(0.0, (double.IsFinite(r.Lambda) ? r.Lambda : 1.0) - 0.9))
                + 8.0 * r.Knock;
            crownTarget = Math.Max(crownTarget, r.CrownTarget);

            fuelPower += r.FuelPower;
            heatToCoolant += r.HeatToCoolant;
            heatToOil += r.HeatToOil;
            exhaustHeat += r.ExhaustHeat;
            portWallHeat += r.PortWallHeat;
            wallsLimited |= r.WallsLimited;
        }
        double power = torque * omega;

        // Engine-level aggregates: per-cylinder quantities as cylinder-weighted means, damage drivers as the worst bank.
        double peakPressure = double.NegativeInfinity, knock = 0.0, knockLimit = double.PositiveInfinity, lambdaMean = 0.0;
        double imep = 0.0, pmep = 0.0, fmep = 0.0, bmep = 0.0, mbt = 0.0;
        for (int b = 0; b < banks; b++)
        {
            double w = c.Banks[b].CylinderShare;
            ref var r = ref _bank[b];
            peakPressure = Math.Max(peakPressure, r.Pcp);
            knock = Math.Max(knock, r.Knock);
            knockLimit = Math.Min(knockLimit, r.KnockLimit);
            imep += w * r.Imep;
            pmep += w * r.Pmep;
            fmep += w * r.Fmep;
            bmep += w * r.Bmep;
            mbt += w * r.Mbt;
            if (r.Firing) lambdaMean += w * r.Lambda;
        }

        // ---- Mechanical loads (the worst cylinder) ----
        double inertiaLoad = g.RodInertiaLoad(omega);
        double gasLoad = peakPressure * g.PistonArea;
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
        double surface = SurfaceScale;
        double surfaceLoss = CoolantSurfaceLoss * surface * (s.CoolantTemperature - input.AmbientTemperature);
        double oilToCoolant = OilToCoolantConductance * surface * (s.OilTemperature - s.CoolantTemperature);
        double sumpLoss = SumpLoss * surface * (1.0 + 0.1 * coolingAir) * (s.OilTemperature - input.AmbientTemperature);
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
        for (int b = 0; b < banks; b++)
        {
            var bs = s.Banks[b];
            bs.ExhaustGasTemperature += (_bank[b].EgtTarget - bs.ExhaustGasTemperature) * MathUtil.LagFactor(dt, 0.2);
            bs.EgtSensor += (bs.ExhaustGasTemperature - bs.EgtSensor) * MathUtil.LagFactor(dt, 1.5);
            bs.LastFuelAirRatio = _bank[b].Firing ? fuelPerCycle / _air[b].AirPerCycle : 0.0;
        }
        s.PistonCrownTemperature += (crownTarget - s.PistonCrownTemperature) * MathUtil.LagFactor(dt, 3.0);

        // ---- Turbochargers ----
        var turboTelemetry = c.Turbos.Count == 0 ? Array.Empty<TurboTelemetry>() : new TurboTelemetry[c.Turbos.Count];
        TurboStep firstTurbo = default;
        for (int i = 0; i < c.Turbos.Count; i++)
        {
            var tc = c.Turbos[i];
            var step = UpdateTurbo(tc, dt, rpm, input.Throttle, input.AmbientPressure, input.AmbientTemperature);
            if (i == 0) firstTurbo = step;
            var ts = s.Turbos[i];
            var air = _air[tc.Banks[0]];
            turboTelemetry[i] = new TurboTelemetry(Units.RadPerSecToRpm(ts.Omega), air.Compressor.PressureRatio, ts.WastegateOpening,
                step.TurbineInletTemperature, air.Compressor.SurgeDepth, ts.Omega > tc.Spec.MaxShaftSpeed);
        }

        var turbos = turboTelemetry.Length == 0 ? ValueList<TurboTelemetry>.Empty : new ValueList<TurboTelemetry>(turboTelemetry);

        // ---- ECU post-step (the knock sensor hears the worst bank) ----
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

        // ---- Telemetry ----
        var bankTelemetry = new BankTelemetry[banks];
        double airPerCycle = 0.0, manifoldPressure = 0.0, portPressure = 0.0, exhaustManifoldPressure = 0.0, manifoldTemperature = 0.0;
        double chargeTemperature = 0.0, veDynamic = 0.0, waveGain = 0.0, runnerTunedRpm = 0.0, residual = 0.0, camAdvance = 0.0, airMassFlow = 0.0, egtSensor = double.NegativeInfinity;
        double portGas = double.NegativeInfinity, floatRpmMin = double.PositiveInfinity;
        bool highLift = false, switchedRunner = false, veFloor = false, camFloor = false;
        int runnerStage = 0;
        for (int b = 0; b < banks; b++)
        {
            var bank = c.Banks[b];
            var bs = s.Banks[b];
            var air = _air[b];
            ref var r = ref _bank[b];
            double w = bank.CylinderShare;
            int profile = bs.HighValveLift ? 1 : 0;
            airPerCycle += w * air.AirPerCycle;
            manifoldPressure += w * air.ManifoldPressure;
            portPressure += w * air.PortPressure;
            exhaustManifoldPressure += w * air.ExhaustManifoldPressure;
            manifoldTemperature += w * air.ManifoldTemperature;
            chargeTemperature += w * air.ChargeTemperature;
            veDynamic += w * air.VeDynamic;
            waveGain += w * air.WaveGain;
            runnerTunedRpm += w * air.RunnerTunedRpm;
            residual += w * air.ResidualFactor;
            camAdvance += w * bs.IntakeCamAdvance;
            airMassFlow += air.MassFlow;
            egtSensor = Math.Max(egtSensor, bs.EgtSensor);
            portGas = Math.Max(portGas, bs.ExhaustGasTemperature);
            floatRpmMin = Math.Min(floatRpmMin, r.FloatRpm);
            highLift |= bs.HighValveLift;
            switchedRunner |= bs.SwitchedRunner;
            runnerStage = Math.Max(runnerStage, bs.RunnerStage);
            veFloor |= rpm > 1 && _airPaths[b].VeShape(rpm, bs.IntakeCamAdvance, profile) < AirPath.VeShapeFloor;
            camFloor |= bank.TunedPistonSpeed(bs.IntakeCamAdvance, profile) < EngineConfiguration.MinTunedPistonSpeed;
            bankTelemetry[b] = new BankTelemetry(air.AirPerCycle, air.ManifoldPressure, air.PortPressure, air.ExhaustPortPressure,
                r.Firing ? r.Lambda : 0.0, r.Knock, r.KnockLimit, r.Pcp, r.Imep, bs.EgtSensor, bs.IntakeCamAdvance,
                bs.HighValveLift, bs.SwitchedRunner, r.FloatRpm, r.Torque, bs.RunnerStage);
        }
        double cylindersPerSecond = rpm / 120.0 * g.Cylinders;
        var turboAir = c.Turbos.Count > 0 ? _air[c.Turbos[0].Banks[0]] : ecuAir;
        var firstTurboState = c.Turbos.Count > 0 ? s.Turbos[0] : null;
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
            ManifoldPressure = manifoldPressure,
            BoostPressure = manifoldPressure - input.AmbientPressure,
            PortPressure = portPressure,
            ExhaustBackPressure = exhaustManifoldPressure - input.AmbientPressure,
            ManifoldTemperature = manifoldTemperature,
            ChargeTemperature = chargeTemperature,
            AirMassFlow = airMassFlow,
            AirPerCycle = airPerCycle,
            VolumetricEfficiency = airPerCycle / (input.AmbientPressure / (PhysicalConstants.AirGasConstant * input.AmbientTemperature) * g.SweptVolumePerCylinder),
            VeDynamic = veDynamic,
            IntakeWaveGain = waveGain,
            RunnerTunedRpm = runnerTunedRpm,
            ResidualFactor = residual,
            IntakeCamAdvance = camAdvance,
            HighValveLift = highLift,
            SwitchedRunner = switchedRunner,
            RunnerStage = runnerStage,
            TargetLambda = targetLambda,
            Lambda = firing ? lambdaMean : 0.0,
            Afr = firing ? lambdaMean * fuel.StoichiometricAfr : 0.0,
            FuelMassFlow = fuelled ? fuelPerCycle * cylindersPerSecond : 0.0,
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
            PeakCylinderPressure = peakPressure,
            CoolantTemperature = s.CoolantTemperature,
            CoolantLevel = s.CoolantLevel,
            OilTemperature = s.OilTemperature,
            OilPressure = oilPressure,
            OilPressureRequired = RequiredOilPressure(rpm),
            ExhaustGasTemperature = egtSensor,
            PistonCrownTemperature = s.PistonCrownTemperature,
            FuelPower = fuelPower,
            HeatToCoolant = heatToCoolant,
            HeatToOil = heatToOil,
            ExhaustHeat = exhaustHeat,
            PortWallHeat = portWallHeat,
            WallHeatLimited = wallsLimited,
            VeFloorActive = veFloor,
            CamTuningFloorActive = camFloor,
            RadiatorHeatRejection = radiatorHeat,
            CoolantSurfaceLoss = surfaceLoss,
            OilToCoolantHeat = oilToCoolant,
            OilSumpLoss = sumpLoss,
            TurboRpm = firstTurboState != null ? Units.RadPerSecToRpm(firstTurboState.Omega) : 0.0,
            CompressorPressureRatio = c.Turbos.Count > 0 ? turboAir.Compressor.PressureRatio : 1.0,
            CompressorEfficiency = turboAir.Compressor.Efficiency,
            CompressorCorrectedFlow = turboAir.Compressor.CorrectedFlow,
            CompressorChokeRatio = turboAir.Compressor.ChokeRatio,
            CompressorSurge = turboAir.Compressor.Surge,
            CompressorOutletTemperature = turboAir.CompressorOutletTemperature,
            CompressorOutletPressure = turboAir.CompressorOutletPressure,
            CompressorPower = firstTurbo.CompressorPower,
            TurbinePower = firstTurbo.TurbinePower,
            TurbineInletPressure = turboAir.TurbineInletPressure,
            PortGasTemperature = portGas,
            TurbineInletTemperature = c.Turbos.Count > 0 ? firstTurbo.TurbineInletTemperature : portGas,
            TurbineBladeSpeedRatio = firstTurbo.TurbineBladeSpeedRatio,
            CompressorSurgeDepth = turboAir.Compressor.SurgeDepth,
            WastegateOpening = firstTurboState?.WastegateOpening ?? 0.0,
            BoostTarget = firstTurbo.BoostTarget,
            TurboOverspeed = turbos.Any(x => x.Overspeed),
            RodTensileLoad = inertiaLoad,
            RodCompressiveLoad = compressive,
            BearingLoad = bearingLoad,
            ValveFloatRpm = floatRpmMin,
            ValveFloat = rpm > floatRpmMin,
            RevLimiterActive = revCut,
            Banks = new ValueList<BankTelemetry>(bankTelemetry),
            Turbos = turbos,
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

    /// <summary>Efficiency penalty from degraded (non-catastrophic) failures on a bank: compression leaks.</summary>
    private static double DegradedEfficiency(BankConfiguration bank)
    {
        double f = 1.0;
        if (bank.HeadGasketPart.Damage.Failure?.Mode == FailureMode.HeadGasketBreach) f *= 0.75;
        if (bank.HeadPart.Damage.Failure?.Mode == FailureMode.CylinderHeadWarp) f *= 0.9;
        return f;
    }
}
