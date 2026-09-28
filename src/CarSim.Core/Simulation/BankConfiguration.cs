using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Simulation;

/// <summary>
/// One bank's air path, derived once from the parts that serve it: its cylinder head, camshafts, springs and gasket,
/// and the intake, throttle, intercooler, turbocharger, exhaust manifold and exhaust in front of and behind it.
/// Elements shared with other banks (a common plenum or throttle, one turbocharger, a Y-pipe) are split by
/// cylinders: this bank's effective area of a shared restriction is its cylinder share of the whole, and a shared
/// compressor or turbine is evaluated at the whole device's flow with this bank taking its share of the power. That
/// is exact when every bank sharing an element breathes alike (tested: an engine authored as one bank or as two
/// identical banks gives the same result); with dissimilar banks it approximates the shared element's flow split (see
/// SIMULATION_SPEC.md, "Banks and air paths"). A single-bank engine has share 1 everywhere.
/// </summary>
public sealed class BankConfiguration
{
    internal BankConfiguration(EngineConfiguration engine, int index, TurboConfiguration? turbo)
    {
        var a = engine.Assembly;
        var def = a.Definition;
        var g = engine.Geometry;
        Index = index;
        Definition = def.Banks[index];
        Cylinders = Definition.Cylinders.Count;
        CylinderShare = (double)Cylinders / g.Cylinders;
        Geometry = EngineGeometry.TryCreate(a, index, out _) ?? throw new InvalidOperationException($"Incomplete geometry for {Definition}.");

        PartInstance Need(string category) => a.PartFor(category, index)
            ?? throw new InvalidOperationException($"No {category} installed for {Definition}.");
        double ShareOf(string category) => (double)Cylinders / def.CylindersServedBy(a.SlotFor(category, index)!);

        HeadGasketPart = Need(PartCategory.HeadGasket);
        HeadPart = Need(PartCategory.CylinderHead);
        SpringsPart = Need(PartCategory.ValveSprings);
        CamsPart = Need(PartCategory.Camshafts);
        HeadGasket = HeadGasketPart.Spec<HeadGasketSpec>();
        Head = HeadPart.Spec<CylinderHeadSpec>();
        Springs = SpringsPart.Spec<ValveSpringSpec>();
        Cams = CamsPart.Spec<CamshaftSpec>();
        Intake = Need(PartCategory.IntakeManifold).Spec<IntakeManifoldSpec>();
        Throttle = Need(PartCategory.ThrottleBody).Spec<ThrottleBodySpec>();
        ExhaustManifold = Need(PartCategory.ExhaustManifold).Spec<ExhaustManifoldSpec>();
        Exhaust = Need(PartCategory.Exhaust).Spec<ExhaustSpec>();

        // Air path restrictions (effective flow areas; this bank's share of shared ones).
        IntakeCdA = CompressibleFlow.EffectiveAreaFromCfm(Intake.FlowCfm) * ShareOf(PartCategory.IntakeManifold);
        ThrottleCdA = CompressibleFlow.EffectiveAreaFromCfm(Throttle.FlowCfm) * ShareOf(PartCategory.ThrottleBody);
        ExhaustManifoldCdA = CompressibleFlow.EffectiveAreaFromCfm(ExhaustManifold.FlowCfm) * ShareOf(PartCategory.ExhaustManifold);
        ExhaustSystemCdA = CompressibleFlow.EffectiveAreaFromCfm(Exhaust.FlowCfm) * ShareOf(PartCategory.Exhaust);
        Turbo = turbo;
        TurboShare = turbo == null ? 0.0 : (double)Cylinders / turbo.Cylinders;
        if (a.PartFor(PartCategory.Intercooler, index) is { } intercooler)
        {
            Intercooler = intercooler.Spec<IntercoolerSpec>();
            IntercoolerShare = ShareOf(PartCategory.Intercooler);
            IntercoolerCdA = CompressibleFlow.EffectiveAreaFromCfm(Intercooler.FlowCfm) * IntercoolerShare;
        }

        // Cam profiles (a variable-lift camshaft has two), runner stages (a variable intake has two or more).
        Profiles = new[] { new CamProfileConfiguration(Head, Cams.Profile(false)), new CamProfileConfiguration(Head, Cams.Profile(true)) };
        RunnerStages = Intake.AllStages().Select(s => new RunnerStageConfiguration(s, Geometry)).ToArray();
        IntakePhaserRange = engine.Ecu.CamPhaseControl ? Cams.IntakePhaserRangeDeg : 0.0;
        HasVariableLift = Cams.HasVariableLift && engine.Ecu.ValveLiftControl;
        HasSwitchedRunner = RunnerStages.Count > 1 && engine.Ecu.IntakeRunnerControl;
        _stroke = g.Stroke;
        VePeakRpm = VePeakRpmAt(0.0);
        ScavengingRpm = EngineConfiguration.HeaderTuningConstant / ExhaustManifold.PrimaryLengthMm;
        ScavengingGain = ExhaustManifold.ScavengingGain;
        ExhaustManifoldHeatLoss = EngineConfiguration.ManifoldHeatLossPerPrimaryMm * ExhaustManifold.PrimaryLengthMm * Cylinders;
        ExhaustPortHeatTransfer = EngineConfiguration.ExhaustPortHeatTransferCoefficient * EngineConfiguration.ExhaustPortWallAreaPerBoreSquared
                                  * g.Bore * g.Bore * Cylinders;
        SweptVolume = g.SweptVolumePerCylinder * Cylinders;
    }

