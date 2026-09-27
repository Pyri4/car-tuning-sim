using CarSim.Core.Content;
using CarSim.Core.Damage;
using CarSim.Core.Dyno;
using CarSim.Core.Ecu;
using CarSim.Core.Engines;
using CarSim.Core.Parts;
using CarSim.Core.Parts.Specs;
using CarSim.Core.Simulation;
using CarSim.Core.Vehicles;
using CarSim.Gameplay;
using CarSim.Verification.Calibration;

namespace CarSim.Verification.Fingerprint;

/// <summary>One entry of the regression matrix: a build, a fuel, a tune and the sections run on it.</summary>
public sealed record FingerprintCase(string Id, string Description, Action<FingerprintContent, FingerprintRecorder> Run);

/// <summary>The content the matrix runs on: the base game, and the base game plus the synthetic matrix layer.</summary>
public sealed class FingerprintContent
{
    private readonly Lazy<ContentDatabase> _base, _withTest;

    public FingerprintContent(string repoRoot)
    {
        RepoRoot = repoRoot;
        string baseDir = Path.Combine(repoRoot, "content", "base"), testDir = Path.Combine(repoRoot, "content", "test");
        _base = new(() => Warm(ContentLoader.LoadDirectory(baseDir).GetOrThrow()));
        _withTest = new(() => Warm(ContentLoader.LoadWithMods(baseDir, testDir).GetOrThrow()));
    }

    /// <summary>Fills the definitions' lazy lookups once, before cases share them across threads.</summary>
    private static ContentDatabase Warm(ContentDatabase db)
    {
        foreach (var engine in db.Engines.Values)
        {
            _ = engine.Banks.Count;
            foreach (var slot in engine.Slots) _ = engine.FindSlot(slot.Id);
        }
        return db;
    }

    public string RepoRoot { get; }

    public ContentDatabase Base => _base.Value;
    public ContentDatabase WithTestLayers => _withTest.Value;
}

/// <summary>
/// The regression fingerprint's build matrix (docs/VERIFICATION.md). Real engines are regression content: every K20 and
/// M54 build is run through steady-state WOT and part-load sweeps, a cold start, both dyno modes, an abuse hold, the
/// failure holds of the damage tests, the game's scenarios and autopilot laps. The synthetic matrix is represented by
/// every family's WOT and part-load sweeps and dyno pull, and a lap for the twin-turbo V6 swapped into a car. Cases only
/// read the content; nothing here knows an engine's physics.
/// </summary>
public static class FingerprintMatrix
{
    private const string K20 = "kestrel_k20", M54 = "isar_m54";

    public static IReadOnlyList<FingerprintCase> Cases { get; } = Build();

    public static FingerprintCase Get(string id) => Cases.FirstOrDefault(c => c.Id == id)
        ?? throw new KeyNotFoundException($"Unknown fingerprint case '{id}'.");

