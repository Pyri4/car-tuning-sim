using CarSim.Core.Common;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;

namespace CarSim.Core.Engines;

public enum IssueSeverity
{
    /// <summary>Informational note (e.g. derived compression ratio).</summary>
    Info,
    /// <summary>The engine will run but the combination is risky.</summary>
    Warning,
    /// <summary>Physically impossible or unsafe to start: the engine will not run.</summary>
    Error,
}

public sealed record CompatibilityIssue(IssueSeverity Severity, string Code, string Message, IReadOnlyList<string> Slots)
{
    public override string ToString() => $"[{Severity}] {Code}: {Message}";
}

/// <summary>Optional operating intent that some checks compare against (e.g. the tune's rev limit).</summary>
public sealed record ValidationContext(double? RevLimitRpm = null);

public sealed class ValidationReport
{
    public ValidationReport(IReadOnlyList<CompatibilityIssue> issues) => Issues = issues;

    public IReadOnlyList<CompatibilityIssue> Issues { get; }
    public bool CanRun => Issues.All(i => i.Severity != IssueSeverity.Error);
    public IEnumerable<CompatibilityIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);
    public IEnumerable<CompatibilityIssue> Warnings => Issues.Where(i => i.Severity == IssueSeverity.Warning);
    public bool Has(string code) => Issues.Any(i => i.Code == code);
}

/// <summary>
/// Checks whether an assembled engine is physically consistent. Rules read spec properties only;
/// no rule refers to a specific part id. Players may assemble anything the slot order allows — this
/// report tells them what will not work (errors) or what is risky (warnings), and why.
/// </summary>
public static class AssemblyValidator
{
    public const double JournalToleranceMm = 0.01;
    public const double BoreToleranceMm = 0.05;
    public const double MinSafeQuenchMm = 0.6;
    public const double PoorQuenchDeckClearanceMm = 1.5;
    public const double HighCompressionRatio = 13.5;
    public const double LowCompressionRatio = 7.5;

