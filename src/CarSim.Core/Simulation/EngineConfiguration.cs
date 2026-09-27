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

    /// <summary>
    /// v₀, the mean piston speed at which valve-event filling peaks for a 220° (at 1 mm) intake cam, m/s. Fitted: re-anchored
    /// with <see cref="AirPath.VeCeiling"/> on the stock K20's full-load air per cycle (least squares over its 25 anchor
    /// points, Intake Gas Dynamics 2.0); it was 15.0 while the runner's effect was folded into the same hump.
    /// </summary>
    public const double BaseTunedPistonSpeed = 13.64;

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
        Injectors = Spec<InjectorSpec>(PartCategory.Injectors);
        FuelPump = Spec<FuelPumpSpec>(PartCategory.FuelPump);
        OilPump = Spec<OilPumpSpec>(PartCategory.OilPump);
        OilPan = Spec<OilPanSpec>(PartCategory.OilPan);
        Radiator = Spec<RadiatorSpec>(PartCategory.Radiator);
        Flywheel = Spec<FlywheelSpec>(PartCategory.Flywheel);
        Ecu = Spec<EcuSpec>(PartCategory.Ecu);

        // One turbocharger configuration per installed turbo (it may serve several banks), then one air path per bank.
        var def = assembly.Definition;
        var turbos = new List<TurboConfiguration>();
        foreach (var (slot, part) in assembly.PartsOf(PartCategory.Turbocharger))
            turbos.Add(new TurboConfiguration(turbos.Count, slot, part, def.BanksServedBy(slot), def.CylindersServedBy(slot)));
        Turbos = turbos;
        var banks = new BankConfiguration[def.Banks.Count];
        for (int b = 0; b < banks.Length; b++)
            banks[b] = new BankConfiguration(this, b, turbos.FirstOrDefault(t => t.Banks.Contains(b)));
        Banks = banks;
        Capabilities = EngineCapabilities.Resolve(assembly);

        // Rotating inertia: crank + flywheel + rod big ends (⅔ rod) + half the reciprocating mass at crank radius + accessories.
        double r2 = geometry.CrankRadius * geometry.CrankRadius;
        RotatingInertia = Crankshaft.InertiaKgM2 + Flywheel.InertiaKgM2
            + geometry.Cylinders * (geometry.RodMass * 2.0 / 3.0 * r2 + 0.5 * geometry.ReciprocatingMass * r2)
            + 0.020;

        // Thermal capacities, J/K. Coolant ~ 50/50 glycol (ρ 1.05 kg/L, cp 3600 J/kgK); roughly half of the
        // block + head metal follows the coolant temperature.
        double coolantLitres = Block.CoolantCapacityL + Radiator.CoolantCapacityL;
        double headsMass = 0.0;
        foreach (var (_, head) in assembly.PartsOf(PartCategory.CylinderHead)) headsMass += head.Definition.MassKg;
        double metalMass = assembly.FindByCategory(PartCategory.Block)!.Definition.MassKg + headsMass;
        CoolantHeatCapacity = coolantLitres * 1.05 * 3600.0 + 0.5 * metalMass * 900.0;
        OilHeatCapacity = OilPan.CapacityL * 0.88 * 1900.0 + 8000.0;
    }

    public EngineAssembly Assembly { get; }
    public FuelDefinition Fuel { get; }
    /// <summary>
    /// The engine's geometry: bore, stroke, displacement and the reciprocating parts are the bottom end's and hold for every
    /// bank. Values set by a head or gasket (compression ratio, quench, clearance) are the first bank's here; read them
    /// from <see cref="BankConfiguration.Geometry"/> — a V engine may run different heads or gaskets per bank.
    /// </summary>
    public EngineGeometry Geometry { get; }

    // Engine-wide parts (one for the whole engine; see EngineTopology.EngineWideCategories).
    public BlockSpec Block { get; }
    public CrankshaftSpec Crankshaft { get; }
    public BearingSpec MainBearings { get; }
    public BearingSpec RodBearings { get; }
    public ConnectingRodSpec Rods { get; }
    public PistonSpec Pistons { get; }
    public InjectorSpec Injectors { get; }
    public FuelPumpSpec FuelPump { get; }
    public OilPumpSpec OilPump { get; }
    public OilPanSpec OilPan { get; }
    public RadiatorSpec Radiator { get; }
    public FlywheelSpec Flywheel { get; }
    public EcuSpec Ecu { get; }

    /// <summary>
    /// One air path per bank: the head, cams, springs, gasket, intake, throttle, exhaust manifold and exhaust serving
    /// it, and its share of anything it shares with other banks.
    /// </summary>
    public IReadOnlyList<BankConfiguration> Banks { get; }

    /// <summary>Installed turbochargers (empty for a naturally aspirated engine), each with its own shaft.</summary>
    public IReadOnlyList<TurboConfiguration> Turbos { get; }

    /// <summary>What the installed parts let this engine do (a derived summary; the simulation reads the parts).</summary>
    public EngineCapabilities Capabilities { get; }

    /// <summary>Engine rotating inertia seen at the crank, kg·m².</summary>
    public double RotatingInertia { get; }

    public double CoolantHeatCapacity { get; }
    public double OilHeatCapacity { get; }

    /// <summary>The installed part of an engine-wide category (see <see cref="EngineTopology.EngineWideCategories"/>).</summary>
    public PartInstance Part(string category) =>
        Assembly.FindByCategory(category) ?? throw new InvalidOperationException($"No {category} installed.");

    private T Spec<T>(string category) where T : PartSpec => Part(category).Spec<T>();

    /// <summary>The bank configuration that <paramref name="part"/> serves first (a bank-scoped part), or null.</summary>
    public BankConfiguration? BankOf(PartInstance part)
    {
        var slot = Assembly.SlotOf(part);
        if (slot == null) return null;
        var served = Assembly.Definition.BanksServedBy(slot);
        return served.Count > 0 ? Banks[served[0]] : null;
    }

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
