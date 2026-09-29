using CarSim.Core.Common;
using CarSim.Core.Content;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Simulation;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Verification;
using CarSim.Verification.Calibration;
using CarSim.Verification.Fingerprint;

namespace CarSim.Cli;

public static class Program
{
    private const string Usage = """
        carsim — Car Tuning Simulator command-line tools

        Usage:
          carsim validate [--content <dir>] [--mods <dir>]
                                                        Load and validate the base content and every mod.
          carsim check-engine <engine-id> [--verbose 1] [--strict 1]
                                                        Assembly-aware validation of one engine (Engine Authoring Factory):
                                                        content, identity, architecture, stock build, topology, interfaces,
                                                        geometry, limits, features, provenance coverage, completeness.
                                                        Exit 0 = no errors (warnings allowed); 2 = errors; 3 = warnings under
                                                        --strict. --verbose also prints notes and unrecorded/defaulted fields.
          carsim inspect <engine-id> [--content <dir>]
                                                        Show the stock build, derived geometry and compatibility report.
          carsim sweep <engine-id> [--swap slot=part,...] [--fuel <id>] [--tune <id>] [--from 1000] [--to 8000] [--step 500]
                                                        Steady-state full-throttle dyno sweep of the stock build (with swaps).
          carsim hold <engine-id> [--rpm 6000] [--seconds 30] [--throttle 1] [--sump-g 0] [--air-speed <m/s>] [build options]
                                                        Hold an operating point; print warnings, failure reports and inspection.
                                                        (--air-speed uses the radiator instead of test-cell coolant control.)
          carsim drive <engine-id> [--vehicle <id>] [--laps 3] [--chassis slot=part,...] [--set slot.field=value,...] [--wear slot=0.4,...] [--cold 1] [--trace <s>] [build options]
                                                        Autopilot laps of the test facility in the engine's stock car (or --vehicle,
                                                        if the interfaces fit): lap times,
                                                        clutch/brake temperatures, wear, warnings and failure reports.
          carsim calibrate-ve <engine-id> [--hold 1] [build options]
                                                        Measure the build's breathing on a steady-state dyno and print a
                                                        volumetric_efficiency table for the tune (a base-map generator for
                                                        content authors; the game's ECU never sees the engine's true VE).
          carsim calibrate-spark <engine-id> [--knock-margin 1.5] [--mbt-margin 1] [--hold 1] [build options]
                                                        Measure best-torque and knock-limited timing on a steady-state dyno and
                                                        print an ignition_advance_deg table (min(MBT − margin, knock limit − margin)
                                                        on the --fuel given; a base map for content authors).
          carsim calibrate-cams <engine-id> [--step 5] [--hold 0.8] [build options]
                                                        For a cam phaser the ECU can drive: the intake advance that traps the most
                                                        air at each rpm and load, as an intake_cam_advance_deg table (a base map).
          carsim bench <engine-id> [--steps 20000] [--repeats 5] [--rpm 5000] [build options]
                                                        Step cost: best-of-N time and allocated bytes per engine step (full
                                                        throttle, held speed) and per vehicle step (autopilot on the test track).
          carsim fingerprint [--case <id>[,<id>...]] [--check <file>] [--write <file>] [--dump <dir>] [--jobs <n>] [--list 1]
                                                        Regression fingerprint of the verification matrix (docs/VERIFICATION.md):
                                                        runs every case (or --case, by id or id prefix) and compares it with the
                                                        checked-in baseline (tests/baselines/fingerprint.txt, or --check <file>).
                                                        --write <file> re-baselines (always the whole matrix); --dump <dir> also
                                                        writes every value at full precision. Exit code 4 when it differs.
          carsim derive-stage <engine-id> --switch <rpm> [--upper-length <mm>] [--diameter <mm>] [--k 2.1] [--damping 0.35]
                                                        Assumption A-D1 for a two-stage intake: the effective lower stage whose
                                                        gain crosses the upper stage's at a sourced switch speed (arithmetic on
                                                        the model's gain functions; no engine is run). The upper stage defaults to
                                                        the stock intake's last stage; prints the sensitivity to the inputs.
          carsim fingerprint-diff <before-dir> <after-dir>
                                                        Key-by-key comparison of two --dump directories (e.g. two commits).
          carsim regenerate-tunes [--tune <id>[,<id>...]] [--write 1] [--jobs <n>] [--manifest <file>]
                                                        The recalibration driver: runs every tune's recipe from
                                                        tools/CarSim.Verification/tune-manifest.json with the dev calibrators and
                                                        reports, table by table, whether the checked-in tables are reproduced;
                                                        audits hand-authored spark maps. --write 1 rewrites the calibrated tables
                                                        (never the hand-authored ones) in the tune files.
        <engine-id> (kestrel_k20, isar_m54, ...) may be left out only when the content has a single engine family.
        Build options: --swap slot=part,...  --add slot=part,...  --fuel <id>  --tune <id>
        Content options (every command): --content <dir> (default content/base)  --mods <dir> (default content/mods;
        --mods content/test loads the synthetic engine matrix)
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            var options = CliOptions.Parse(args.Skip(1).ToArray());
            return args[0] switch
            {
                "validate" => Validate(options),
                "inspect" => Inspect(options),
                "check-engine" => CheckEngine(options),
                "sweep" => Sweep(options),
                "hold" => Hold(options),
                "drive" => Drive(options),
                "calibrate-ve" => CalibrateVe(options),
                "bench" => Bench(options),
                "derive-stage" => DeriveStage(options),
                "calibrate-spark" => CalibrateSpark(options),
                "calibrate-cams" => CalibrateCams(options),
                "fingerprint" => Fingerprint(options),
                "fingerprint-diff" => FingerprintDiff(options),
                "regenerate-tunes" => RegenerateTunes(options),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or KeyNotFoundException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static string ModsDir(CliOptions o) =>
        o.Named.GetValueOrDefault("mods") ?? Path.GetFullPath(Path.Combine(o.ContentDir, "..", "mods"));

    private static int Validate(CliOptions o)
    {
        var result = ContentLoader.LoadWithMods(o.ContentDir, ModsDir(o));
        var db = result.Database;
        Console.WriteLine($"Content: {o.ContentDir}");
        Console.WriteLine(result.Mods.Count == 0 ? $"  no mods in {ModsDir(o)}" : $"  mods: {string.Join(", ", result.Mods)}");
        foreach (var ov in result.Overrides) Console.WriteLine($"    {ov}");
        Console.WriteLine($"  {db.Parts.Count} parts, {db.Engines.Count} engines, {db.Fuels.Count} fuels, {db.Tunes.Count} tunes");
        if (result.Success)
        {
            Console.WriteLine("  OK — no errors.");
            return 0;
        }
        Console.WriteLine($"  {result.Errors.Count} error(s):");
        foreach (var e in result.Errors) Console.WriteLine($"    {e}");
        return 2;
    }

    private sealed record Built(EngineSimulation Sim, EcuTune Tune, ContentDatabase Db, PartInstanceFactory Factory);

    /// <summary>The engine family named on the command line; optional only when the content has a single family.</summary>
    private static string EngineId(CliOptions o, ContentDatabase db)
    {
        if (o.Positional.FirstOrDefault() is { } id) return id;
        if (db.Engines.Count == 1) return db.Engines.Keys.First();
        throw new ArgumentException($"Several engine families are loaded; name one: {string.Join(", ", db.Engines.Keys.OrderBy(k => k, StringComparer.Ordinal))}.");
    }

    /// <summary>Builds the stock engine with --swap/--add parts, --fuel and --tune applied.</summary>
    private static Built BuildEngine(CliOptions o)
    {
        var db = ContentLoader.LoadWithMods(o.ContentDir, ModsDir(o)).GetOrThrow();
        var engine = db.GetEngine(EngineId(o, db));
        var factory = new PartInstanceFactory();
        var assembly = EngineAssembly.CreateStock(engine, db, factory);
        if (o.Named.TryGetValue("swap", out var swaps))
        {
            foreach (var pair in swaps.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length != 2) throw new ArgumentException($"Bad --swap entry '{pair}', expected slot=part.");
                SwapPart(assembly, kv[0], db.GetPart(kv[1]), factory);
            }
        }
        if (o.Named.TryGetValue("add", out var adds))
        {
            foreach (var pair in adds.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length != 2) throw new ArgumentException($"Bad --add entry '{pair}', expected slot=part.");
                var r = assembly.Install(kv[0], factory.Create(db.GetPart(kv[1])));
                if (!r.Ok) throw new ArgumentException(r.Message);
            }
        }
        var fuel = db.GetFuel(o.Named.GetValueOrDefault("fuel", "gasoline_95"));
        var tune = EcuTune.FromDocument(db.GetTune(o.Named.GetValueOrDefault("tune", engine.StockTune)));
        var config = EngineConfiguration.Build(assembly, fuel, new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa));
        foreach (var issue in config.Report.Issues.Where(i => i.Severity != IssueSeverity.Info)) Console.WriteLine(issue);
        return new Built(new EngineSimulation(config.GetOrThrow(), tune, EngineState.Warm()), tune, db, factory);
    }

    private static int CalibrateVe(CliOptions o)
    {
        var (sim, tune, _, _) = BuildEngine(o);
        var table = VeCalibrator.Calibrate(sim, Num(o, "hold", 1.0));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine($"// {tune.Id}: rpm {string.Join(", ", tune.VolumetricEfficiency.XAxis)}; load kPa {string.Join(", ", tune.VolumetricEfficiency.YAxis)}");
        Console.WriteLine("\"volumetric_efficiency\": [");
        for (int r = 0; r < table.Length; r++)
            Console.WriteLine($"  [{string.Join(", ", table[r].Select(v => v.ToString("0.000", inv)))}]{(r < table.Length - 1 ? "," : "")}");
        Console.WriteLine("],");
        Console.WriteLine($"\"displacement_cc\": {tune.DisplacementCc.ToString("0", inv)},");
        return 0;
    }

    private static int CalibrateSpark(CliOptions o)
    {
        var (sim, tune, _, _) = BuildEngine(o);
        var table = SparkCalibrator.Calibrate(sim, Num(o, "knock-margin", 1.5), Num(o, "mbt-margin", 1.0), Num(o, "hold", 1.0));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine($"// {tune.Id} on {sim.Config.Fuel.Id}: rpm {string.Join(", ", tune.IgnitionAdvance.XAxis)}; load kPa {string.Join(", ", tune.IgnitionAdvance.YAxis)}");
        Console.WriteLine("\"ignition_advance_deg\": [");
        for (int r = 0; r < table.Length; r++)
            Console.WriteLine($"  [{string.Join(", ", table[r].Select(v => v.ToString("0.#", inv)))}]{(r < table.Length - 1 ? "," : "")}");
        Console.WriteLine("],");
        return 0;
    }

    private static int CalibrateCams(CliOptions o)
    {
        var (sim, tune, _, _) = BuildEngine(o);
        double range = sim.Config.Banks.Max(b => b.IntakePhaserRange);
        if (range <= 0)
            return Fail($"This build has no intake cam phaser its ECU can drive ({string.Join(", ", sim.Config.Banks.Select(b => b.CamsPart.Definition.Name).Distinct())}, {sim.Config.Part(PartCategory.Ecu).Definition.Name}).");
        var table = CamPhaseCalibrator.Calibrate(sim, Num(o, "step", 5.0), Num(o, "hold", 0.8));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine($"// {tune.Id}: rpm {string.Join(", ", tune.IgnitionAdvance.XAxis)}; load kPa {string.Join(", ", tune.IgnitionAdvance.YAxis)}; phaser range {range:0.#}°");
        Console.WriteLine("\"intake_cam_advance_deg\": [");
        for (int r = 0; r < table.Length; r++)
            Console.WriteLine($"  [{string.Join(", ", table[r].Select(v => v.ToString("0.#", inv)))}]{(r < table.Length - 1 ? "," : "")}");
        Console.WriteLine("],");
        return 0;
    }

    private static double Num(CliOptions o, string key, double fallback) =>
        o.Named.TryGetValue(key, out var v) ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    private static int Hold(CliOptions o)
    {
        var (sim, _, _, _) = BuildEngine(o);
        double rpm = Num(o, "rpm", 6000), seconds = Num(o, "seconds", 30), throttle = Num(o, "throttle", 1.0);
        var input = new EngineInputs
        {
            Throttle = throttle, SpeedMode = SpeedMode.Held, HeldRpm = rpm, SumpAccelerationG = Num(o, "sump-g", 0),
            CoolingAirSpeed = Num(o, "air-speed", 10),
            CoolantTemperatureOverride = o.Named.ContainsKey("air-speed") ? null : 363.15,
        };
        const double dt = 0.005;
        int perSecond = (int)(1 / dt);
        Console.WriteLine($"Holding {rpm:F0} rpm at {throttle:P0} throttle for up to {seconds:F0} s ...");
        for (int i = 0; i < seconds * perSecond; i++)
        {
            var t = sim.Step(dt, input);
            if (i % perSecond == perSecond - 1 || sim.Damage.Failures.Count > 0)
            {
                Console.WriteLine($"t={t.Time,5:F1}s {t.Torque,6:F1} N·m {t.PowerHp,6:F1} hp λ {t.Lambda:F2} knock {t.KnockIntensity:F1}° PCP {t.PeakCylinderPressureBar:F0} bar coolant {t.CoolantC:F0} °C oil {t.OilC:F0} °C {t.OilPressureBar:F2} bar");
                foreach (var w in sim.Damage.Warnings) Console.WriteLine($"   [{w.Level}] {w.Message}");
            }
            if (sim.Damage.Failures.Count > 0) break;
        }
        foreach (var f in sim.Damage.Failures) { Console.WriteLine(); Console.WriteLine(f.ToText()); }
        Console.WriteLine("Inspection:");
        foreach (var slot in sim.Config.Assembly.Definition.Slots)
        {
            var part = sim.Config.Assembly.PartIn(slot.Id);
            if (part == null) continue;
            var findings = CarSim.Core.Damage.PartInspector.Inspect(part);
            if (findings.All(x => x.Severity == CarSim.Core.Damage.FindingSeverity.Good)) continue;
            Console.WriteLine($"  {slot.Label}: {string.Join(" ", findings.Select(x => x.Text))}");
        }
        Console.WriteLine($"Compression test: {CarSim.Core.Damage.EngineDiagnostics.DescribeCompressionTest(sim.Config.Assembly)}");
        return sim.Damage.Failures.Count > 0 ? 3 : 0;
    }

    private static int Sweep(CliOptions o)
    {
        var (sim, _, _, _) = BuildEngine(o);
        double from = double.Parse(o.Named.GetValueOrDefault("from", "1000"), System.Globalization.CultureInfo.InvariantCulture);
        double to = double.Parse(o.Named.GetValueOrDefault("to", "8000"), System.Globalization.CultureInfo.InvariantCulture);
        double step = double.Parse(o.Named.GetValueOrDefault("step", "500"), System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"{"rpm",6} {"Nm",6} {"kW",6} {"hp",6} {"MAPkPa",7} {"VE",5} {"λ",5} {"duty",5} {"adv",5} {"MBT",5} {"KLSA",5} {"knk",4} {"PCPbar",6} {"EGT°C",6} {"oil bar",7} {"FMEPbar",7} {"limit",6} {"port",6} {"exhBP",6} {"VEdyn",5} {"resid",5} {"PMEP",5} {"turbo krpm",10} {"PR",5} {"cEff",5} {"choke",5} {"WG",4} {"IAT°C",6} {"cam°",5}");
        // Switched valvetrain or intake stages and, on engines with several banks or turbos, their channels.
        bool stages = sim.Config.Banks.Any(b => b.HasVariableLift || b.HasSwitchedRunner);
        int banks = sim.Config.Banks.Count, turbos = sim.Config.Turbos.Count;
        if (stages || banks > 1 || turbos > 1)
            Console.WriteLine("  (then: " + string.Join(", ", new[] { stages ? "valve-lift/runner stage" : null, banks > 1 ? "λ, knock and EGT per bank" : null,
                turbos > 1 ? "krpm per turbo" : null }.Where(x => x != null)) + ")");
        foreach (var t in SteadyStateSweep.Run(sim, from, to, step))
        {
            Console.Write($"{t.Rpm,6:F0} {t.Torque,6:F1} {t.PowerKw,6:F1} {t.PowerHp,6:F1} {t.MapKpa,7:F1} {t.VolumetricEfficiency,5:F2} {t.Lambda,5:F2} {t.InjectorDuty,5:F2} {t.IgnitionAdvance,5:F1} {t.MbtAdvance,5:F1} {t.KnockLimitAdvance,5:F1} {t.KnockIntensity,4:F1} {t.PeakCylinderPressureBar,6:F1} {t.EgtC,6:F0} {t.OilPressureBar,7:F2} {Units.PaToBar(t.Fmep),7:F2} {t.FuelLimit,6} {Units.PaToKpa(t.PortPressure),6:F1} {Units.PaToKpa(t.ExhaustBackPressure),6:F1} {t.VeDynamic,5:F2} {t.ResidualFactor,5:F3} {Units.PaToBar(t.Pmep),5:F2} {t.TurboRpm / 1000,10:F1} {t.CompressorPressureRatio,5:F2} {t.CompressorEfficiency,5:F2} {t.CompressorChokeRatio,5:F2} {t.WastegateOpening,4:F2} {Units.KToC(t.ChargeTemperature),6:F0} {t.IntakeCamAdvance,5:F1}");
            if (stages) Console.Write($"  {(t.HighValveLift ? "HI" : "lo")}/{(t.SwitchedRunner ? "sw" : "pr")}");
            if (banks > 1) foreach (var b in t.Banks) Console.Write($"  λ{b.Lambda:F2} k{b.KnockIntensity:F1} {Units.KToC(b.ExhaustGasTemperature):F0}°C");
            if (turbos > 1) foreach (var tt in t.Turbos) Console.Write($"  {tt.ShaftRpm / 1000:F0}k");
            Console.WriteLine();
        }
        return 0;
    }

    private static IEnumerable<(string Key, string Value)> Pairs(CliOptions o, string option)
    {
        if (!o.Named.TryGetValue(option, out var list)) yield break;
        foreach (var pair in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length != 2) throw new ArgumentException($"Bad --{option} entry '{pair}', expected key=value.");
            yield return (kv[0], kv[1]);
        }
    }

    private sealed record BuiltCar(CarSim.Core.Vehicles.VehicleSimulation Sim, CarSim.Core.Vehicles.VehicleConfiguration Car,
        CarSim.Core.Vehicles.VehicleAssembly Chassis, EngineAssembly Engine);

    /// <summary>The engine (with build options) in the car that takes its family, with --chassis, --set and --wear applied.</summary>
    private static BuiltCar BuildCar(CliOptions o)
    {
        var (engineSim, _, db, factory) = BuildEngine(o);
        var engine = engineSim.Config.Assembly;
        // --vehicle puts the engine in another car (an engine swap, checked by interfaces); otherwise its stock car.
        var vehicle = o.Named.GetValueOrDefault("vehicle") is { } vehicleId ? db.GetVehicle(vehicleId)
                      : db.Vehicles.Values.OrderBy(v => v.Id, StringComparer.Ordinal).FirstOrDefault(v => v.Engine == engine.Definition.Id)
                        ?? throw new ArgumentException($"No car ships with engine '{engine.Definition.Id}'; name one with --vehicle <id> ({string.Join(", ", db.Vehicles.Keys.OrderBy(k => k, StringComparer.Ordinal))}).");
        var chassis = CarSim.Core.Vehicles.VehicleAssembly.CreateStock(vehicle, db, factory);
        foreach (var (slot, partId) in Pairs(o, "chassis"))
        {
            chassis.Remove(slot, out _);
            var r = chassis.Install(slot, factory.Create(db.GetPart(partId)));
            if (!r.Ok) throw new ArgumentException(r.Message);
        }
        foreach (var (key, value) in Pairs(o, "set"))
        {
            var dot = key.IndexOf('.');
            if (dot < 0) throw new ArgumentException($"Bad --set entry '{key}', expected slot.field=value.");
            string slot = key[..dot], field = key[(dot + 1)..];
            var part = chassis.PartIn(slot) ?? engine.PartIn(slot) ?? throw new ArgumentException($"Nothing installed in '{slot}'.");
            if (!part.Adjust(field, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)))
                throw new ArgumentException($"{part.Definition.Name} has no '{field}' adjustment. It offers: {string.Join(", ", part.Definition.Adjustments.Select(a => a.Field))}.");
            Console.WriteLine($"  {part.Definition.Name}: {field} = {part.SettingOf(field)}");
        }
        foreach (var (slot, value) in Pairs(o, "wear"))
        {
            var part = chassis.PartIn(slot) ?? engine.PartIn(slot) ?? throw new ArgumentException($"Nothing installed in '{slot}'.");
            part.Wear = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        var interfaces = CarSim.Core.Vehicles.VehicleCompatibility.InterfaceProblems(engine, chassis);
        if (interfaces.Count > 0) throw new ArgumentException($"{engine.Definition.Name} does not fit the {vehicle.Name}: {string.Join(" ", interfaces)}");
        var car = new CarSim.Core.Vehicles.VehicleConfiguration(vehicle, chassis, engine, db);
        var sim = new CarSim.Core.Vehicles.VehicleSimulation(car, engineSim);
        sim.StartIdling();
        return new BuiltCar(sim, car, chassis, engine);
    }

    private static int Drive(CliOptions o)
    {
        var (sim, car, chassis, _) = BuildCar(o);
        var vehicle = car.Definition;
        var session = new CarSim.Gameplay.DrivingSession(sim, CarSim.Core.Vehicles.TrackLayout.TestFacility()) { AutopilotEnabled = true };
        if (o.Named.GetValueOrDefault("cold") == "1") session.StartOnColdTyres();
        int laps = (int)Num(o, "laps", 3);
        Console.WriteLine($"{vehicle.Name}, {car.Mass:F0} kg — {laps} autopilot lap(s) after an out lap");
        Console.WriteLine($"{"lap",4} {"time s",7} {"clutch°C",8} {"brakeF°C",8} {"brakeR°C",8} {"clutch%",7} {"pads%",6} {"tyreF%",6} {"tyreR%",6} {"gbxNm",6} {"diffNm",6} {"tyreF°C",7} {"tyreR°C",7} {"hot kPa",9}  warnings");
        var warnings = new SortedSet<string>();
        double trace = Num(o, "trace", 0), nextTrace = 0;
        double maxClutch = 0, maxFront = 0, maxRear = 0, maxGearbox = 0, maxDiff = 0, tyreF = 0, tyreR = 0;
        string W(string slot) => $"{chassis.PartIn(slot)!.Wear * 100,6:F1}";
        while (session.Timer.Laps < laps && sim.State.Time < 200.0 * (laps + 1))
        {
            var step = session.Advance(0.1, default);
            var t = session.Last!;
            maxClutch = Math.Max(maxClutch, t.ClutchTemperatureC);
            maxFront = Math.Max(maxFront, t.BrakeTemperatureFrontC);
            maxRear = Math.Max(maxRear, t.BrakeTemperatureRearC);
            maxGearbox = Math.Max(maxGearbox, sim.Wear.GearboxLoadNm);
            maxDiff = Math.Max(maxDiff, sim.Wear.DifferentialLoadNm);
            tyreF = Math.Max(tyreF, Math.Max(t.TyreTemperatureC[0], t.TyreTemperatureC[1]));
            tyreR = Math.Max(tyreR, Math.Max(t.TyreTemperatureC[2], t.TyreTemperatureC[3]));
            foreach (var w in sim.Engine.Damage.Warnings.Concat(sim.Wear.Warnings)) warnings.Add(w.Code);
            if (trace > 0 && sim.State.Time >= nextTrace)
            {
                nextTrace += trace;
                Console.WriteLine($"  t={t.Time,6:F1} s={session.Track.DistanceAt(session.TrackIndex),6:F0}m off={session.LateralOffset,5:F1} {t.SpeedKmh,5:F0}km/h g{t.GearLabel} {t.EngineRpm,5:F0}rpm thr {t.Throttle:F2} brk {t.Brake:F2} steer {t.Steer * 57.3,5:F1}° clutch {t.Clutch:F2} slip {t.ClutchSlipRpm,5:F0} yaw {t.YawRate:F2} beta {t.BodySlipAngle * 57.3,5:F1}° {t.Engine.Torque,4:F0}Nm cap {t.ClutchCapacityNm:F0} {t.ClutchTemperatureC:F0}°C");
            }
            if (step.LapCompleted)
            {
                Console.WriteLine($"{session.Timer.Laps,4} {session.Timer.LastLap,7:F2} {maxClutch,8:F0} {maxFront,8:F0} {maxRear,8:F0} {W("clutch"),7} {W("brakes")} {W("tires_front")} {W("tires_rear")} {maxGearbox,6:F0} {maxDiff,6:F0} {tyreF,7:F0} {tyreR,7:F0} {t.TyrePressureKpa[0],4:F0}/{t.TyrePressureKpa[2],4:F0}  {string.Join(",", warnings)}");
                warnings.Clear();
                maxClutch = maxFront = maxRear = maxGearbox = maxDiff = tyreF = tyreR = 0;
            }
            foreach (var f in step.NewFailures) { Console.WriteLine(); Console.WriteLine(f.ToText()); }
            if (sim.Engine.Damage.Seized) break;
        }
        return session.Failures.Count > 0 ? 3 : 0;
    }

    /// <summary>
    /// Step-cost measurement for any engine family: the engine held at full throttle (2 ms steps, as in driving) and
    /// the whole car on autopilot. Best of <c>--repeats</c> runs of <c>--steps</c> steps after a warm-up; allocation is
    /// the managed bytes allocated per step on this thread.
    /// </summary>
    private static int Bench(CliOptions o)
    {
        int steps = (int)Num(o, "steps", 20000), repeats = (int)Num(o, "repeats", 5);
        double rpm = Num(o, "rpm", 5000);
        const double dt = 0.002;
        var (engineSim, _, _, _) = BuildEngine(o);
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = rpm, CoolantTemperatureOverride = 363.15 };
        var (engineUs, engineBytes) = Measure(() => engineSim.Step(dt, input), steps, repeats);
        Console.WriteLine($"{engineSim.Config.Assembly.Definition.Name}: {engineSim.Config.Geometry.Cylinders} cylinders, {Units.M3ToCc(engineSim.Config.Geometry.Displacement):F0} cc");
        Console.WriteLine($"  engine step  (WOT, {rpm:F0} rpm held): {engineUs,7:F2} µs  {engineBytes,8:F0} B/step  ({engineBytes * 500 / 1e6:F1} MB/s at 500 Hz)");
        BuiltCar built;
        try { built = BuildCar(o); }
        catch (ArgumentException e)
        {
            Console.WriteLine($"  vehicle step: skipped ({e.Message})");
            return 0;
        }
        var session = new CarSim.Gameplay.DrivingSession(built.Sim, CarSim.Core.Vehicles.TrackLayout.TestFacility()) { AutopilotEnabled = true };
        var (carUs, carBytes) = Measure(() => session.Advance(CarSim.Gameplay.DrivingSession.StepSeconds, default), steps, repeats);
        Console.WriteLine($"  vehicle step (autopilot lap, engine + chassis): {carUs,7:F2} µs  {carBytes,8:F0} B/step  ({carBytes * 500 / 1e6:F1} MB/s at 500 Hz)");
        return 0;
    }

    /// <summary>A-D1: the lower stage of a two-stage intake from a sourced switch speed (RunnerStageDerivation).</summary>
    private static int DeriveStage(CliOptions o)
    {
        var db = ContentLoader.LoadWithMods(o.ContentDir, ModsDir(o)).GetOrThrow();
        var engine = db.GetEngine(EngineId(o, db));
        var assembly = EngineAssembly.CreateStock(engine, db, new PartInstanceFactory());
        var geometry = EngineGeometry.TryCreate(assembly, 0, out _) ?? throw new InvalidOperationException("Incomplete geometry.");
        var intake = assembly.SpecFor<CarSim.Core.Parts.Specs.IntakeManifoldSpec>(PartCategory.IntakeManifold, 0)
                     ?? throw new InvalidOperationException("No intake manifold on the first bank.");
        var last = intake.AllStages()[^1];
        if (!o.Named.ContainsKey("switch")) return Fail("derive-stage needs --switch <rpm> (the sourced switch speed).");
        double switchRpm = Num(o, "switch", 0), upperMm = Num(o, "upper-length", last.RunnerLengthMm);
        double? diameterMm = o.Named.ContainsKey("diameter") ? Num(o, "diameter", 0) : last.RunnerDiameterMm;
        double k = Num(o, "k", IntakeGasDynamics.TunedFrequencyRatio), zeta = Num(o, "damping", IntakeGasDynamics.Damping);
        var d = RunnerStageDerivation.LowerStage(upperMm, diameterMm, geometry, switchRpm, k, zeta);
        double shownDiameter = diameterMm ?? Units.MToMm(RunnerStageConfiguration.DefaultDiameterPerBore * geometry.Bore);
        Console.WriteLine($"{engine.Name}: upper stage {upperMm:F1} mm × Ø{shownDiameter:F1} mm{(diameterMm == null ? " (default 0.40 × bore)" : "")}, " +
                          $"switch {switchRpm:F0} rpm, K {k}, ζ {zeta}, runner gas {IntakeGasDynamics.ReferenceTemperature} K");
        Console.WriteLine($"  upper stage tuned      {d.UpperTunedRpm,8:F1} rpm");
        Console.WriteLine($"  lower stage tuned      {d.LowerTunedRpm,8:F1} rpm");
        Console.WriteLine($"  lower stage length     {(d.LowerLengthMm is double l ? $"{l,8:F2} mm" : "none in 50–1000 mm")}");
        Console.WriteLine($"  gain crossover (check) {d.CrossoverRpm,8:F1} rpm");
        Console.WriteLine();
        Console.WriteLine("Sensitivity (each input moved alone):");
        void Row(string label, StageDerivation r) =>
            Console.WriteLine($"  {label,-26} upper {r.UpperTunedRpm,6:F0} rpm  lower {r.LowerTunedRpm,6:F0} rpm  length {(r.LowerLengthMm is double x ? $"{x:F1} mm" : "—")}");
        StageDerivation? Try(Func<StageDerivation> f) { try { return f(); } catch (ArgumentException) { return null; } }
        foreach (var (label, run) in new (string, Func<StageDerivation>)[]
        {
            ("switch 3,750 rpm (band low)", () => RunnerStageDerivation.LowerStage(upperMm, diameterMm, geometry, 3750, k, zeta)),
            ("switch 4,100 rpm (band high)", () => RunnerStageDerivation.LowerStage(upperMm, diameterMm, geometry, 4100, k, zeta)),
            ("K 2.0", () => RunnerStageDerivation.LowerStage(upperMm, diameterMm, geometry, switchRpm, 2.0, zeta)),
            ("K 2.2", () => RunnerStageDerivation.LowerStage(upperMm, diameterMm, geometry, switchRpm, 2.2, zeta)),
            ("ζ 0.33", () => RunnerStageDerivation.LowerStage(upperMm, diameterMm, geometry, switchRpm, k, 0.33)),
            ("ζ 0.45", () => RunnerStageDerivation.LowerStage(upperMm, diameterMm, geometry, switchRpm, k, 0.45)),
            ("upper 300 mm", () => RunnerStageDerivation.LowerStage(300, diameterMm, geometry, switchRpm, k, zeta)),
            ("upper 450 mm", () => RunnerStageDerivation.LowerStage(450, diameterMm, geometry, switchRpm, k, zeta)),
            ("diameter 30 mm", () => RunnerStageDerivation.LowerStage(upperMm, 30, geometry, switchRpm, k, zeta)),
            ("diameter 40 mm", () => RunnerStageDerivation.LowerStage(upperMm, 40, geometry, switchRpm, k, zeta)),
        })
        {
            if (Try(run) is { } r) Row(label, r);
            else Console.WriteLine($"  {label,-26} excluded: the upper stage then tunes below the switch speed");
        }
        return 0;
    }

    private static (double MicrosecondsPerStep, double BytesPerStep) Measure(Action step, int steps, int repeats)
    {
        for (int i = 0; i < Math.Min(steps, 2000); i++) step();
        double best = double.PositiveInfinity, bytes = 0;
        for (int r = 0; r < repeats; r++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < steps; i++) step();
            watch.Stop();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds * 1000.0 / steps);
            bytes = (double)(GC.GetAllocatedBytesForCurrentThread() - allocated) / steps;
        }
        return (best, bytes);
    }

    /// <summary>The regression fingerprint: run the matrix, compare with (or write) the baseline.</summary>
    private static int Fingerprint(CliOptions o)
    {
        var all = FingerprintMatrix.Cases;
        if (o.Named.ContainsKey("list"))
        {
            foreach (var c in all) Console.WriteLine($"{c.Id,-26} {c.Description}");
            return 0;
        }
        var cases = all.ToList();
        if (o.Named.TryGetValue("case", out var filter))
        {
            var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries);
            cases = all.Where(c => wanted.Any(w => c.Id == w || c.Id.StartsWith(w, StringComparison.Ordinal))).ToList();
            if (cases.Count == 0) return Fail($"No fingerprint case matches '{filter}' (carsim fingerprint --list 1).");
        }
        string? dump = o.Named.GetValueOrDefault("dump");
        int? jobs = o.Named.TryGetValue("jobs", out var j) ? int.Parse(j, System.Globalization.CultureInfo.InvariantCulture) : null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (o.Named.TryGetValue("write", out var writePath))
        {
            if (cases.Count != all.Count) return Fail("--write re-baselines the whole matrix; leave out --case.");
            var records = FingerprintRunner.Run(cases, schema: null, dumpDirectory: dump, parallelism: jobs);
            var baseline = FingerprintBaseline.FromRecords(cases.Zip(records));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(writePath))!);
            File.WriteAllText(writePath, baseline.ToText(all.Select(c => c.Id).ToList()));
            Console.WriteLine($"Wrote {writePath}: {cases.Count} cases, {baseline.Sections.Count} sections, {baseline.Schema.Count} channels ({watch.Elapsed.TotalSeconds:F1} s).");
            return 0;
        }
        string path = o.Named.GetValueOrDefault("check") ?? RepoPaths.FingerprintBaseline;
        var expected = FingerprintBaseline.Load(path);
        var run = FingerprintRunner.Run(cases, expected.Schema, dump, parallelism: jobs);
        var comparison = FingerprintComparison.Compare(expected, run);
        int sections = run.Sum(r => r.Sections.Count);
        Console.WriteLine($"Fingerprint: {cases.Count} cases, {sections} sections against {path} ({watch.Elapsed.TotalSeconds:F1} s).");
        foreach (var line in comparison.Changed) Console.WriteLine("  CHANGED " + line);
        foreach (var line in comparison.Missing) Console.WriteLine("  MISSING " + line);
        foreach (var line in comparison.Diagnostics) Console.WriteLine("  DIAGNOSTIC (knife edge, reported, not a regression) " + line);
        if (comparison.NewChannels.Count > 0)
            Console.WriteLine($"  new channels (not in the baseline's schema, not compared): {string.Join(", ", comparison.NewChannels)}");
        Console.WriteLine(comparison.Identical ? "  IDENTICAL: every digested value is bit-for-bit the baseline's."
            : "  DIFFERENT. A deliberate change is re-baselined with `carsim fingerprint --write tests/baselines/fingerprint.txt` and documented.");
        return comparison.Identical ? 0 : 4;
    }

    private static int FingerprintDiff(CliOptions o)
    {
        if (o.Positional.Count != 2) return Fail("Usage: carsim fingerprint-diff <before-dir> <after-dir>");
        Console.Write(FingerprintRunner.DiffDumps(o.Positional[0], o.Positional[1]));
        return 0;
    }

    /// <summary>The recalibration driver over the tune manifest.</summary>
    private static int RegenerateTunes(CliOptions o)
    {
        var manifest = TuneManifest.Load(o.Named.GetValueOrDefault("manifest") ?? RepoPaths.TuneManifest);
        var recipes = manifest.Tunes.ToList();
        if (o.Named.TryGetValue("tune", out var filter))
        {
            var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries);
            recipes = recipes.Where(r => wanted.Contains(r.Tune)).ToList();
            if (recipes.Count == 0) return Fail($"No tune in the manifest matches '{filter}'.");
        }
        bool write = o.Named.GetValueOrDefault("write") == "1";
        int jobs = o.Named.TryGetValue("jobs", out var j) ? int.Parse(j, System.Globalization.CultureInfo.InvariantCulture) : Environment.ProcessorCount;
        string root = RepoPaths.Root;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var results = new TuneRegeneration[recipes.Count];
        Parallel.For(0, recipes.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i => results[i] = TuneRegenerator.Run(recipes[i], root));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var r in results)
        {
            Console.WriteLine($"{r.Recipe.Tune} ({r.Recipe.Engine}, {r.Recipe.Fuel}; {string.Join(" → ", r.Recipe.Steps.Select(s => s.Calibrator))}; {r.Seconds:F0} s)");
            foreach (var f in r.Fields)
            {
                string cycle = f.Oscillating == 0 ? "" : $"; {f.Oscillating} in a rounding 2-cycle keep their checked-in value";
                string open = f.Unsettled == 0 ? "" : $"; WARNING: {f.Unsettled} moved again on a second pass (not settled)";
                Console.WriteLine(f.Reproduced ? $"  {f.Field}: reproduced ({f.Cells} values{cycle})"
                    : string.Create(inv, $"  {f.Field}: {f.Differing} of {f.Cells} values differ (max |Δ| {f.MaxAbsDifference:0.###}){cycle}{open}"));
            }
            foreach (var a in r.Audit) Console.WriteLine("  audit: " + a);
            if (!r.FileRoundTrips) Console.WriteLine("  WARNING: rewriting the file with its own values would change it (format drift); --write would reformat these tables.");
            if (write && !r.Reproduced)
            {
                string path = Path.Combine(root, r.Recipe.File);
                File.WriteAllText(path, TuneRegenerator.Write(File.ReadAllText(path), r.Recipe.Tune, r.Regenerated, r.Fields.Select(f => f.Field)));
                Console.WriteLine($"  wrote {r.Recipe.File}");
            }
        }
        int reproduced = results.Count(r => r.Reproduced);
        Console.WriteLine($"{reproduced} of {results.Length} tunes reproduced exactly ({watch.Elapsed.TotalSeconds:F0} s).");
        return 0;
    }

    private static void SwapPart(EngineAssembly a, string slot, PartDefinition part, PartInstanceFactory factory)
    {
        var removed = new Stack<(string, PartInstance)>();
        foreach (var s in a.RemovalSequenceFor(slot))
        {
            var r = a.Remove(s, out var p);
            if (!r.Ok) throw new ArgumentException(r.Message);
            removed.Push((s, p!));
        }
        if (a.IsInstalled(slot)) a.Remove(slot, out _);
        var ir = a.Install(slot, factory.Create(part));
        if (!ir.Ok) throw new ArgumentException(ir.Message);
        while (removed.Count > 0) { var (s, p) = removed.Pop(); a.Install(s, p); }
    }

    /// <summary>Loads the content with its errors (a check reports them) and checks one engine.</summary>
    private static int CheckEngine(CliOptions o)
    {
        if (o.Positional.FirstOrDefault() is not { } engineId)
            return Fail("check-engine needs an engine id (carsim check-engine <engine-id>).");
        var load = ContentLoader.LoadWithMods(o.ContentDir, ModsDir(o));
        var report = EngineCheck.Run(load, engineId);
        bool strict = o.Named.GetValueOrDefault("strict") is "1" or "true";
        Console.Write(report.ToText(verbose: o.Named.GetValueOrDefault("verbose") is "1" or "true", strict: strict));
        return report.ExitCode(strict);
    }

    private static string FeatureStatusLabel(FeatureStatus s) => s switch
    {
        FeatureStatus.Supported => "supported",
        FeatureStatus.NotModelled => "not modelled",
        FeatureStatus.MissingData => "missing data",
        _ => "undeclared",
    };

    private static int Inspect(CliOptions o)
    {
        var db = ContentLoader.LoadWithMods(o.ContentDir, ModsDir(o)).GetOrThrow();
        var engine = db.GetEngine(EngineId(o, db));
        var assembly = EngineAssembly.CreateStock(engine, db, new PartInstanceFactory());

        Console.WriteLine($"{engine.Name} [{engine.Id}]");
        Console.WriteLine();
        Console.WriteLine("Architecture:");
        Console.WriteLine($"  {string.Join(", ", EngineCapabilities.Resolve(assembly).Describe())}");
        if (engine.Banks.Count > 1)
            foreach (var bank in engine.Banks)
                Console.WriteLine($"  bank '{bank.Id}': cylinders {string.Join(", ", bank.Cylinders)}");
        Console.WriteLine();
        Console.WriteLine("Stock build (assembly order):");
        foreach (var slot in engine.AssemblyOrder())
        {
            var part = assembly.PartIn(slot.Id);
            string banks = slot.Banks.Count > 0 && engine.Banks.Count > 1 ? $" [{string.Join(", ", slot.Banks)}]" : "";
            Console.WriteLine($"  {slot.Label + banks,-28} {(part == null ? "(empty)" : part.Definition.Name)}");
        }

        var g = EngineGeometry.TryCreate(assembly, out _);
        if (g != null)
        {
            Console.WriteLine();
            Console.WriteLine("Geometry:");
            Console.WriteLine($"  Displacement        {Units.M3ToCc(g.Displacement),8:F1} cc");
            Console.WriteLine($"  Bore × stroke       {Units.MToMm(g.Bore):F1} × {Units.MToMm(g.Stroke):F1} mm");
            Console.WriteLine($"  Rod ratio           {g.RodRatio,8:F3}");
            Console.WriteLine($"  Compression ratio   {g.CompressionRatio,8:F2} :1");
            Console.WriteLine($"  Deck clearance      {Math.Round(Units.MToMm(g.DeckClearance), 2) + 0.0,8:F2} mm");
            Console.WriteLine($"  Piston-to-head      {Units.MToMm(g.PistonToHeadClearance),8:F2} mm");
            Console.WriteLine($"  Recip. mass / cyl   {g.ReciprocatingMass * 1000,8:F0} g");
            // A bank with its own head or gasket has its own chamber: list each bank when they differ.
            var banks = Enumerable.Range(0, engine.Banks.Count).Select(b => EngineGeometry.TryCreate(assembly, b, out _)).ToList();
            if (banks.Distinct().Count() > 1)
                for (int b = 0; b < banks.Count; b++)
                    if (banks[b] is { } bg)
                        Console.WriteLine($"  Bank {engine.Banks[b].Id,-14} CR {bg.CompressionRatio:F2}:1, piston-to-head {Units.MToMm(bg.PistonToHeadClearance):F2} mm");
        }

        var report = AssemblyValidator.Validate(assembly);
        // Intake gas dynamics: each bank's runner stages and the speed each tunes to (runner gas at the reference ambient).
        if (report.CanRun && EngineConfiguration.Build(assembly, db.Fuels.Values.First()).Configuration is { } built)
        {
            Console.WriteLine();
            Console.WriteLine($"Intake runner stages (tuned speed with the runner gas at {IntakeGasDynamics.ReferenceTemperature:F2} K):");
            foreach (var bank in built.Banks)
                for (int s = 0; s < bank.RunnerStages.Count; s++)
                {
                    var stage = bank.RunnerStages[s];
                    string which = built.Banks.Count > 1 ? $"bank {bank.Definition.Id}, " : "";
                    Console.WriteLine($"  {which}stage {s}: L {Units.MToMm(stage.Length):F1} mm, d {Units.MToMm(stage.Diameter):F1} mm{(stage.DefaultDiameter ? " (default)" : "")}, " +
                                      $"β {stage.VolumeRatio:F3}, x {stage.Fundamental:F4}, tuned {stage.TunedRpmAtReference:F0} rpm");
                }
        }
        Console.WriteLine();
        Console.WriteLine($"Compatibility: {(report.CanRun ? "can run" : "CANNOT RUN")}");
        foreach (var issue in report.Issues) Console.WriteLine($"  {issue}");
        Console.WriteLine();
        Console.WriteLine($"Total engine mass: {assembly.TotalMassKg:F1} kg");

        // Identity, declared features and provenance coverage (metadata; the physics never reads them).
        Console.WriteLine();
        var id = engine.Identity;
        string who = id == null ? "" : string.Join(" ", new[] { id.Manufacturer, id.Variant ?? id.Family }.Where(s => !string.IsNullOrEmpty(s)));
        Console.WriteLine(id == null ? "Identity: not recorded"
            : $"Identity: {id.Kind}{(who.Length > 0 ? ", " + who : "")}{(engine.Extends != null ? $", variant of '{engine.Extends}'" : "")}");
        var features = FeatureReport.For(id, EngineCapabilities.Resolve(assembly));
        if (features.Count > 0)
        {
            Console.WriteLine("Features:");
            foreach (var f in features)
                Console.WriteLine($"  {f.Feature,-30} {FeatureStatusLabel(f.Status),-13}{(f.Approximation != null ? $" stands in: {f.Approximation}" : "")}");
        }
        var parts = assembly.AllParts.Select(p => p.Definition).DistinctBy(p => p.Id).ToList();
        int fields = parts.Sum(p => p.AuthoredSpecFields.Count), recorded = parts.Sum(p => p.Provenance.Keys.Count(k => k != "mass_kg"));
        var byType = parts.SelectMany(p => p.Provenance.Values).GroupBy(v => v.Type).OrderBy(gr => gr.Key, StringComparer.Ordinal)
            .Select(gr => $"{gr.Key} {gr.Count()}");
        Console.WriteLine($"Provenance: {recorded} of {fields} authored spec values of the stock parts have a record" +
                          (recorded > 0 ? $" ({string.Join(", ", byType)}, mass included)" : ""));
        return 0;
    }
}

/// <summary>Minimal argument parsing: <c>--content dir</c> plus positional arguments.</summary>
public sealed class CliOptions
{
    public string ContentDir { get; private set; } = DefaultContentDir();
    public List<string> Positional { get; } = new();
    public Dictionary<string, string> Named { get; } = new(StringComparer.Ordinal);

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Option {args[i]} needs a value.");
                string key = args[i][2..];
                string value = args[++i];
                if (key == "content") o.ContentDir = value;
                else o.Named[key] = value;
            }
            else o.Positional.Add(args[i]);
        }
        return o;
    }

    /// <summary>Finds content/base by walking up from the working directory, then from the executable.</summary>
    private static string DefaultContentDir()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "content", "base");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "content", "base");
    }
}
