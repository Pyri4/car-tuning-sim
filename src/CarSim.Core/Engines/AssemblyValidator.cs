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
/// <param name="RevLimitRpm">The tune's rev limit.</param>
/// <param name="MaxBoostTargetKpa">Highest absolute boost target in the tune (null if the tune has no boost table).</param>
public sealed record ValidationContext(double? RevLimitRpm = null, double? MaxBoostTargetKpa = null);

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
        var def = a.Definition;
        int banks = def.Banks.Count;
        // "Bank 'right': " on a multi-bank engine, nothing on a single-bank one.
        string On(int bank) => banks > 1 ? $"Bank '{def.Banks[bank].Id}': " : "";

        // Topology: the family must fit the engine model (content loading rejects families that do not;
        // this also covers definitions built in code).
        foreach (var problem in EngineTopology.CheckFamily(def))
            Add(IssueSeverity.Error, "unsupported_topology", problem);

        // Completeness: required slots, and every category the simulation reads for every bank, even where a family
        // marks its slot optional.
        var missingSlots = a.MissingRequiredSlots();
        foreach (var slot in missingSlots)
            Add(IssueSeverity.Error, "missing_part", $"{slot.Label} is not installed.", slot.Id);
        foreach (var (category, bank) in EngineTopology.MissingCategories(a))
        {
            var slot = bank < 0 ? def.Slots.FirstOrDefault(s => s.Category == category) : a.SlotFor(category, bank);
            if (slot != null && missingSlots.Contains(slot)) continue;
            string what = category.Replace('_', ' ');
            Add(IssueSeverity.Error, "missing_category", bank < 0 || banks == 1
                    ? $"No {what} installed: the engine cannot run without one."
                    : $"{On(bank)}no {what} installed: every bank needs one.",
                slot == null ? Array.Empty<string>() : new[] { slot.Id });
        }

        // Interfaces: every requirement must be provided by another installed part — for a part serving some banks, by
        // an engine-wide part or one serving one of the same banks (the right exhaust manifold bolts to the right head).
        var installed = a.Installed.ToList();
        foreach (var (slotId, part) in installed)
        {
            var slot = def.GetSlot(slotId);
            var banksServed = def.BanksServedBy(slot);
            foreach (var req in part.Definition.Requires)
            {
                bool provided = installed.Any(o => o.Key != slotId && o.Value.Definition.Provides.Contains(req, StringComparer.Ordinal)
                                                   && def.BanksServedBy(def.GetSlot(o.Key)).Intersect(banksServed).Any());
                if (!provided)
                {
                    bool elsewhere = installed.Any(o => o.Key != slotId && o.Value.Definition.Provides.Contains(req, StringComparer.Ordinal));
                    string where = banks > 1 && slot.Banks.Count > 0 ? $" on {string.Join(", ", slot.Banks.Select(b => $"bank '{b}'"))}" : "";
                    Add(IssueSeverity.Error, "missing_interface", elsewhere
                        ? $"{part.Definition.Name} ({slot.Label}) needs a part providing '{req}'{where}, but only another bank has one."
                        : $"{part.Definition.Name} needs a part providing '{req}', but none is installed.", slotId);
                }
            }
        }

        var block = a.SpecOf<BlockSpec>(PartCategory.Block);
        var crank = a.SpecOf<CrankshaftSpec>(PartCategory.Crankshaft);
        var mains = a.SpecOf<BearingSpec>(PartCategory.MainBearings);
        var rodBearings = a.SpecOf<BearingSpec>(PartCategory.RodBearings);
        var rods = a.SpecOf<ConnectingRodSpec>(PartCategory.ConnectingRods);
        var pistons = a.SpecOf<PistonSpec>(PartCategory.Pistons);
        var injectors = a.SpecOf<InjectorSpec>(PartCategory.Injectors);
        var flywheel = a.SpecOf<FlywheelSpec>(PartCategory.Flywheel);
        var ecu = a.SpecOf<EcuSpec>(PartCategory.Ecu);

        int cylinders = def.Cylinders;
        if (block != null && block.Cylinders != cylinders)
            Add(IssueSeverity.Error, "cylinder_count", $"Block has {block.Cylinders} cylinders; this engine family has {cylinders}.", SlotOf(a, PartCategory.Block));

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

        // Per-bank checks: each bank's gasket, head, springs and camshafts.
        for (int b = 0; b < banks; b++)
        {
            var gasketSlot = a.SlotFor(PartCategory.HeadGasket, b)?.Id ?? PartCategory.HeadGasket;
            var headSlot = a.SlotFor(PartCategory.CylinderHead, b)?.Id ?? PartCategory.CylinderHead;
            var camSlot = a.SlotFor(PartCategory.Camshafts, b)?.Id ?? PartCategory.Camshafts;
            var springSlot = a.SlotFor(PartCategory.ValveSprings, b)?.Id ?? PartCategory.ValveSprings;
            var gasket = a.SpecFor<HeadGasketSpec>(PartCategory.HeadGasket, b);
            var head = a.SpecFor<CylinderHeadSpec>(PartCategory.CylinderHead, b);
            var cams = a.SpecFor<CamshaftSpec>(PartCategory.Camshafts, b);
            var springs = a.SpecFor<ValveSpringSpec>(PartCategory.ValveSprings, b);
            if (gasket != null && block != null && gasket.BoreMm < block.BoreMm && !issues.Any(i => i.Code == "gasket_bore_small" && i.Slots.Contains(gasketSlot)))
                Add(IssueSeverity.Error, "gasket_bore_small",
                    $"{On(b)}Head gasket bore ({gasket.BoreMm} mm) is smaller than the cylinder bore ({block.BoreMm} mm); the piston would hit the fire ring.",
                    gasketSlot);
            if (head != null && cams != null && head.Valvetrain != cams.Valvetrain && !issues.Any(i => i.Code == "valvetrain_mismatch" && i.Slots.Contains(camSlot) && i.Slots.Contains(headSlot)))
                Add(IssueSeverity.Error, "valvetrain_mismatch",
                    $"{On(b)}the cylinder head is built for a {ValvetrainTypes.Describe(head.Valvetrain)} valvetrain, but the camshafts fitted are {ValvetrainTypes.Describe(cams.Valvetrain)} camshafts: they cannot work together.",
                    headSlot, camSlot);
            if (cams != null && springs != null && cams.MaxLiftMm > springs.MaxLiftMm && !issues.Any(i => i.Code == "coil_bind" && i.Slots.Contains(camSlot) && i.Slots.Contains(springSlot)))
                Add(IssueSeverity.Error, "coil_bind",
                    $"{On(b)}Cam lift {cams.MaxLiftMm:F1} mm exceeds the valve springs' usable lift {springs.MaxLiftMm:F1} mm (coil bind / retainer contact).",
                    camSlot, springSlot);
            if (context.RevLimitRpm is double rev && cams != null && springs != null && head != null)
            {
                var springPart = a.PartFor(PartCategory.ValveSprings, b)!;
                double floatRpm = ValvetrainModel.LowestFloatRpm(cams, springs, head, springPart.Wear);
                if (floatRpm < rev && !issues.Any(i => i.Code == "valve_float" && i.Slots.Contains(springSlot)))
                    Add(IssueSeverity.Warning, "valve_float",
                        $"{On(b)}Valves will float at about {floatRpm:F0} rpm, below the {rev:F0} rpm rev limit. Fit stiffer springs or lower the limit.",
                        springSlot, camSlot);
            }
            // Variable valvetrain hardware the ECU cannot drive.
            if (cams is { IntakePhaserRangeDeg: > 0 } && ecu is { CamPhaseControl: false } && !issues.Any(i => i.Code == "cam_phaser_uncontrolled" && i.Slots.Contains(camSlot)))
                Add(IssueSeverity.Warning, "cam_phaser_uncontrolled",
                    $"{On(b)}The ECU cannot drive the intake cam phaser: it stays at its park position ({cams.InstalledIntakeCenterlineDeg:F0}° ATDC, " +
                    "the fully retarded end), so low-speed torque suffers.",
                    SlotOf(a, PartCategory.Ecu), camSlot);
            if (cams is { HasVariableLift: true } && ecu is { ValveLiftControl: false } && !issues.Any(i => i.Code == "valve_lift_uncontrolled" && i.Slots.Contains(camSlot)))
                Add(IssueSeverity.Warning, "valve_lift_uncontrolled",
                    $"{On(b)}The ECU cannot switch the variable-lift camshafts: they stay on their base profile, so the top end suffers.",
                    SlotOf(a, PartCategory.Ecu), camSlot);
            var intakeSlot = a.SlotFor(PartCategory.IntakeManifold, b)?.Id ?? PartCategory.IntakeManifold;
            if (a.SpecFor<IntakeManifoldSpec>(PartCategory.IntakeManifold, b) is { SwitchedRunnerLengthMm: not null } && ecu is { IntakeRunnerControl: false }
                && !issues.Any(i => i.Code == "intake_runner_uncontrolled" && i.Slots.Contains(intakeSlot)))
                Add(IssueSeverity.Warning, "intake_runner_uncontrolled",
                    $"{On(b)}The ECU cannot switch the variable intake manifold: it stays on its primary runner.",
                    SlotOf(a, PartCategory.Ecu), intakeSlot);
        }

        // Geometry-derived checks, per bank: each bank's gasket and head set its own chamber, quench and compression
        // ratio. Banks with the same geometry are reported once (without a bank name when every bank agrees).
        var geometries = Enumerable.Range(0, banks).Select(b => EngineGeometry.TryCreate(a, b, out _)).ToList();
        bool sameGeometry = geometries.Distinct().Count() == 1;
        for (int b = 0; b < banks; b++)
        {
            var geometry = geometries[b];
            if (geometry == null || geometries.Take(b).Contains(geometry)) continue;
            string on = sameGeometry ? "" : On(b);
            string gasketSlot = a.SlotFor(PartCategory.HeadGasket, b)?.Id ?? PartCategory.HeadGasket;
            string headSlot = a.SlotFor(PartCategory.CylinderHead, b)?.Id ?? PartCategory.CylinderHead;
            double quenchMm = Units.MToMm(geometry.PistonToHeadClearance);
            double deckMm = Units.MToMm(geometry.DeckClearance);
            if (quenchMm < 0)
                Add(IssueSeverity.Error, "piston_head_contact",
                    $"{on}Pistons rise {-quenchMm:F2} mm into the cylinder head at TDC (deck clearance {deckMm:F2} mm, gasket {Units.MToMm(geometry.GasketThickness):F2} mm). Check stroke, rod length and compression height.",
                    SlotOf(a, PartCategory.Pistons), SlotOf(a, PartCategory.ConnectingRods), SlotOf(a, PartCategory.Crankshaft));
            else if (quenchMm < MinSafeQuenchMm)
                Add(IssueSeverity.Warning, "tight_quench",
                    $"{on}Piston-to-head clearance is only {quenchMm:F2} mm; rod stretch at high RPM may cause contact (safe ≥ {MinSafeQuenchMm} mm).",
                    SlotOf(a, PartCategory.Pistons), gasketSlot);
            if (deckMm > PoorQuenchDeckClearanceMm)
                Add(IssueSeverity.Warning, "poor_quench",
                    $"{on}Pistons sit {deckMm:F2} mm below the deck at TDC; weak quench makes the engine more knock-prone.",
                    SlotOf(a, PartCategory.Pistons));

            double cr = geometry.CompressionRatio;
            if (double.IsInfinity(cr) || geometry.ClearanceVolume <= 0)
                Add(IssueSeverity.Error, "no_clearance_volume", $"{on}Combustion chamber has no clearance volume; the engine cannot turn over.", SlotOf(a, PartCategory.Pistons), headSlot);
            else
            {
                Add(IssueSeverity.Info, "compression_ratio",
                    $"{on}Static compression ratio {cr:F2}:1 ({Units.M3ToLitres(geometry.Displacement):F2} L, bore {Units.MToMm(geometry.Bore):F1} × stroke {Units.MToMm(geometry.Stroke):F1} mm).");
                if (cr > HighCompressionRatio)
                    Add(IssueSeverity.Warning, "high_compression", $"{on}Compression ratio {cr:F1}:1 is very high for pump fuel; expect heavy knock unless timing is pulled or octane raised.", headSlot, gasketSlot);
                if (cr < LowCompressionRatio)
                    Add(IssueSeverity.Warning, "low_compression", $"{on}Compression ratio {cr:F1}:1 is very low; thermal efficiency and off-boost response will suffer.", headSlot, gasketSlot);
            }
        }

        if (context.RevLimitRpm is double revLimit)
        {
            if (crank != null && crank.MaxRpm < revLimit)
                Add(IssueSeverity.Warning, "crank_overspeed",
                    $"Crankshaft is rated to {crank.MaxRpm:F0} rpm; the rev limit is {revLimit:F0} rpm.", SlotOf(a, PartCategory.Crankshaft));
            if (flywheel != null && flywheel.MaxRpm < revLimit)
                Add(IssueSeverity.Warning, "flywheel_overspeed",
                    $"Flywheel is rated to {flywheel.MaxRpm:F0} rpm; the rev limit is {revLimit:F0} rpm.", SlotOf(a, PartCategory.Flywheel));
        }

        // Forced induction vs ECU, per turbocharger.
        foreach (var (turboSlot, turboPart) in a.PartsOf(PartCategory.Turbocharger))
        {
            var turbo = turboPart.Spec<TurbochargerSpec>();
            if (ecu == null) break;
            string which = a.PartsOf(PartCategory.Turbocharger).Count() > 1 ? $"{turboSlot.Label}: " : "";
            double springAbsKpa = Units.PaToKpa(PhysicalConstants.StandardPressure) + turbo.WastegateSpringKpa;
            if (ecu.MapSensorMaxKpa < springAbsKpa - 5)
                Add(IssueSeverity.Warning, "map_sensor_range",
                    $"{which}The ECU's MAP sensor reads only up to {ecu.MapSensorMaxKpa:F0} kPa, but the wastegate spring alone makes about {springAbsKpa:F0} kPa. " +
                    "The ECU cannot see boost: it will fuel and time the engine as if it were at its sensor limit (lean and over-advanced).",
                    SlotOf(a, PartCategory.Ecu), turboSlot.Id);
            if (!ecu.BoostControl)
                Add(IssueSeverity.Info, "boost_by_spring",
                    $"{which}No electronic boost control: boost is set by the wastegate spring (about {turbo.WastegateSpringKpa:F0} kPa gauge, creeping higher with flow).");
            if (ecu.BoostControl && context.MaxBoostTargetKpa is double over && over > ecu.MapSensorMaxKpa)
                Add(IssueSeverity.Warning, "boost_target_above_map_sensor",
                    $"{which}The boost target ({over:F0} kPa) is above what the ECU's MAP sensor can read ({ecu.MapSensorMaxKpa:F0} kPa): it never sees the target reached, " +
                    "holds the wastegate shut and over-boosts.",
                    SlotOf(a, PartCategory.Ecu), turboSlot.Id);
            if (ecu.BoostControl && context.MaxBoostTargetKpa is double target && target < springAbsKpa - 5)
                Add(IssueSeverity.Warning, "boost_target_below_spring",
                    $"{which}The boost target ({target:F0} kPa) is below what the wastegate spring allows ({springAbsKpa:F0} kPa); the ECU can only hold the wastegate shut, not open it early.",
                    turboSlot.Id);
        }

        return new ValidationReport(issues);
    }

    private static string SlotOf(EngineAssembly a, string category) =>
        a.Definition.Slots.FirstOrDefault(s => s.Category == category)?.Id ?? category;
}
