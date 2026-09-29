using System.Globalization;
using CarSim.Core.Common;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;

namespace CarSim.Core.Engines;

/// <summary>
/// What an entry of <see cref="EngineDerivedValues"/> is. The chain is AUTHORITATIVE CONTENT → DERIVED ENGINE PROPERTY →
/// CONSISTENCY CHECK: an input is authored (or left to a model default), a property is derived from inputs, and an
/// authored claim about a property (a tune's displacement belief) is validated against it — never a second source of
/// the same value.
/// </summary>
public enum DerivedValueStatus
{
    /// <summary>An input stated in the content: a part's spec field, the family's architecture, a tune's rev limit.</summary>
    Authored,

    /// <summary>An input the content leaves to a model default (e.g. the runner diameter, 0.40 × bore).</summary>
    Defaulted,

    /// <summary>Computed from the inputs by the model's own equations (<see cref="EngineGeometry"/>, <see cref="RunnerStageConfiguration"/>).</summary>
    Derived,

    /// <summary>An authored claim about a derived property (a tune's belief) that agrees with it.</summary>
    Validated,

    /// <summary>An authored claim about a derived property that disagrees with it.</summary>
    Mismatch,

    /// <summary>Not derivable from the content (a part missing, no rev limit, not in the schema); the basis says why.</summary>
    Unavailable,
}

/// <summary>
/// One entry of the derived-values report. <paramref name="Value"/> is in <paramref name="Unit"/> (cc, mm, m/s, …; empty for
/// a ratio) and null when unavailable. <paramref name="Bank"/> and <paramref name="Stage"/> locate per-bank and per-runner-stage
/// values. <paramref name="Basis"/> says where the value comes from (the inputs, the equation, or why it is unavailable).
/// </summary>
public sealed record DerivedValue(string Key, string Label, DerivedValueStatus Status, double? Value, string Unit, string Basis,
    int? Bank = null, int? Stage = null)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The value in its display precision, with its unit ("3941.8 cc", "9.91:1", "—" when unavailable).</summary>
    public string Display => Value is not double v ? "—" : Unit switch
    {
        "cc" or "mm" or "m/s" or "deg" or "L/s" => v.ToString("0.0", Inv) + (Unit == "deg" ? "°" : " " + Unit),
        "rpm" => v.ToString("0", Inv) + " rpm",
        "kg/s" => v.ToString("0.000", Inv) + " kg/s",
        ":1" => v.ToString("0.00", Inv) + ":1",
        "count" => v.ToString("0", Inv),
        _ => v.ToString("0.00", Inv),
    };

    /// <summary>One report line: label, value, status, basis.</summary>
    public string Text => Value == null
        ? $"{Label} — {Status.ToString().ToLowerInvariant()}: {Basis}"
        : $"{Label} {Display} — {Status.ToString().ToLowerInvariant()}: {Basis}";
}

/// <summary>
/// The derived values of an assembled engine (Engine Authoring Factory 1.0, Phase 3): displacement, swept, clearance and
/// cylinder volumes, compression ratio per bank, rod and bore/stroke ratios, mean piston speed and theoretical airflow at a
/// rev limit, the mean firing interval, and each runner stage's tuned speed — with the authored inputs they rest on.
/// <para>
/// This is a view, not a second model: every number is read from <see cref="EngineGeometry"/> or
/// <see cref="RunnerStageConfiguration"/> (the classes the simulation itself builds), so there is one equation per quantity and
/// one source of truth. It is computed on the resolved assembly (variants and swapped parts included) and is deterministic:
/// the same assembly gives the same entries in the same order. Nothing here reads an engine's identity.
/// </para>
/// </summary>
public sealed class EngineDerivedValues
{
    /// <summary>A four-stroke cycle is two revolutions (720° of crank): the model's cycle, for every engine.</summary>
    public const double CycleDegrees = 720.0;

    private EngineDerivedValues(IReadOnlyList<DerivedValue> values) => Values = values;

    /// <summary>Every entry, in report order.</summary>
    public IReadOnlyList<DerivedValue> Values { get; }