    private static List<FingerprintCase> Build()
    {
        var cases = new List<FingerprintCase>
        {
            // ---- Kestrel K20 (pinned: bit-identical unless a documented generic correction changes it) -----------
            Engine("k20_stock_95", "K20 stock, RON 95, factory tune", c => Stock(c.Base, K20), "gasoline_95", null, Full),
            Engine("k20_stock_98", "K20 stock, RON 98", c => Stock(c.Base, K20), "gasoline_98", null, Sweeps),
            Engine("k20_short_runner_95", "K20 with the short-runner intake (runner-length response)",
                c => Swapped(c.Base, K20, ("intake_manifold", "k20.intake.short_runner")), "gasoline_95", null, Sweeps),
            Engine("k20_na_built_98", "K20 NA build: race cams, performance springs, ported head, 4-2-1 header, 76 mm exhaust",
                c => Swapped(c.Base, K20, ("valve_springs", "k20.valve_springs.performance"), ("camshafts", "k20.cams.race"),
                    ("cylinder_head", "k20.head.ported"), ("exhaust_manifold", "k20.exhaust_manifold.header421"),
                    ("exhaust", "exhaust.race_76mm")), "gasoline_98", null, Sweeps),
            Engine("k20_t28_98", "K20 T28 turbo build (the turbo tests' recipe), turbo base tune, RON 98",
                c => TurboBuild(c.Base, "turbo.t28_ball"), "gasoline_98", "k20.turbo_base", Full),
            Engine("k20_t28_95", "K20 T28 turbo build on RON 95 (knock-limited)", c => TurboBuild(c.Base, "turbo.t28_ball"), "gasoline_95",
                "k20.turbo_base", Sweeps),
            Engine("k20_t35_98", "K20 T35 turbo build (tubular manifold)", c => TurboBuild(c.Base, "turbo.t35_big"), "gasoline_98",
                "k20.turbo_base", Sweeps),
            // The recalibration driver on the K20 factory tune's recipe: pins the dev calibrators' output (tune regeneration).
            new("k20_regen_stock_ve", "Tune regeneration: k20.stock's recipe (calibrate-ve on the stock engine, RON 95)", (c, r) =>
            {
                var recipe = TuneManifest.Load(Path.Combine(c.RepoRoot, "tools", "CarSim.Verification", "tune-manifest.json")).Tunes.Single(t => t.Tune == "k20.stock");
                var result = TuneRegenerator.Run(recipe, c.RepoRoot);
                r.BeginSection("regen");
                foreach (var field in result.Fields)
                {
                    r.Sample(field.Regenerated, field.Field);
                    r.Value(field.Field + ".differing_from_checked_in", field.Differing);
                    r.Value(field.Field + ".max_abs_difference", field.MaxAbsDifference);
                }
                foreach (var line in result.Audit) r.Text("audit", line);
            }),

            // ---- Isar M54 (the pre-change baseline of Intake Gas Dynamics 2.0) -------------------------------------
            Engine("m54_stock_98", "M54 stock, RON 98, factory tune with VANOS", c => Stock(c.Base, M54), "gasoline_98", null, Full),
            Engine("m54_stock_95", "M54 stock, RON 95 (knock control active)", c => Stock(c.Base, M54), "gasoline_95", null, Sweeps),
            Engine("m54_parked_98", "M54 with its phaser parked (no cam table): the fixed-cam filling curve",
                c => Stock(c.Base, M54), "gasoline_98", null, Sweeps, tune => tune with { IntakeCamAdvanceDeg = null }),
        };

        // ---- Failure holds (the damage tests' recipes on both families) -----------------------------------------------
        cases.Add(Hold("fail_k20_overrev", "K20 over-rev: 9,300 rpm closed throttle", c => Sim(c.Base, Stock(c.Base, K20), "gasoline_95", null),
            (r, s) => { HoldAt(r, s, 7000, 1); HoldAt(r, s, 9300, 20, throttle: 0); HoldAt(r, s, 3000, 0.01); }));
        cases.Add(Hold("fail_k20_detonation", "K20 12° over-advanced on RON 91, knock control off",
            c => Sim(c.Base, Stock(c.Base, K20), "gasoline_91", null, t => { t.KnockControlEnabled = false; t.OffsetIgnition(12, 90); }),
            (r, s) => HoldAt(r, s, 2500, 300)));
        cases.Add(Hold("fail_k20_turbo_lean", "K20 turbo on the stock ECU (MAP sensor saturates, runs lean)",
            c =>
            {
                var a = Swapped(c.Base, K20, ("exhaust_manifold", "k20.exhaust_manifold.turbo_log"));
                Check(a.Install("turbocharger", new PartInstanceFactory(8_000_000).Create(c.Base.GetPart("turbo.t28_ball"))));
                return Sim(c.Base, a, "gasoline_95", null);
            },
            (r, s) => HoldAt(r, s, 5500, 120)));
        cases.Add(Hold("fail_k20_oil_starvation", "K20 1.25 g sustained cornering at 6,500 rpm", c => Sim(c.Base, Stock(c.Base, K20), "gasoline_95", null),
            (r, s) => HoldAt(r, s, 6500, 300, sumpG: 1.25)));
        cases.Add(Hold("fail_k20_overheat", "K20 6,500 rpm WOT with 0.5 m/s cooling air", c => Sim(c.Base, Stock(c.Base, K20), "gasoline_95", null),
            (r, s) => { HoldAt(r, s, 5000, 1); HoldAt(r, s, 6500, 2000, coolantK: null, airSpeed: 0.5, dt: 0.01); HoldAt(r, s, 5000, 1); }));
        cases.Add(Hold("fail_m54_overrev", "M54 over-rev: 9,000 rpm closed throttle", c => Sim(c.Base, Stock(c.Base, M54), "gasoline_98", null),
            (r, s) => { HoldAt(r, s, 6000, 1); HoldAt(r, s, 9000, 20, throttle: 0); HoldAt(r, s, 3000, 0.01); }));
        cases.Add(Hold("fail_m54_detonation", "M54 12° over-advanced on RON 91, knock control off",
            c => Sim(c.Base, Stock(c.Base, M54), "gasoline_91", null, t => { t.KnockControlEnabled = false; t.OffsetIgnition(12, 90); }),
            (r, s) => HoldAt(r, s, 2500, 300)));
        cases.Add(Hold("fail_m54_oil_starvation", "M54 1.25 g sustained cornering at 6,000 rpm", c => Sim(c.Base, Stock(c.Base, M54), "gasoline_98", null),
            (r, s) => HoldAt(r, s, 6000, 300, sumpG: 1.25)));
        cases.Add(Hold("fail_m54_overheat", "M54 6,000 rpm WOT with 0.5 m/s cooling air", c => Sim(c.Base, Stock(c.Base, M54), "gasoline_98", null),
            (r, s) => { HoldAt(r, s, 5000, 1); HoldAt(r, s, 6000, 2000, coolantK: null, airSpeed: 0.5, dt: 0.01); HoldAt(r, s, 5000, 1); }));

        // ---- The game's scenarios (worn project car; the M54 car) and autopilot laps ------------------------------------
        cases.Add(Scenario("scenario_project_car", "Scenario 'project_car' (worn K20): dyno pull and a lap", "project_car"));
        cases.Add(Scenario("scenario_isar_c30_six", "Scenario 'isar_c30_six' (M54 car): dyno pull and a lap", "isar_c30_six"));
        cases.Add(Drive("drive_k20_stock", "Kestrel S2, stock K20, RON 95: out lap + 2 laps", c => c.Base, c => Stock(c.Base, K20), "gasoline_95", null,
            "kestrel_s2", laps: 2));
        cases.Add(Drive("drive_k20_t28", "Kestrel S2, K20 T28 build, RON 98: out lap + 2 laps", c => c.Base, c => TurboBuild(c.Base, "turbo.t28_ball"),
            "gasoline_98", "k20.turbo_base", "kestrel_s2", laps: 2));
        cases.Add(Drive("drive_m54_stock", "Isar C30, stock M54, RON 98: out lap + 2 laps", c => c.Base, c => Stock(c.Base, M54), "gasoline_98", null,
            "isar_c30", laps: 2));

        // ---- Synthetic engine matrix (content/test): every family, on the fuel its tune was calibrated on ----------------
        foreach (var (family, fuel) in new[]
                 {
                     ("syn_i4_sohc", "gasoline_95"), ("syn_i3_turbo_vvl", "gasoline_98"), ("syn_i4_turbo", "gasoline_98"), ("syn_i6_vis", "gasoline_95"),
                     ("syn_h4_turbo", "gasoline_98"), ("syn_v6_na", "gasoline_95"), ("syn_v6_tt", "gasoline_98"), ("syn_v8_ohv", "gasoline_95"),
                     ("syn_v8_dohc_vvt", "gasoline_98"),
                 })
            cases.Add(Engine(family, $"Synthetic family {family}, its calibrated tune on {fuel}", c => Stock(c.WithTestLayers, family), fuel,
                null, Synthetic, content: c => c.WithTestLayers));
        cases.Add(Drive("drive_syn_v6_tt_swap", "Twin-turbo V6 swapped into the Isar C30, RON 98: out lap + 1 lap", c => c.WithTestLayers,
            c => Stock(c.WithTestLayers, "syn_v6_tt"), "gasoline_98", null, "isar_c30", laps: 1));
        return cases;
    }