    public static ValidationReport Validate(EngineAssembly a, ValidationContext? context = null)
    {
        context ??= new ValidationContext();
        var issues = new List<CompatibilityIssue>();
        void Add(IssueSeverity s, string code, string msg, params string[] slots) => issues.Add(new CompatibilityIssue(s, code, msg, slots));

        // Completeness.
        foreach (var slot in a.MissingRequiredSlots())
            Add(IssueSeverity.Error, "missing_part", $"{slot.Label} is not installed.", slot.Id);

        // Interfaces: every requirement must be provided by some other installed part.
        var installed = a.Installed.ToList();
        foreach (var (slotId, part) in installed)
        {
            foreach (var req in part.Definition.Requires)
            {
                bool provided = installed.Any(o => o.Key != slotId && o.Value.Definition.Provides.Contains(req, StringComparer.Ordinal));
                if (!provided)
                    Add(IssueSeverity.Error, "missing_interface",
                        $"{part.Definition.Name} needs a part providing '{req}', but none is installed.", slotId);
            }
        }

        var block = a.SpecOf<BlockSpec>(PartCategory.Block);
        var crank = a.SpecOf<CrankshaftSpec>(PartCategory.Crankshaft);
        var mains = a.SpecOf<BearingSpec>(PartCategory.MainBearings);
        var rodBearings = a.SpecOf<BearingSpec>(PartCategory.RodBearings);
        var rods = a.SpecOf<ConnectingRodSpec>(PartCategory.ConnectingRods);
        var pistons = a.SpecOf<PistonSpec>(PartCategory.Pistons);
        var gasket = a.SpecOf<HeadGasketSpec>(PartCategory.HeadGasket);
        var head = a.SpecOf<CylinderHeadSpec>(PartCategory.CylinderHead);
        var cams = a.SpecOf<CamshaftSpec>(PartCategory.Camshafts);
        var springs = a.SpecOf<ValveSpringSpec>(PartCategory.ValveSprings);
        var injectors = a.SpecOf<InjectorSpec>(PartCategory.Injectors);
        var flywheel = a.SpecOf<FlywheelSpec>(PartCategory.Flywheel);

        int cylinders = a.Definition.Cylinders;
        if (block != null && block.Cylinders != cylinders)
            Add(IssueSeverity.Error, "cylinder_count", $"Block has {block.Cylinders} cylinders; this engine family has {cylinders}.", "block");

        void CheckCount(string category, int? count, string what)
        {
            if (count != null && count != cylinders)
                Add(IssueSeverity.Error, "set_count", $"{what}: set of {count}, engine needs {cylinders}.", SlotOf(a, category));
        }
        CheckCount(PartCategory.Pistons, pistons?.Count, "Pistons");
        CheckCount(PartCategory.ConnectingRods, rods?.Count, "Connecting rods");
        CheckCount(PartCategory.Injectors, injectors?.Count, "Injectors");

        // Journal and pin fits.
        void CheckFit(double? a1, double? b1, string code, string message, params string[] cats)
        {
            if (a1 is double x && b1 is double y && Math.Abs(x - y) > JournalToleranceMm)
                Add(IssueSeverity.Error, code, message, cats.Select(c => SlotOf(a, c)).ToArray());
        }
        CheckFit(block?.MainJournalDiameterMm, crank?.MainJournalDiameterMm, "main_journal_mismatch",
            $"Crank main journals ({crank?.MainJournalDiameterMm} mm) do not fit the block saddles ({block?.MainJournalDiameterMm} mm).",
            PartCategory.Block, PartCategory.Crankshaft);
        CheckFit(mains?.JournalDiameterMm, crank?.MainJournalDiameterMm, "main_bearing_mismatch",
            $"Main bearings are for {mains?.JournalDiameterMm} mm journals; crank mains are {crank?.MainJournalDiameterMm} mm.",
            PartCategory.MainBearings, PartCategory.Crankshaft);
        CheckFit(rods?.BigEndDiameterMm, crank?.RodJournalDiameterMm, "rod_journal_mismatch",
            $"Rod big ends ({rods?.BigEndDiameterMm} mm) do not fit the crank pins ({crank?.RodJournalDiameterMm} mm).",
            PartCategory.ConnectingRods, PartCategory.Crankshaft);
        CheckFit(rodBearings?.JournalDiameterMm, crank?.RodJournalDiameterMm, "rod_bearing_mismatch",
            $"Rod bearings are for {rodBearings?.JournalDiameterMm} mm pins; crank pins are {crank?.RodJournalDiameterMm} mm.",
            PartCategory.RodBearings, PartCategory.Crankshaft);
        CheckFit(rods?.PinDiameterMm, pistons?.PinDiameterMm, "piston_pin_mismatch",
            $"Piston pins ({pistons?.PinDiameterMm} mm) do not fit the rod small ends ({rods?.PinDiameterMm} mm).",
            PartCategory.ConnectingRods, PartCategory.Pistons);

        if (block != null && pistons != null && Math.Abs(block.BoreMm - pistons.BoreMm) > BoreToleranceMm)
        {
            string hint = pistons.BoreMm > block.BoreMm ? " The block must be bored oversize first." : " They would slap and leak badly.";
            Add(IssueSeverity.Error, "piston_bore_mismatch",
                $"Pistons are made for a {pistons.BoreMm} mm bore; the block is {block.BoreMm} mm.{hint}",
                SlotOf(a, PartCategory.Pistons), SlotOf(a, PartCategory.Block));
        }

        if (gasket != null && block != null && gasket.BoreMm < block.BoreMm)
            Add(IssueSeverity.Error, "gasket_bore_small",
                $"Head gasket bore ({gasket.BoreMm} mm) is smaller than the cylinder bore ({block.BoreMm} mm); the piston would hit the fire ring.",
                SlotOf(a, PartCategory.HeadGasket));

        // Geometry-derived checks.
        var geometry = EngineGeometry.TryCreate(a, out _);
        if (geometry != null)
        {
            double quenchMm = Units.MToMm(geometry.PistonToHeadClearance);
            double deckMm = Units.MToMm(geometry.DeckClearance);
            if (quenchMm < 0)
                Add(IssueSeverity.Error, "piston_head_contact",
                    $"Pistons rise {-quenchMm:F2} mm into the cylinder head at TDC (deck clearance {deckMm:F2} mm, gasket {Units.MToMm(geometry.GasketThickness):F2} mm). Check stroke, rod length and compression height.",
                    SlotOf(a, PartCategory.Pistons), SlotOf(a, PartCategory.ConnectingRods), SlotOf(a, PartCategory.Crankshaft));
            else if (quenchMm < MinSafeQuenchMm)
                Add(IssueSeverity.Warning, "tight_quench",
                    $"Piston-to-head clearance is only {quenchMm:F2} mm; rod stretch at high RPM may cause contact (safe ≥ {MinSafeQuenchMm} mm).",
                    SlotOf(a, PartCategory.Pistons), SlotOf(a, PartCategory.HeadGasket));
            if (deckMm > PoorQuenchDeckClearanceMm)
                Add(IssueSeverity.Warning, "poor_quench",
                    $"Pistons sit {deckMm:F2} mm below the deck at TDC; weak quench makes the engine more knock-prone.",
                    SlotOf(a, PartCategory.Pistons));

            double cr = geometry.CompressionRatio;
            if (double.IsInfinity(cr) || geometry.ClearanceVolume <= 0)
                Add(IssueSeverity.Error, "no_clearance_volume", "Combustion chamber has no clearance volume; the engine cannot turn over.", SlotOf(a, PartCategory.Pistons));
            else
            {
                Add(IssueSeverity.Info, "compression_ratio",
                    $"Static compression ratio {cr:F2}:1 ({Units.M3ToLitres(geometry.Displacement):F2} L, bore {Units.MToMm(geometry.Bore):F1} × stroke {Units.MToMm(geometry.Stroke):F1} mm).");
                if (cr > HighCompressionRatio)
                    Add(IssueSeverity.Warning, "high_compression", $"Compression ratio {cr:F1}:1 is very high for pump fuel; expect heavy knock unless timing is pulled or octane raised.");
                if (cr < LowCompressionRatio)
                    Add(IssueSeverity.Warning, "low_compression", $"Compression ratio {cr:F1}:1 is very low; thermal efficiency and off-boost response will suffer.");
            }
        }

        // Valvetrain.
        if (cams != null && springs != null && cams.MaxLiftMm > springs.MaxLiftMm)
            Add(IssueSeverity.Error, "coil_bind",
                $"Cam lift {cams.MaxLiftMm:F1} mm exceeds the valve springs' usable lift {springs.MaxLiftMm:F1} mm (coil bind / retainer contact).",
                SlotOf(a, PartCategory.Camshafts), SlotOf(a, PartCategory.ValveSprings));

        if (context.RevLimitRpm is double rev)
        {
            if (cams != null && springs != null && head != null)
            {
                var springPart = a.FindByCategory(PartCategory.ValveSprings)!;
                double floatRpm = ValvetrainModel.FloatRpm(cams, springs, head, springPart.Wear);
                if (floatRpm < rev)
                    Add(IssueSeverity.Warning, "valve_float",
                        $"Valves will float at about {floatRpm:F0} rpm, below the {rev:F0} rpm rev limit. Fit stiffer springs or lower the limit.",
                        SlotOf(a, PartCategory.ValveSprings), SlotOf(a, PartCategory.Camshafts));
            }
            if (crank != null && crank.MaxRpm < rev)
                Add(IssueSeverity.Warning, "crank_overspeed",
                    $"Crankshaft is rated to {crank.MaxRpm:F0} rpm; the rev limit is {rev:F0} rpm.", SlotOf(a, PartCategory.Crankshaft));
            if (flywheel != null && flywheel.MaxRpm < rev)
                Add(IssueSeverity.Warning, "flywheel_overspeed",
                    $"Flywheel is rated to {flywheel.MaxRpm:F0} rpm; the rev limit is {rev:F0} rpm.", SlotOf(a, PartCategory.Flywheel));
        }

        return new ValidationReport(issues);
    }

    private static string SlotOf(EngineAssembly a, string category) =>
        a.Definition.Slots.FirstOrDefault(s => s.Category == category)?.Id ?? category;
}