    /// <summary>The first entry with <paramref name="key"/> (and bank and stage, when given).</summary>
    public DerivedValue? Find(string key, int? bank = null, int? stage = null) =>
        Values.FirstOrDefault(v => v.Key == key && (bank == null || v.Bank == bank) && (stage == null || v.Stage == stage));

    /// <summary>
    /// The engine's displacement, cc (<see cref="EngineGeometry.Displacement"/> of the installed bottom end), or null when the
    /// geometry is incomplete. The one value a tune's <c>displacement_cc</c> belief is set from and checked against.
    /// </summary>
    public double? DisplacementCc => Find(Keys.Displacement)?.Value;

    /// <summary>These values with <paramref name="checks"/> (validated or mismatched claims about them) appended.</summary>
    public EngineDerivedValues With(IEnumerable<DerivedValue> checks) => new(Values.Concat(checks).ToList());

    /// <summary>Every entry as a report line (<c>--verbose</c>).</summary>
    public IReadOnlyList<string> Lines() => Values.Select(v => v.Text).ToList();

    /// <summary>
    /// A compact report: the derived values grouped on a few lines, every validated or mismatched claim, and how many inputs
    /// are model defaults. Authored inputs are listed only by <see cref="Lines"/>.
    /// </summary>
    public IReadOnlyList<string> Summary()
    {
        var lines = new List<string>();
        string Group(params string[] keys) => string.Join(", ", keys.Select(k => Find(k)).Where(e => e is { Status: DerivedValueStatus.Derived })
            .Select(e => $"{e!.Label} {e.Display}"));
        string engineWide = Group(Keys.Displacement, Keys.SweptVolume, Keys.RodRatio, Keys.BoreStroke);
        lines.Add(engineWide.Length > 0 ? engineWide : $"geometry: unavailable ({Find(Keys.Displacement)?.Basis})");
        foreach (var bank in Values.Where(v => v.Key == Keys.CompressionRatio).Select(v => v.Bank))
        {
            var entries = new[] { Keys.ClearanceVolume, Keys.CylinderVolume, Keys.CompressionRatio }.Select(k => Find(k, bank))
                .Where(e => e is { Status: DerivedValueStatus.Derived }).Select(e => $"{e!.Label} {e.Display}").ToList();
            if (entries.Count > 0) lines.Add(string.Join(", ", entries));
        }
        var limit = Find(Keys.RevLimit);
        string atLimit = Group(Keys.MeanPistonSpeed, Keys.Airflow, Keys.AirMassFlow);
        lines.Add(atLimit.Length > 0 ? $"{atLimit} ({limit!.Display}, {limit.Basis})"
            : $"values at the rev limit: unavailable ({Find(Keys.MeanPistonSpeed)?.Basis})");
        var interval = Find(Keys.FiringInterval);
        lines.Add($"{interval?.Label} {interval?.Display}; {Find(Keys.FiringPhasing)?.Label}: unavailable ({Find(Keys.FiringPhasing)?.Basis})");
        var runners = Values.Where(v => v.Key == Keys.RunnerTunedSpeed).ToList();
        if (runners.Count > 0) lines.Add(string.Join("; ", runners.Select(r => $"{r.Label} {r.Display}")));
        lines.AddRange(Values.Where(v => v.Status is DerivedValueStatus.Validated or DerivedValueStatus.Mismatch).Select(v => v.Text));
        int defaulted = Values.Count(v => v.Status == DerivedValueStatus.Defaulted);
        if (defaulted > 0) lines.Add($"{defaulted} input(s) left to model defaults (--verbose lists every input)");
        return lines;
    }

    /// <summary>Stable keys of the entries (tests and tools address entries by key).</summary>
    public static class Keys
    {
        public const string Cylinders = "cylinders", Bore = "bore_mm", Stroke = "stroke_mm", RevLimit = "rev_limit_rpm";
        public const string Displacement = "displacement_cc", SweptVolume = "swept_volume_cc", ClearanceVolume = "clearance_volume_cc";
        public const string CylinderVolume = "cylinder_volume_cc", CompressionRatio = "compression_ratio", RodRatio = "rod_ratio";
        public const string BoreStroke = "bore_stroke_ratio", MeanPistonSpeed = "mean_piston_speed_ms";
        public const string FiringInterval = "mean_firing_interval_deg", FiringPhasing = "bank_firing_phasing";
        public const string RunnerDiameter = "runner_diameter_mm", RunnerTunedSpeed = "runner_tuned_rpm";
        public const string Airflow = "theoretical_airflow_l_s", AirMassFlow = "theoretical_air_mass_kg_s";
    }