    // ---- Sections ------------------------------------------------------------------------------------------------------

    [Flags]
    private enum Sections
    {
        Geometry = 1, Wot = 2, PartLoad = 4, Cold = 8, DynoSweep = 16, DynoSteady = 32, Abuse = 64,
    }

    private const Sections Full = Sections.Geometry | Sections.Wot | Sections.PartLoad | Sections.Cold | Sections.DynoSweep | Sections.DynoSteady | Sections.Abuse;
    private const Sections Sweeps = Sections.Wot | Sections.PartLoad | Sections.DynoSweep;
    private const Sections Synthetic = Sections.Geometry | Sections.Wot | Sections.PartLoad | Sections.DynoSweep;

    private static FingerprintCase Engine(string id, string description, Func<FingerprintContent, EngineAssembly> build, string fuel, string? tune,
        Sections sections, Func<TuneDocument, TuneDocument>? editTune = null, Func<FingerprintContent, ContentDatabase>? content = null) =>
        new(id, description, (c, r) =>
        {
            var db = content?.Invoke(c) ?? c.Base;
            EngineSimulation New(EngineState? state = null) => Sim(db, build(c), fuel, tune, state: state, document: editTune);
            if (sections.HasFlag(Sections.Geometry)) Geometry(r, build(c));
            var probe = New();
            double top = Math.Ceiling((probe.Ecu.Tune.RevLimitRpm + 500) / 250.0) * 250.0;
            if (sections.HasFlag(Sections.Wot)) Wot(r, New(), top);
            if (sections.HasFlag(Sections.PartLoad)) PartLoad(r, New(), probe.Ecu.Tune.RevLimitRpm);
            if (sections.HasFlag(Sections.Cold)) Cold(r, New(new EngineState()));
            if (sections.HasFlag(Sections.DynoSweep)) Dyno(r, New(), DynoMode.Sweep);
            if (sections.HasFlag(Sections.DynoSteady)) Dyno(r, New(), DynoMode.SteadyState);
            if (sections.HasFlag(Sections.Abuse)) Abuse(r, New());
        });