    private readonly double _stroke;

    /// <summary>Position among the engine's banks (0-based, declaration order).</summary>
    public int Index { get; }
    public EngineBankDefinition Definition { get; }

    /// <summary>Cylinders in this bank.</summary>
    public int Cylinders { get; }

    /// <summary>This bank's share of the engine's cylinders (the weight of its per-cylinder quantities in engine means).</summary>
    public double CylinderShare { get; }

    /// <summary>Swept volume of this bank's cylinders, m³.</summary>
    public double SweptVolume { get; }

    /// <summary>This bank's geometry: the shared bottom end with its own gasket and head (its compression ratio and quench).</summary>
    public EngineGeometry Geometry { get; }

    public PartInstance HeadGasketPart { get; }
    public PartInstance HeadPart { get; }
    public PartInstance SpringsPart { get; }
    public PartInstance CamsPart { get; }

    public HeadGasketSpec HeadGasket { get; }
    public CylinderHeadSpec Head { get; }
    public ValveSpringSpec Springs { get; }
    public CamshaftSpec Cams { get; }
    public IntakeManifoldSpec Intake { get; }
    public ThrottleBodySpec Throttle { get; }
    public ExhaustManifoldSpec ExhaustManifold { get; }
    public ExhaustSpec Exhaust { get; }

    /// <summary>The turbocharger this bank's exhaust drives and its intake draws through, or null (naturally aspirated).</summary>
    public TurboConfiguration? Turbo { get; }

    /// <summary>This bank's share of its turbocharger's flow (1 for a turbo per bank, ½ for one turbo on two equal banks).</summary>
    public double TurboShare { get; }

    public IntercoolerSpec? Intercooler { get; }
    public double IntercoolerShare { get; }

    /// <summary>Effective flow areas, m² (this bank's share of shared elements).</summary>
    public double IntakeCdA { get; }
    public double ThrottleCdA { get; }
    public double IntercoolerCdA { get; }
    public double ExhaustManifoldCdA { get; }
    public double ExhaustSystemCdA { get; }

    /// <summary>[0] the base cam profile, [1] the variable-lift profile (the base again for a single-profile camshaft).</summary>
    public IReadOnlyList<CamProfileConfiguration> Profiles { get; }

    /// <summary>The intake's runner stages: [0] the primary runner, then the switched stages in the ECU's order.</summary>
    public IReadOnlyList<RunnerStageConfiguration> RunnerStages { get; }

    /// <summary>
    /// How far the ECU can advance this bank's intake cam, crank degrees: the phaser's range when the ECU can drive
    /// it, otherwise 0 (fixed cams, or a phaser the ECU cannot control, stay at their installed position).
    /// </summary>
    public double IntakePhaserRange { get; }

    /// <summary>Whether the ECU can switch this bank's camshafts to a high-lift profile.</summary>
    public bool HasVariableLift { get; }

    /// <summary>Whether the ECU can switch this bank's intake manifold between its runner stages.</summary>
    public bool HasSwitchedRunner { get; }

    /// <summary>Speed of peak valve-event filling with the cams at their installed (park) position and on the base profile, rpm.</summary>
    public double VePeakRpm { get; }

    public double ScavengingRpm { get; }
    public double ScavengingGain { get; }

    /// <summary>Heat-loss conductance of this bank's share of the exhaust manifold to the surroundings, W/K.</summary>
    public double ExhaustManifoldHeatLoss { get; }