    /// <summary>
    /// Derives the values of <paramref name="assembly"/>. <paramref name="revLimitRpm"/> (a tune's rev limit, an authored
    /// input; <paramref name="revLimitSource"/> names it) is needed for the values at the limit; without it they are
    /// unavailable.
    /// </summary>
    public static EngineDerivedValues Of(EngineAssembly assembly, double? revLimitRpm = null, string revLimitSource = "the tune")
    {
        var inv = CultureInfo.InvariantCulture;
        var def = assembly.Definition;
        var v = new List<DerivedValue>();
        string Id(string category) => assembly.FindByCategory(category)?.Definition.Id ?? "not installed";
        // Rounded for the basis text only (+ 0.0 turns a rounded −0 into 0).
        string Cc(double m3) => (Math.Round(Units.M3ToCc(m3), 2) + 0.0).ToString("0.##", inv);

        // ---- Authored inputs of the engine-wide geometry ----------------------------------------------------------------
        var block = assembly.SpecOf<BlockSpec>(PartCategory.Block);
        var crank = assembly.SpecOf<CrankshaftSpec>(PartCategory.Crankshaft);
        v.Add(block == null
            ? new DerivedValue(Keys.Cylinders, "cylinders (block)", DerivedValueStatus.Unavailable, null, "count", "no block installed")
            : new DerivedValue(Keys.Cylinders, "cylinders (block)", DerivedValueStatus.Authored, block.Cylinders, "count",
                $"block {Id(PartCategory.Block)}.cylinders; the family declares {def.Cylinders}"));
        v.Add(block == null
            ? new DerivedValue(Keys.Bore, "bore", DerivedValueStatus.Unavailable, null, "mm", "no block installed")
            : new DerivedValue(Keys.Bore, "bore", DerivedValueStatus.Authored, block.BoreMm, "mm", $"block {Id(PartCategory.Block)}.bore_mm"));
        v.Add(crank == null
            ? new DerivedValue(Keys.Stroke, "stroke", DerivedValueStatus.Unavailable, null, "mm", "no crankshaft installed")
            : new DerivedValue(Keys.Stroke, "stroke", DerivedValueStatus.Authored, crank.StrokeMm, "mm", $"crankshaft {Id(PartCategory.Crankshaft)}.stroke_mm"));
        v.Add(revLimitRpm is double rev
            ? new DerivedValue(Keys.RevLimit, "rev limit", DerivedValueStatus.Authored, rev, "rpm", $"rev_limit_rpm of {revLimitSource}")
            : new DerivedValue(Keys.RevLimit, "rev limit", DerivedValueStatus.Unavailable, null, "rpm", "no tune states a rev limit"));

        // ---- Engine-wide derived geometry (the bottom end is shared by every bank) ------------------------------------
        var g = EngineGeometry.TryCreate(assembly, 0, out var missing);
        string why = $"geometry incomplete: missing {string.Join(", ", missing)}";
        if (g == null)
        {
            foreach (var (key, label, unit) in new[]
                     {
                         (Keys.Displacement, "displacement", "cc"), (Keys.SweptVolume, "swept volume per cylinder", "cc"),
                         (Keys.RodRatio, "rod ratio (rod / stroke)", ""), (Keys.BoreStroke, "bore / stroke", ""),
                     })
                v.Add(new DerivedValue(key, label, DerivedValueStatus.Unavailable, null, unit, why));
        }
        else
        {
            v.Add(new DerivedValue(Keys.Displacement, "displacement", DerivedValueStatus.Derived, Units.M3ToCc(g.Displacement), "cc",
                string.Create(inv, $"π/4 · bore² · stroke × cylinders = π/4 · {Units.MToMm(g.Bore):0.###}² · {Units.MToMm(g.Stroke):0.###} mm × {g.Cylinders}")));
            v.Add(new DerivedValue(Keys.SweptVolume, "swept volume per cylinder", DerivedValueStatus.Derived, Units.M3ToCc(g.SweptVolumePerCylinder), "cc",
                "π/4 · bore² · stroke"));
            v.Add(new DerivedValue(Keys.RodRatio, "rod ratio (rod / stroke)", DerivedValueStatus.Derived, g.RodRatio, "",
                string.Create(inv, $"connecting rods {Id(PartCategory.ConnectingRods)}: {Units.MToMm(g.RodLength):0.###} mm / {Units.MToMm(g.Stroke):0.###} mm")));
            v.Add(new DerivedValue(Keys.BoreStroke, "bore / stroke", DerivedValueStatus.Derived, g.Bore / g.Stroke, "",
                string.Create(inv, $"{Units.MToMm(g.Bore):0.###} mm / {Units.MToMm(g.Stroke):0.###} mm")));
        }

        // ---- Per bank: each bank's head and gasket close its own chamber ------------------------------------------------
        for (int b = 0; b < def.Banks.Count; b++)
        {
            string on = def.Banks.Count > 1 ? $" (bank '{def.Banks[b].Id}')" : "";
            var bg = EngineGeometry.TryCreate(assembly, b, out var bankMissing);
            if (bg == null)
            {
                string bankWhy = $"geometry incomplete: missing {string.Join(", ", bankMissing)}";
                v.Add(new DerivedValue(Keys.ClearanceVolume, "clearance volume per cylinder" + on, DerivedValueStatus.Unavailable, null, "cc", bankWhy, b));
                v.Add(new DerivedValue(Keys.CylinderVolume, "cylinder volume per cylinder" + on, DerivedValueStatus.Unavailable, null, "cc", bankWhy, b));
                v.Add(new DerivedValue(Keys.CompressionRatio, "compression ratio" + on, DerivedValueStatus.Unavailable, null, ":1", bankWhy, b));
                continue;
            }
            v.Add(new DerivedValue(Keys.ClearanceVolume, "clearance volume per cylinder" + on, DerivedValueStatus.Derived,
                Units.M3ToCc(bg.ClearanceVolume), "cc", string.Create(inv,
                    $"chamber {Cc(bg.ChamberVolume)} + gasket {Cc(bg.GasketVolume)} + deck {Cc(bg.DeckVolume)} + dish {Cc(bg.PistonDishVolume)} cc"),
                b));
            v.Add(new DerivedValue(Keys.CylinderVolume, "cylinder volume per cylinder" + on, DerivedValueStatus.Derived,
                Units.M3ToCc(bg.SweptVolumePerCylinder + bg.ClearanceVolume), "cc", "swept + clearance volume (at bottom dead centre)", b));
            v.Add(new DerivedValue(Keys.CompressionRatio, "compression ratio" + on, DerivedValueStatus.Derived, bg.CompressionRatio, ":1",
                "(swept + clearance) / clearance", b));
        }

        // ---- At the rev limit ------------------------------------------------------------------------------------------
        if (g == null || revLimitRpm is not double limit)
        {
            string noLimit = g == null ? why : "needs a rev limit (no tune states one)";
            v.Add(new DerivedValue(Keys.MeanPistonSpeed, "mean piston speed at the rev limit", DerivedValueStatus.Unavailable, null, "m/s", noLimit));
            v.Add(new DerivedValue(Keys.Airflow, "theoretical airflow at the rev limit (VE 1)", DerivedValueStatus.Unavailable, null, "L/s", noLimit));
            v.Add(new DerivedValue(Keys.AirMassFlow, "theoretical air mass flow at the rev limit (VE 1)", DerivedValueStatus.Unavailable, null, "kg/s", noLimit));
        }
        else
        {
            v.Add(new DerivedValue(Keys.MeanPistonSpeed, "mean piston speed at the rev limit", DerivedValueStatus.Derived, g.MeanPistonSpeed(limit), "m/s",
                string.Create(inv, $"2 · stroke · rpm / 60 at {limit:0} rpm")));
            // Displacement drawn once per cycle of two revolutions (the model's four-stroke cycle), at VE 1.
            double volumeFlow = g.Displacement * limit / 60.0 / 2.0;
            double density = PhysicalConstants.StandardPressure / (PhysicalConstants.AirGasConstant * PhysicalConstants.StandardTemperature);
            v.Add(new DerivedValue(Keys.Airflow, "theoretical airflow at the rev limit (VE 1)", DerivedValueStatus.Derived, volumeFlow * 1000.0, "L/s",
                string.Create(inv, $"displacement × rpm / 2 (four-stroke) at {limit:0} rpm")));
            v.Add(new DerivedValue(Keys.AirMassFlow, "theoretical air mass flow at the rev limit (VE 1)", DerivedValueStatus.Derived, volumeFlow * density, "kg/s",
                string.Create(inv, $"at {Units.PaToKpa(PhysicalConstants.StandardPressure):0.###} kPa, {PhysicalConstants.StandardTemperature:0.##} K (the model's reference ambient)")));
        }

        // ---- Firing: the mean interval follows from the cycle; the phasing of the banks needs the crank, which the schema
        //      does not describe (the mean-value model does not use it) ---------------------------------------------------
        v.Add(def.Cylinders > 0
            ? new DerivedValue(Keys.FiringInterval, "mean firing interval", DerivedValueStatus.Derived, CycleDegrees / def.Cylinders, "deg",
                string.Create(inv, $"{CycleDegrees:0}° crank per four-stroke cycle / {def.Cylinders} cylinders; even or uneven firing depends on the crank-pin phasing"))
            : new DerivedValue(Keys.FiringInterval, "mean firing interval", DerivedValueStatus.Unavailable, null, "deg", "no cylinders"));
        v.Add(new DerivedValue(Keys.FiringPhasing, "firing phasing of the banks", DerivedValueStatus.Unavailable, null, "deg",
            "crank-pin phasing is not in the schema; the mean-value model has no per-cylinder firing events"));

        // ---- Intake: each bank's runner stages and the speed each tunes to (the simulation's own stage model) ------------
        for (int b = 0; b < def.Banks.Count; b++)
        {
            string on = def.Banks.Count > 1 ? $"bank '{def.Banks[b].Id}', " : "";
            var intakePart = assembly.PartFor(PartCategory.IntakeManifold, b);
            var bg = EngineGeometry.TryCreate(assembly, b, out _);
            if (intakePart == null || bg == null)
            {
                v.Add(new DerivedValue(Keys.RunnerTunedSpeed, $"runner tuned speed ({on}stage 0)", DerivedValueStatus.Unavailable, null, "rpm",
                    intakePart == null ? "no intake manifold installed" : "geometry incomplete", b, 0));
                continue;
            }
            var intake = (IntakeManifoldSpec)intakePart.EffectiveSpec;
            var stages = intake.AllStages();
            for (int s = 0; s < stages.Count; s++)
            {
                var stage = new RunnerStageConfiguration(stages[s], bg);
                if (stage.DefaultDiameter)
                    v.Add(new DerivedValue(Keys.RunnerDiameter, $"runner diameter ({on}stage {s})", DerivedValueStatus.Defaulted,
                        Units.MToMm(stage.Diameter), "mm", string.Create(inv,
                            $"{intakePart.Definition.Id} states none: {RunnerStageConfiguration.DefaultDiameterPerBore:0.00} × bore (assumption A5)"), b, s));
                v.Add(new DerivedValue(Keys.RunnerTunedSpeed, $"runner tuned speed ({on}stage {s})", DerivedValueStatus.Derived,
                    stage.TunedRpmAtReference, "rpm", string.Create(inv,
                        $"{intakePart.Definition.Id}: L {Units.MToMm(stage.Length):0.#} mm, d {Units.MToMm(stage.Diameter):0.#} mm, β {stage.VolumeRatio:0.###}; runner gas at {IntakeGasDynamics.ReferenceTemperature:0.##} K"),
                    b, s));
            }
        }
        return new EngineDerivedValues(v);
    }
}
