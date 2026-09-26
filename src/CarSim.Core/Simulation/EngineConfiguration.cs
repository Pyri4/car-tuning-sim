using CarSim.Core.Common;
using CarSim.Core.Engines;
using CarSim.Core.Fuels;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Simulation;

/// <summary>
/// Everything the engine model needs, derived once from a valid assembly and a fuel. Spec values are
/// converted to the air-path, volumetric-efficiency and thermal constants used every step. Wear is not
/// baked in: wear-dependent quantities read the live <see cref="PartInstance"/>s each step.
/// </summary>
public sealed class EngineConfiguration
{
    // ---- Model constants (documented in SIMULATION_SPEC.md) ----

    /// <summary>Crank degrees added to 1 mm duration to approximate the full (advertised) valve event.</summary>
    public const double ValveEventRampDeg = ValvetrainModel.RampAllowanceDeg;

    /// <summary>Samples used to average port flow over the valve-lift profile.</summary>
    private const int LiftProfileSamples = 48;

    /// <summary>Reference runner length for intake tuning, m.</summary>
    public const double ReferenceRunnerLength = 0.300;

    /// <summary>Tuned mean piston speed for a 220° (at 1 mm) intake cam, m/s.</summary>
    public const double BaseTunedPistonSpeed = 15.0;

    /// <summary>Additional tuned mean piston speed per degree of intake duration above 220°, m/s.</summary>
    public const double TunedPistonSpeedPerDeg = 0.15;

    /// <summary>
    /// Intake centreline (crank degrees ATDC) the duration correlation above was fitted at: straight-up cams on the
    /// K20's 108–114° separations, pinned by its 112° OEM pair. The correlation reads duration as a stand-in for
    /// intake closing, so it holds for cams installed there; a cam that states its centreline (degreed in, or on a
    /// phaser) closes the intake earlier or later by its distance from this reference.
    /// </summary>
    public const double ReferenceIntakeCenterlineDeg = 112.0;

    /// <summary>
    /// Tuned mean piston speed per crank degree the intake closes later, m/s: duration moves the closing by half a
    /// degree per degree (0.15 m/s), a centreline shift by a whole degree.
    /// </summary>
    public const double TunedPistonSpeedPerCenterlineDeg = 2.0 * TunedPistonSpeedPerDeg;

    /// <summary>
    /// Guard on the correlation, m/s: a very early intake closing would extrapolate it to zero or negative speed.
    /// Below it the VE peak sits at a few hundred rpm (under any idle), where the engine already breathes badly at
    /// speed. Tested not to act on shipped cams and calibrations (<c>CamTuningFloorActive</c>).
    /// </summary>
    public const double MinTunedPistonSpeed = 2.0;

    /// <summary>Header tuning constant: tuned rpm ≈ K / primary length (mm).</summary>
    public const double HeaderTuningConstant = 5.2e6;

    /// <summary>
    /// Exhaust-manifold heat loss to the engine bay per mm of primary pipe per cylinder, W/K (a bare steel
    /// primary of ≈ 40 mm diameter in underbonnet airflow). A 250 mm log manifold on four cylinders loses
    /// ≈ 7.5 W/K; an 820 mm tubular header ≈ 25 W/K.
    /// </summary>
    public const double ManifoldHeatLossPerPrimaryMm = 0.0075;

    /// <summary>
    /// Exhaust-port (and late-cycle chamber) wall area per cylinder over bore², m²/m²: a port of throat ≈ 0.35·B
    /// and length ≈ 1.2·B has π·0.35·1.2 ≈ 1.3·B² of wall.
    /// </summary>
    public const double ExhaustPortWallAreaPerBoreSquared = 1.3;

    /// <summary>
    /// Cycle-averaged gas-to-wall heat-transfer coefficient in the exhaust port, W/(m²·K). Port measurements
    /// (Caton &amp; Heywood 1981) give ≈ 400–1000 W/(m²·K) while the valve is open, a third of the cycle. Held
    /// constant with flow (like the manifold's loss), so the port takes a small share of the gas's excess
    /// heat at full load and nearly all of it when only a few grams per second flow (closed-throttle overrun).
    /// </summary>
    public const double ExhaustPortHeatTransferCoefficient = 150.0;

