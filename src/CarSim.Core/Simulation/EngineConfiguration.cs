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

    /// <summary>Header tuning constant: tuned rpm ≈ K / primary length (mm).</summary>
    public const double HeaderTuningConstant = 5.2e6;

    /// <summary>
    /// Exhaust-manifold heat loss to the engine bay per mm of primary pipe per cylinder, W/K (a bare steel
    /// primary of ≈ 40 mm diameter in underbonnet airflow). A 250 mm log manifold on four cylinders loses
    /// ≈ 7.5 W/K; an 820 mm tubular header ≈ 25 W/K.
    /// </summary>
    public const double ManifoldHeatLossPerPrimaryMm = 0.0075;

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
        double tunedPistonSpeed = BaseTunedPistonSpeed + (Cams.IntakeDurationDeg - 220.0) * TunedPistonSpeedPerDeg;
        double camPeakRpm = tunedPistonSpeed * 60.0 / (2.0 * geometry.Stroke);
        VePeakRpm = camPeakRpm * Math.Pow(ReferenceRunnerLength / Intake.RunnerLength, 0.25);
        OverlapDeg = Cams.OverlapDeg;
        ScavengingRpm = HeaderTuningConstant / ExhaustManifold.PrimaryLengthMm;
        ScavengingGain = ExhaustManifold.ScavengingGain;
        ExhaustManifoldHeatLoss = ManifoldHeatLossPerPrimaryMm * ExhaustManifold.PrimaryLengthMm * geometry.Cylinders;

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

    public double VePeakRpm { get; }
    public double OverlapDeg { get; }
    public double ScavengingRpm { get; }
    public double ScavengingGain { get; }

    /// <summary>Heat-loss conductance of the exhaust manifold to the surroundings, W/K.</summary>
    public double ExhaustManifoldHeatLoss { get; }

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