    /// <summary>Heat-transfer conductance between the exhaust gas and this bank's coolant-jacketed port walls, W/K.</summary>
    public double ExhaustPortHeatTransfer { get; }

    /// <summary>
    /// Crank degrees the intake closes later than the correlation's reference installation, with the intake cam
    /// advanced <paramref name="intakeAdvanceDeg"/> from its installed centreline (0 for straight-up cams).
    /// </summary>
    public double IntakeClosingShiftDeg(double intakeAdvanceDeg) => Cams.HasCenterlines
        ? Cams.InstalledIntakeCenterlineDeg - intakeAdvanceDeg - EngineConfiguration.ReferenceIntakeCenterlineDeg
        : 0.0;

    /// <summary>
    /// Tuned mean piston speed of the valve-event filling peak (before the <see cref="EngineConfiguration.MinTunedPistonSpeed"/>
    /// guard), m/s, on cam profile <paramref name="profile"/> (0 base, 1 high lift).
    /// </summary>
    public double TunedPistonSpeed(double intakeAdvanceDeg, int profile = 0) =>
        Profiles[profile].DurationTunedPistonSpeed + IntakeClosingShiftDeg(intakeAdvanceDeg) * EngineConfiguration.TunedPistonSpeedPerCenterlineDeg;

    /// <summary>
    /// Speed of peak valve-event filling, rpm, with the intake cam advanced, on a cam profile. The runner is not in it: its
    /// gas dynamics are a separate gain (<see cref="RunnerStageConfiguration.TunedRpm"/>) that cam timing cannot move.
    /// </summary>
    public double VePeakRpmAt(double intakeAdvanceDeg, int profile = 0)
    {
        double tunedPistonSpeed = Math.Max(EngineConfiguration.MinTunedPistonSpeed, TunedPistonSpeed(intakeAdvanceDeg, profile));
        return tunedPistonSpeed * 60.0 / (2.0 * _stroke);
    }

    /// <summary>Valve overlap with the intake cam advanced <paramref name="intakeAdvanceDeg"/>, on a cam profile, crank degrees.</summary>
    public double OverlapAt(double intakeAdvanceDeg, int profile = 0) => Cams.OverlapAt(intakeAdvanceDeg, Profiles[profile].Spec);

    /// <summary>Valve-float speed of this bank's valvetrain on a cam profile, with the springs' wear.</summary>
    public double ValveFloatRpm(int profile = 0) =>
        ValvetrainModel.FloatRpm(Profiles[profile].Spec, Springs, Head, SpringsPart.Wear);

    public override string ToString() => Definition.ToString();
}

/// <summary>What one cam profile gives a bank's air path: port flow over its lift, event lengths and the filling correlation.</summary>
public sealed class CamProfileConfiguration
{
    internal CamProfileConfiguration(CylinderHeadSpec head, CamProfileSpec profile)
    {
        Spec = profile;
        IntakePortCdA = CompressibleFlow.EffectiveAreaFromCfm(EngineConfiguration.MeanFlowOverLiftProfile(head.IntakeFlowCurve(), profile.IntakeLiftMm));
        ExhaustPortCdA = CompressibleFlow.EffectiveAreaFromCfm(EngineConfiguration.MeanFlowOverLiftProfile(head.ExhaustFlowCurve(), profile.ExhaustLiftMm));
        IntakeEventFraction = Math.Min(1.0, (profile.IntakeDurationDeg + EngineConfiguration.ValveEventRampDeg) / 720.0);
        ExhaustEventFraction = Math.Min(1.0, (profile.ExhaustDurationDeg + EngineConfiguration.ValveEventRampDeg) / 720.0);
        DurationTunedPistonSpeed = EngineConfiguration.BaseTunedPistonSpeed + (profile.IntakeDurationDeg - 220.0) * EngineConfiguration.TunedPistonSpeedPerDeg;
    }

    public CamProfileSpec Spec { get; }

    /// <summary>Per-cylinder port flow area averaged over the valve-lift profile, m².</summary>
    public double IntakePortCdA { get; }
    public double ExhaustPortCdA { get; }

    /// <summary>Fraction of the 720° cycle each valve is open (advertised duration / 720).</summary>
    public double IntakeEventFraction { get; }
    public double ExhaustEventFraction { get; }

    /// <summary>Tuned mean piston speed from the intake duration alone (straight-up cams), m/s.</summary>
    public double DurationTunedPistonSpeed { get; }
}