    private EngineConfiguration(EngineAssembly assembly, FuelDefinition fuel, EngineGeometry geometry)
    {
        Assembly = assembly;
        Fuel = fuel;
        Geometry = geometry;
        Block = Spec<BlockSpec>(PartCategory.Block);
        Crankshaft = Spec<CrankshaftSpec>(PartCategory.Crankshaft);
        MainBearings = Spec<BearingSpec>(PartCategory.MainBearings);
        RodBearings = Spec<BearingSpec>(PartCategory.RodBearings);
        Rods = Spec<ConnectingRodSpec>(PartCategory.ConnectingRods);
        Pistons = Spec<PistonSpec>(PartCategory.Pistons);
        HeadGasket = Spec<HeadGasketSpec>(PartCategory.HeadGasket);
        Head = Spec<CylinderHeadSpec>(PartCategory.CylinderHead);
        Cams = Spec<CamshaftSpec>(PartCategory.Camshafts);
        Springs = Spec<ValveSpringSpec>(PartCategory.ValveSprings);
        Intake = Spec<IntakeManifoldSpec>(PartCategory.IntakeManifold);
        Throttle = Spec<ThrottleBodySpec>(PartCategory.ThrottleBody);
        Injectors = Spec<InjectorSpec>(PartCategory.Injectors);
        FuelPump = Spec<FuelPumpSpec>(PartCategory.FuelPump);
        ExhaustManifold = Spec<ExhaustManifoldSpec>(PartCategory.ExhaustManifold);
        Exhaust = Spec<ExhaustSpec>(PartCategory.Exhaust);
        OilPump = Spec<OilPumpSpec>(PartCategory.OilPump);
        OilPan = Spec<OilPanSpec>(PartCategory.OilPan);
        Radiator = Spec<RadiatorSpec>(PartCategory.Radiator);
        Flywheel = Spec<FlywheelSpec>(PartCategory.Flywheel);
        Ecu = Spec<EcuSpec>(PartCategory.Ecu);
        Turbo = assembly.SpecOf<TurbochargerSpec>(PartCategory.Turbocharger);
        Intercooler = assembly.SpecOf<IntercoolerSpec>(PartCategory.Intercooler);
        IntercoolerCdA = Intercooler == null ? 0.0 : CompressibleFlow.EffectiveAreaFromCfm(Intercooler.FlowCfm);

        // Air path restrictions (effective flow areas).
        IntakeCdA = CompressibleFlow.EffectiveAreaFromCfm(Intake.FlowCfm);
        ThrottleCdA = CompressibleFlow.EffectiveAreaFromCfm(Throttle.FlowCfm);
        ExhaustManifoldCdA = CompressibleFlow.EffectiveAreaFromCfm(ExhaustManifold.FlowCfm);
        ExhaustSystemCdA = CompressibleFlow.EffectiveAreaFromCfm(Exhaust.FlowCfm);
        IntakePortCdA = CompressibleFlow.EffectiveAreaFromCfm(MeanFlowOverLiftProfile(Head.IntakeFlowCurve(), Cams.IntakeLiftMm));
        ExhaustPortCdA = CompressibleFlow.EffectiveAreaFromCfm(MeanFlowOverLiftProfile(Head.ExhaustFlowCurve(), Cams.ExhaustLiftMm));
        IntakeEventFraction = Math.Min(1.0, (Cams.IntakeDurationDeg + ValveEventRampDeg) / 720.0);
        ExhaustEventFraction = Math.Min(1.0, (Cams.ExhaustDurationDeg + ValveEventRampDeg) / 720.0);

        // Volumetric-efficiency tuning.
        _durationTunedPistonSpeed = BaseTunedPistonSpeed + (Cams.IntakeDurationDeg - 220.0) * TunedPistonSpeedPerDeg;
        _runnerTuning = Math.Pow(ReferenceRunnerLength / Intake.RunnerLength, 0.25);
        IntakePhaserRange = Ecu.CamPhaseControl ? Cams.IntakePhaserRangeDeg : 0.0;
        VePeakRpm = VePeakRpmAt(0.0);
        OverlapDeg = Cams.OverlapDeg;
        ScavengingRpm = HeaderTuningConstant / ExhaustManifold.PrimaryLengthMm;
        ScavengingGain = ExhaustManifold.ScavengingGain;
        ExhaustManifoldHeatLoss = ManifoldHeatLossPerPrimaryMm * ExhaustManifold.PrimaryLengthMm * geometry.Cylinders;
        ExhaustPortHeatTransfer = ExhaustPortHeatTransferCoefficient * ExhaustPortWallAreaPerBoreSquared
                                  * geometry.Bore * geometry.Bore * geometry.Cylinders;

        // Rotating inertia: crank + flywheel + rod big ends (⅔ rod) + half the reciprocating mass at crank radius + accessories.
        double r2 = geometry.CrankRadius * geometry.CrankRadius;
        RotatingInertia = Crankshaft.InertiaKgM2 + Flywheel.InertiaKgM2
            + geometry.Cylinders * (geometry.RodMass * 2.0 / 3.0 * r2 + 0.5 * geometry.ReciprocatingMass * r2)
            + 0.020;

        // Thermal capacities, J/K. Coolant ~ 50/50 glycol (ρ 1.05 kg/L, cp 3600 J/kgK); roughly half of the
        // block + head metal follows the coolant temperature.
        double coolantLitres = Block.CoolantCapacityL + Radiator.CoolantCapacityL;
        double metalMass = assembly.FindByCategory(PartCategory.Block)!.Definition.MassKg
                         + assembly.FindByCategory(PartCategory.CylinderHead)!.Definition.MassKg;
        CoolantHeatCapacity = coolantLitres * 1.05 * 3600.0 + 0.5 * metalMass * 900.0;
        OilHeatCapacity = OilPan.CapacityL * 0.88 * 1900.0 + 8000.0;
    }