    private static FingerprintCase Hold(string id, string description, Func<FingerprintContent, EngineSimulation> build,
        Action<FingerprintRecorder, EngineSimulation> run) =>
        new(id, description, (c, r) =>
        {
            var sim = build(c);
            r.BeginSection("hold");
            run(r, sim);
            DamageSummary(r, sim);
        });

    private static FingerprintCase Scenario(string id, string description, string scenario) =>
        new(id, description, (c, r) =>
        {
            var (engine, _) = Garage.NewGame(c.Base, scenario).CreateSimulation();
            Dyno(r, engine ?? throw new InvalidOperationException($"Scenario '{scenario}' does not start."), DynoMode.Sweep);
            var (car, problem) = Garage.NewGame(c.Base, scenario).CreateVehicleSimulation();
            if (car == null) throw new InvalidOperationException($"Scenario '{scenario}' cannot drive: {problem}");
            Laps(r, car, laps: 1);
        });

    private static FingerprintCase Drive(string id, string description, Func<FingerprintContent, ContentDatabase> content,
        Func<FingerprintContent, EngineAssembly> build, string fuel, string? tune, string vehicleId, int laps) =>
        new(id, description, (c, r) =>
        {
            var db = content(c);
            var engine = Sim(db, build(c), fuel, tune);
            var vehicle = db.GetVehicle(vehicleId);
            var chassis = VehicleAssembly.CreateStock(vehicle, db, new PartInstanceFactory(9_000_000));
            var problems = VehicleCompatibility.InterfaceProblems(engine.Config.Assembly, chassis);
            if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems));
            var car = new VehicleSimulation(new VehicleConfiguration(vehicle, chassis, engine.Config.Assembly, db), engine);
            car.StartIdling();
            Laps(r, car, laps);
            r.BeginSection("wear");
            foreach (var (slot, part) in chassis.Installed.OrderBy(p => p.Key, StringComparer.Ordinal))
                r.Value($"{slot}.wear", part.Wear);
            DamageSummary(r, engine);
        });

    private static void Geometry(FingerprintRecorder r, EngineAssembly a)
    {
        r.BeginSection("geometry");
        var g = EngineGeometry.TryCreate(a, out _) ?? throw new InvalidOperationException("Incomplete geometry.");
        r.Sample(g);
        for (int b = 0; b < a.Definition.Banks.Count; b++) r.Sample(EngineGeometry.TryCreate(a, b, out _), $"bank{b}");
        r.Value("displacement_cc", CarSim.Core.Common.Units.M3ToCc(g.Displacement));
        r.Value("compression_ratio", g.CompressionRatio);
        r.Value("mass_kg", a.TotalMassKg);
        r.Text("issues", string.Join(" | ", AssemblyValidator.Validate(a).Issues.Select(i => i.ToString())));
    }

    private static void Wot(FingerprintRecorder r, EngineSimulation sim, double top)
    {
        r.BeginSection("wot");
        var points = SteadyStateSweep.Run(sim, 1000, top, 250, settleSeconds: 1.0);
        foreach (var p in points) r.Sample(p);
        foreach (var p in points.Where(p => Math.Abs(p.Rpm % 500) < 1e-6))
        {
            string at = $"@{p.Rpm:0}";
            r.Value("torque_nm" + at, p.Torque);
            r.Value("power_kw" + at, p.PowerKw);
            r.Value("ve" + at, p.VolumetricEfficiency);
            r.Value("ve_dynamic" + at, p.VeDynamic);
            r.Value("lambda" + at, p.Lambda);
        }
        Peaks(r, points);
    }

    private static void PartLoad(FingerprintRecorder r, EngineSimulation sim, double revLimit)
    {
        r.BeginSection("partload");
        for (double rpm = 1000; rpm <= revLimit - 500 + 1e-6; rpm += 1000)
            foreach (double throttle in new[] { 0.02, 0.1, 0.2, 0.35, 0.5, 0.75 })
                r.Sample(SteadyStateSweep.Settle(sim, rpm, throttle, 1.0), $"{rpm:0}/{throttle:0.00}");
    }

    /// <summary>Crank from ambient, idle on the radiator, a part-throttle and a full-throttle blip, idle.</summary>
    private static void Cold(FingerprintRecorder r, EngineSimulation sim)
    {
        r.BeginSection("cold");
        const double dt = 0.002;
        double firstFire = double.NaN;
        for (int i = 0; i < (int)(40 / dt); i++)
        {
            double t = i * dt;
            var input = new EngineInputs
            {
                Throttle = t is >= 30 and < 31 ? 0.5 : t is >= 33 and < 33.4 ? 1.0 : 0.0,
                Starter = t < 1.5, SpeedMode = SpeedMode.Free, CoolingAirSpeed = 2.0,
            };
            var tel = sim.Step(dt, input);
            if (double.IsNaN(firstFire) && tel.Firing) firstFire = tel.Time;
            if (i % 50 == 0) r.Sample(tel);
            if (i == (int)(10 / dt) || i == (int)(29 / dt)) r.Value($"rpm@{t:0}s", tel.Rpm);
        }
        r.Value("first_fire_s", firstFire);
        r.Value("coolant_c@40s", sim.State.CoolantTemperature - 273.15);
        DamageSummary(r, sim);
    }

    private static void Dyno(FingerprintRecorder r, EngineSimulation sim, DynoMode mode)
    {
        r.BeginSection(mode == DynoMode.Sweep ? "dyno_sweep" : "dyno_steady");
        var runner = new DynoRunner(sim, new DynoSettings { Mode = mode }, "fingerprint");
        var run = runner.RunToCompletion();
        foreach (var s in run.Samples) r.Sample(s);
        r.Text("phase", runner.Phase.ToString());
        r.Text("abort", runner.AbortReason);
        r.Text("note", runner.Note);
        Peaks(r, run.Samples.Where(s => s.Firing).ToList());
    }

    /// <summary>WOT at 6,400 rpm on the radiator at walking-pace air speed, until a failure or 60 s.</summary>
    private static void Abuse(FingerprintRecorder r, EngineSimulation sim)
    {
        r.BeginSection("abuse");
        const double dt = 0.005;
        var input = new EngineInputs { Throttle = 1, SpeedMode = SpeedMode.Held, HeldRpm = 6400, CoolingAirSpeed = 1.5 };
        for (int i = 0; i < (int)(60 / dt); i++)
        {
            var tel = sim.Step(dt, input);
            if (i % 200 == 0 || sim.Damage.Failures.Count > 0) r.Sample(tel);
            if (sim.Damage.Failures.Count > 0) break;
        }
        r.Value("coolant_c", sim.State.CoolantTemperature - 273.15);
        DamageSummary(r, sim);
    }

    private static void HoldAt(FingerprintRecorder r, EngineSimulation sim, double rpm, double seconds, double throttle = 1.0,
        double sumpG = 0.0, double? coolantK = 363.15, double airSpeed = 10.0, double dt = 0.005)
    {
        var input = new EngineInputs
        {
            Throttle = throttle, SpeedMode = SpeedMode.Held, HeldRpm = rpm, CoolantTemperatureOverride = coolantK,
            SumpAccelerationG = sumpG, CoolingAirSpeed = airSpeed,
        };
        var t = sim.Step(dt, input);
        int steps = (int)(seconds / dt);
        for (int i = 0; i < steps && sim.Damage.Failures.Count == 0; i++)
        {
            t = sim.Step(dt, input);
            if (i % 400 == 0) r.Sample(t);
        }
        r.Sample(t, "last");
    }

    private static void Laps(FingerprintRecorder r, VehicleSimulation car, int laps)
    {
        r.BeginSection("drive");
        var session = new DrivingSession(car, TrackLayout.TestFacility()) { AutopilotEnabled = true };
        int frame = 0;
        while (session.Timer.Laps < laps && car.State.Time < 200.0 * (laps + 1))
        {
            var step = session.Advance(0.1, default);
            r.Sample(session.Last);
            foreach (var f in step.NewFailures) r.Text($"failure@{frame}", f.ToText());
            if (step.LapCompleted) r.Value($"lap{session.Timer.Laps}_s", session.Timer.LastLap ?? double.NaN);
            frame++;
            if (car.Engine.Damage.Seized) break;
        }
        r.Value("laps", session.Timer.Laps);
        r.Value("autopilot_recoveries", session.AutopilotRecoveries);
    }

    private static void Peaks(FingerprintRecorder r, IReadOnlyList<EngineTelemetry> points)
    {
        if (points.Count == 0) return;
        var power = points.MaxBy(p => p.Power)!;
        var torque = points.MaxBy(p => p.Torque)!;
        r.Value("peak_power_kw", power.PowerKw);
        r.Value("peak_power_rpm", power.Rpm);
        r.Value("peak_torque_nm", torque.Torque);
        r.Value("peak_torque_rpm", torque.Rpm);
    }

    private static void DamageSummary(FingerprintRecorder r, EngineSimulation sim)
    {
        for (int i = 0; i < sim.Damage.Failures.Count; i++)
        {
            var f = sim.Damage.Failures[i];
            r.Text($"failure{i}", f.ToText());
        }
        r.Text("seized", sim.Damage.Seized ? "true" : "false");
        r.Text("warnings", string.Join(",", sim.Damage.Warnings.Select(w => w.Code).OrderBy(c => c, StringComparer.Ordinal)));
        foreach (var part in sim.Config.Assembly.AllParts.OrderBy(p => p.Definition.Id, StringComparer.Ordinal).ThenBy(p => p.InstanceId, StringComparer.Ordinal))
        {
            r.Value($"{part.Definition.Id}.wear", part.Wear);
            r.Value($"{part.Definition.Id}.fatigue", part.Damage.MaxFatigue);
            r.Text($"{part.Definition.Id}.inspection", string.Join(" ", PartInspector.Inspect(part).Select(f => $"{f.Severity}:{f.Text}")));
        }
    }

    // ---- Builds --------------------------------------------------------------------------------------------------------

    private static EngineAssembly Stock(ContentDatabase db, string engine) =>
        EngineAssembly.CreateStock(db.GetEngine(engine), db, new PartInstanceFactory());

    private static EngineAssembly Swapped(ContentDatabase db, string engine, params (string Slot, string Part)[] swaps)
    {
        var a = Stock(db, engine);
        var factory = new PartInstanceFactory(1_000_000);
        foreach (var (slot, part) in swaps)
        {
            var removed = new Stack<(string Slot, PartInstance Part)>();
            foreach (var s in a.RemovalSequenceFor(slot))
            {
                var result = a.Remove(s, out var p);
                Check(result);
                removed.Push((s, p!));
            }
            if (a.IsInstalled(slot)) Check(a.Remove(slot, out _));
            Check(a.Install(slot, factory.Create(db.GetPart(part))));
            while (removed.Count > 0)
            {
                var (s, p) = removed.Pop();
                Check(a.Install(s, p));
            }
        }
        return a;
    }

    /// <summary>The turbo tests' build: forged, fuelled, standalone ECU, log manifold (tubular above a 70 mm wheel), FMIC.</summary>
    private static EngineAssembly TurboBuild(ContentDatabase db, string turbo)
    {
        string manifold = db.GetPart(turbo).GetSpec<TurbochargerSpec>().CompressorWheelDiameterMm > 70
            ? "k20.exhaust_manifold.turbo_t3_tubular" : "k20.exhaust_manifold.turbo_log";
        var a = Swapped(db, K20, ("pistons", "k20.pistons.forged_lc"), ("connecting_rods", "k20.rods.forged_h"),
            ("head_gasket", "k20.head_gasket.race"), ("injectors", "injectors.550cc"), ("fuel_pump", "fuel_pump.hf_330"),
            ("ecu", "ecu.standalone"), ("exhaust", "exhaust.sport_63mm"), ("exhaust_manifold", manifold));
        var factory = new PartInstanceFactory(5_000_000);
        Check(a.Install("turbocharger", factory.Create(db.GetPart(turbo))));
        Check(a.Install("intercooler", factory.Create(db.GetPart("intercooler.fmic_street"))));
        return a;
    }

    private static EngineSimulation Sim(ContentDatabase db, EngineAssembly a, string fuel, string? tuneId, Action<EcuTune>? edit = null,
        EngineState? state = null, Func<TuneDocument, TuneDocument>? document = null)
    {
        var doc = db.GetTune(tuneId ?? a.Definition.StockTune);
        if (document != null) doc = document(doc);
        var tune = EcuTune.FromDocument(doc);
        edit?.Invoke(tune);
        var config = EngineConfiguration.Build(a, db.GetFuel(fuel), new ValidationContext(tune.RevLimitRpm, tune.MaxBoostTargetKpa)).GetOrThrow();
        return new EngineSimulation(config, tune, state ?? EngineState.Warm());
    }

    private static void Check(AssemblyResult result)
    {
        if (!result.Ok) throw new InvalidOperationException(result.Message);
    }
}
