using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Engines;

/// <summary>
/// What an assembled engine can do, resolved from its architecture and installed parts: a summary for inspection,
/// the workshop, the CLI and tests. It is derived, never authored, and the simulation does not branch on it — the
/// physics reads the same part data these flags are resolved from (a phaser range, a second cam profile, a switched
/// runner length, a turbocharger), so a capability exists exactly when some part provides it. See
/// ENGINE_AUTHORING_GUIDE.md, "Capabilities".
/// </summary>
public sealed record EngineCapabilities
{
    public required string Layout { get; init; }
    public required int Cylinders { get; init; }
    public required int Banks { get; init; }
    public double? BankAngleDeg { get; init; }
    public IReadOnlyList<int> FiringOrder { get; init; } = Array.Empty<int>();

    /// <summary>Valvetrain types fitted (one per distinct head/cam pair: "dohc", "sohc", "ohv").</summary>
    public required IReadOnlyList<string> Valvetrains { get; init; }

    /// <summary>Valves per cylinder of the heads fitted.</summary>
    public required IReadOnlyList<int> ValvesPerCylinder { get; init; }

    /// <summary>Intake cam phasers fitted, and whether the ECU can drive them.</summary>
    public bool IntakeCamPhasers { get; init; }
    public bool IntakeCamPhasing { get; init; }

    /// <summary>Two-profile (variable-lift) camshafts fitted, and whether the ECU can switch them.</summary>
    public bool VariableLiftCams { get; init; }
    public bool VariableValveLift { get; init; }

    /// <summary>A variable intake manifold fitted, and whether the ECU can switch it.</summary>
    public bool SwitchedRunners { get; init; }
    public bool VariableIntakeRunner { get; init; }

    public int Turbochargers { get; init; }
    public bool Intercooled { get; init; }

    /// <summary>Distinct intake paths (banks drawing through different manifolds, throttles, compressors or coolers).</summary>
    public int IntakePaths { get; init; }

    /// <summary>Distinct exhaust paths (banks exhausting through different manifolds, turbines or systems).</summary>
    public int ExhaustPaths { get; init; }

    public bool ForcedInduction => Turbochargers > 0;

    /// <summary>Short labels, e.g. "V8 (90°)", "2 banks", "OHV", "2 valves/cyl", "cam phasing", "twin turbo".</summary>
    public IReadOnlyList<string> Describe()
    {
        var d = new List<string>
        {
            Layout switch
            {
                "v" => $"V{Cylinders}" + (BankAngleDeg is double a ? $" ({a:F0}°)" : ""),
                "flat" => $"flat-{Cylinders}",
                _ => $"inline-{Cylinders}",
            },
        };
        if (Banks > 1) d.Add($"{Banks} banks");
        d.AddRange(Valvetrains.Select(ValvetrainTypes.Describe));
        d.AddRange(ValvesPerCylinder.Select(v => $"{v} valves/cyl"));
        if (IntakeCamPhasers) d.Add(IntakeCamPhasing ? "intake cam phasing" : "intake cam phasers (not driven by the ECU)");
        if (VariableLiftCams) d.Add(VariableValveLift ? "variable valve lift" : "variable-lift cams (not switched by the ECU)");
        if (SwitchedRunners) d.Add(VariableIntakeRunner ? "variable intake runner" : "variable intake (not switched by the ECU)");
        d.Add(Turbochargers switch { 0 => "naturally aspirated", 1 => "turbocharged", 2 => "twin turbo", var n => $"{n} turbos" });
        if (Intercooled) d.Add("intercooled");
        if (IntakePaths > 1) d.Add($"{IntakePaths} intake paths");
        if (ExhaustPaths > 1) d.Add($"{ExhaustPaths} exhaust paths");
        if (FiringOrder.Count > 0) d.Add("firing order " + string.Join("-", FiringOrder));
        return d;
    }

    /// <summary>Resolves the capabilities of an assembly (complete or not) from its data.</summary>
    public static EngineCapabilities Resolve(EngineAssembly a)
    {
        var def = a.Definition;
        var ecu = a.SpecOf<EcuSpec>(PartCategory.Ecu);
        var cams = a.PartsOf(PartCategory.Camshafts).Select(p => p.Part.Spec<CamshaftSpec>()).ToList();
        var heads = a.PartsOf(PartCategory.CylinderHead).Select(p => p.Part.Spec<CylinderHeadSpec>()).ToList();
        var intakes = a.PartsOf(PartCategory.IntakeManifold).Select(p => p.Part.Spec<IntakeManifoldSpec>()).ToList();
        bool phasers = cams.Any(c => c.IntakePhaserRangeDeg > 0);
        bool lift = cams.Any(c => c.HasVariableLift);
        bool runners = intakes.Any(i => i.HasSwitchedStages);

        string Chain(int bank, params string[] categories) =>
            string.Join("|", categories.Select(c => a.SlotFor(c, bank)?.Id ?? "-"));
        var intakePaths = Enumerable.Range(0, def.Banks.Count)
            .Select(b => Chain(b, PartCategory.IntakeManifold, PartCategory.ThrottleBody, PartCategory.Turbocharger, PartCategory.Intercooler)).Distinct().Count();
        var exhaustPaths = Enumerable.Range(0, def.Banks.Count)
            .Select(b => Chain(b, PartCategory.ExhaustManifold, PartCategory.Turbocharger, PartCategory.Exhaust)).Distinct().Count();

        return new EngineCapabilities
        {
            Layout = def.Layout,
            Cylinders = def.Cylinders,
            Banks = def.Banks.Count,
            BankAngleDeg = def.BankAngleDeg,
            FiringOrder = def.FiringOrder,
            Valvetrains = heads.Select(h => h.Valvetrain).Concat(cams.Select(c => c.Valvetrain)).Distinct().ToList(),
            ValvesPerCylinder = heads.Select(h => h.ValvesPerCylinder).Distinct().ToList(),
            IntakeCamPhasers = phasers,
            IntakeCamPhasing = phasers && ecu is { CamPhaseControl: true },
            VariableLiftCams = lift,
            VariableValveLift = lift && ecu is { ValveLiftControl: true },
            SwitchedRunners = runners,
            VariableIntakeRunner = runners && ecu is { IntakeRunnerControl: true },
            Turbochargers = a.PartsOf(PartCategory.Turbocharger).Count(),
            Intercooled = a.PartsOf(PartCategory.Intercooler).Any(),
            IntakePaths = intakePaths,
            ExhaustPaths = exhaustPaths,
        };
    }
}