    public EngineAssembly Assembly { get; }
    public FuelDefinition Fuel { get; }
    public EngineGeometry Geometry { get; }

    public BlockSpec Block { get; }
    public CrankshaftSpec Crankshaft { get; }
    public BearingSpec MainBearings { get; }
    public BearingSpec RodBearings { get; }
    public ConnectingRodSpec Rods { get; }
    public PistonSpec Pistons { get; }
    public HeadGasketSpec HeadGasket { get; }
    public CylinderHeadSpec Head { get; }
    public CamshaftSpec Cams { get; }
    public ValveSpringSpec Springs { get; }
    public IntakeManifoldSpec Intake { get; }
    public ThrottleBodySpec Throttle { get; }
    public InjectorSpec Injectors { get; }
    public FuelPumpSpec FuelPump { get; }
    public ExhaustManifoldSpec ExhaustManifold { get; }
    public ExhaustSpec Exhaust { get; }
    public OilPumpSpec OilPump { get; }
    public OilPanSpec OilPan { get; }
    public RadiatorSpec Radiator { get; }
    public FlywheelSpec Flywheel { get; }
    public EcuSpec Ecu { get; }

    /// <summary>Installed turbocharger, or null for a naturally aspirated build.</summary>
    public TurbochargerSpec? Turbo { get; }

    /// <summary>Installed intercooler, or null.</summary>
    public IntercoolerSpec? Intercooler { get; }

    public double IntercoolerCdA { get; }

    /// <summary>Effective flow areas, m².</summary>
    public double IntakeCdA { get; }
    public double ThrottleCdA { get; }
    public double ExhaustManifoldCdA { get; }
    public double ExhaustSystemCdA { get; }

    /// <summary>Per-cylinder port flow area averaged over the valve-lift profile, m².</summary>
    public double IntakePortCdA { get; }
    public double ExhaustPortCdA { get; }

    /// <summary>Fraction of the 720° cycle each valve is open (advertised duration / 720).</summary>
    public double IntakeEventFraction { get; }
    public double ExhaustEventFraction { get; }

    /// <summary>Speed of peak cam/runner filling with the cams at their installed (park) position, rpm.</summary>
    public double VePeakRpm { get; }

    /// <summary>Valve overlap with the cams at their installed (park) position, crank degrees.</summary>
    public double OverlapDeg { get; }

    /// <summary>
    /// How far the ECU can advance the intake cam, crank degrees: the phaser's range when the ECU can drive it,
    /// otherwise 0 (fixed cams, or a phaser the ECU cannot control, stay at their installed position).
    /// </summary>
    public double IntakePhaserRange { get; }

    private readonly double _durationTunedPistonSpeed, _runnerTuning;