/// <summary>
/// One runner stage of a bank's intake and its acoustic mode (<see cref="IntakeGasDynamics"/>): the runner with the
/// cylinder volume behind the open valve, solved once here. Per bank: the cylinder volume is the bank's own.
/// </summary>
public sealed class RunnerStageConfiguration
{
    /// <summary>Runner diameter assumed when the part states none, as a fraction of the bore (assumption A5).</summary>
    public const double DefaultDiameterPerBore = 0.40;

    /// <summary>A runner stage of <paramref name="spec"/>'s geometry on a bank of <paramref name="bankGeometry"/> (also for tools that try geometry out).</summary>
    public RunnerStageConfiguration(IntakeStageSpec spec, EngineGeometry bankGeometry)
    {
        Spec = spec;
        Length = Common.Units.MmToM(spec.RunnerLengthMm);
        DefaultDiameter = spec.RunnerDiameterMm == null;
        Diameter = spec.RunnerDiameterMm is double d ? Common.Units.MmToM(d) : DefaultDiameterPerBore * bankGeometry.Bore;
        Area = Math.PI * Diameter * Diameter / 4.0;
        EffectiveLength = Length + IntakeGasDynamics.EndCorrection * Diameter / 2.0;
        EffectiveCylinderVolume = bankGeometry.ClearanceVolume + bankGeometry.SweptVolumePerCylinder / 2.0;
        VolumeRatio = Area * EffectiveLength / EffectiveCylinderVolume;
        Fundamental = IntakeGasDynamics.Fundamental(VolumeRatio);
        TunedRpmAtReference = IntakeGasDynamics.TunedRpm(Fundamental, EffectiveLength, IntakeGasDynamics.ReferenceSpeedOfSound);
    }

    public IntakeStageSpec Spec { get; }

    /// <summary>Acoustic length (runner mouth to valve seat), m.</summary>
    public double Length { get; }

    /// <summary>Mean runner diameter, m (the model default when the part states none).</summary>
    public double Diameter { get; }

    /// <summary>Whether <see cref="Diameter"/> is the model default (0.40 × bore) rather than part data.</summary>
    public bool DefaultDiameter { get; }

    /// <summary>Runner cross-section A_r, m².</summary>
    public double Area { get; }

    /// <summary>L_eff = L + δ·r_r, the acoustic length with the plenum mouth's end correction, m.</summary>
    public double EffectiveLength { get; }

    /// <summary>V_eff = V_c + V_d/2 per cylinder: the cylinder's mean volume over the intake stroke (assumption A8), m³.</summary>
    public double EffectiveCylinderVolume { get; }

    /// <summary>β = A_r·L_eff/V_eff, the runner's volume over the cylinder's.</summary>
    public double VolumeRatio { get; }

    /// <summary>x, the root of x·tan x = β.</summary>
    public double Fundamental { get; }

    /// <summary>Tuned speed with the runner gas at <see cref="IntakeGasDynamics.ReferenceTemperature"/>, rpm.</summary>
    public double TunedRpmAtReference { get; }

    /// <summary>Tuned speed with the runner gas at <paramref name="runnerGasTemperature"/> (K), rpm: ∝ the speed of sound.</summary>
    public double TunedRpm(double runnerGasTemperature) =>
        TunedRpmAtReference * (IntakeGasDynamics.SpeedOfSound(runnerGasTemperature) / IntakeGasDynamics.ReferenceSpeedOfSound);
}

/// <summary>One installed turbocharger: its spec, part and the banks it serves (their exhaust drives it, their intake draws through it).</summary>
public sealed class TurboConfiguration
{
    internal TurboConfiguration(int index, EngineSlotDefinition slot, PartInstance part, IReadOnlyList<int> banks, int cylinders)
    {
        Index = index;
        Slot = slot;
        Part = part;
        Spec = part.Spec<TurbochargerSpec>();
        Banks = banks;
        Cylinders = cylinders;
    }

    public int Index { get; }
    public EngineSlotDefinition Slot { get; }
    public PartInstance Part { get; }
    public TurbochargerSpec Spec { get; }

    /// <summary>Indices of the banks this turbo serves.</summary>
    public IReadOnlyList<int> Banks { get; }

    /// <summary>Cylinders feeding and fed by this turbo.</summary>
    public int Cylinders { get; }

    public override string ToString() => $"{Part.Definition.Name} ({Slot.Label})";
}