    /// <summary>
    /// Crank degrees the intake closes later than the correlation's reference installation, with the intake cam
    /// advanced <paramref name="intakeAdvanceDeg"/> from its installed centreline (0 for straight-up cams).
    /// </summary>
    public double IntakeClosingShiftDeg(double intakeAdvanceDeg) => Cams.HasCenterlines
        ? Cams.InstalledIntakeCenterlineDeg - intakeAdvanceDeg - ReferenceIntakeCenterlineDeg
        : 0.0;

    /// <summary>Tuned mean piston speed of the cam/runner filling peak (before the <see cref="MinTunedPistonSpeed"/> guard), m/s.</summary>
    public double TunedPistonSpeed(double intakeAdvanceDeg) =>
        _durationTunedPistonSpeed + IntakeClosingShiftDeg(intakeAdvanceDeg) * TunedPistonSpeedPerCenterlineDeg;

    /// <summary>Speed of peak cam/runner filling with the intake cam advanced <paramref name="intakeAdvanceDeg"/>, rpm.</summary>
    public double VePeakRpmAt(double intakeAdvanceDeg)
    {
        double tunedPistonSpeed = Math.Max(MinTunedPistonSpeed, TunedPistonSpeed(intakeAdvanceDeg));
        double camPeakRpm = tunedPistonSpeed * 60.0 / (2.0 * Geometry.Stroke);
        return camPeakRpm * _runnerTuning;
    }

    /// <summary>Valve overlap with the intake cam advanced <paramref name="intakeAdvanceDeg"/>, crank degrees.</summary>
    public double OverlapAt(double intakeAdvanceDeg) => Cams.OverlapAt(intakeAdvanceDeg);
    public double ScavengingRpm { get; }
    public double ScavengingGain { get; }

    /// <summary>Heat-loss conductance of the exhaust manifold to the surroundings, W/K.</summary>
    public double ExhaustManifoldHeatLoss { get; }

    /// <summary>Heat-transfer conductance between the exhaust gas and the coolant-jacketed port walls, W/K.</summary>
    public double ExhaustPortHeatTransfer { get; }

    /// <summary>Engine rotating inertia seen at the crank, kg·m².</summary>
    public double RotatingInertia { get; }

    public double CoolantHeatCapacity { get; }
    public double OilHeatCapacity { get; }

    public PartInstance Part(string category) =>
        Assembly.FindByCategory(category) ?? throw new InvalidOperationException($"No {category} installed.");

    private T Spec<T>(string category) where T : PartSpec => Part(category).Spec<T>();

    /// <summary>
    /// Average flow-bench CFM over a harmonic valve-lift event of peak <paramref name="maxLiftMm"/>.
    /// The curve is treated as passing through (0, 0).
    /// </summary>
    public static double MeanFlowOverLiftProfile(Curve1D flowByLiftMm, double maxLiftMm)
    {
        double sum = 0.0;
        for (int i = 0; i < LiftProfileSamples; i++)
        {
            double phase = (i + 0.5) / LiftProfileSamples;
            double lift = maxLiftMm * 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * phase));
            double flow = lift < flowByLiftMm.MinX
                ? flowByLiftMm.Evaluate(flowByLiftMm.MinX) * lift / flowByLiftMm.MinX
                : flowByLiftMm.Evaluate(lift);
            sum += flow;
        }
        return sum / LiftProfileSamples;
    }

    /// <summary>
    /// Validates the assembly and, if it can run, builds a configuration.
    /// </summary>
    public static ConfigurationResult Build(EngineAssembly assembly, FuelDefinition fuel, ValidationContext? context = null)
    {
        var report = AssemblyValidator.Validate(assembly, context);
        if (!report.CanRun) return new ConfigurationResult(null, report);
        var geometry = EngineGeometry.TryCreate(assembly, out _)!;
        return new ConfigurationResult(new EngineConfiguration(assembly, fuel, geometry), report);
    }
}

public sealed record ConfigurationResult(EngineConfiguration? Configuration, ValidationReport Report)
{
    public bool Success => Configuration != null;

    public EngineConfiguration GetOrThrow() => Configuration ??
        throw new InvalidOperationException("Engine cannot run:\n" + string.Join("\n", Report.Errors));
}
