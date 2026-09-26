# Architecture

## Status
**Decided (2026-09-26).** Engine/framework: **Godot 4.7 (.NET edition) for presentation, with an
game-engine-agnostic C# (.NET 8) simulation core** (no Godot dependency; the *car*-engine model's own limits are
in `EngineTopology` and SIMULATION_SPEC.md). Sections below marked *(planned)* describe intended
structure that does not exist yet; everything else reflects the repository as it is.

---

## 1. Engine / framework decision

### Requirements that drove the decision
The game is simulation-first: a deterministic mechanical model (engine, drivetrain, tires, damage)
matters more than rendering. The team is small and development is heavily AI-assisted. Content must
be data-driven and moddable. The mechanical model must be testable without launching the game.

### Candidates evaluated

| Criterion | Unity 6 | Unreal 5 | Godot 4.7 (.NET) | Custom (Rust/Bevy, C++/raylib) |
|---|---|---|---|---|
| Vehicle physics | WheelCollider (limited); serious sims replace it | Chaos Vehicles (good, opinionated) | VehicleBody3D (basic); Jolt rigid bodies | Write everything |
| Custom physics integration | Good (FixedUpdate, C#) | Good (C++), heavy | Good (`_PhysicsProcess`, C#, configurable tick) | Full control |
| 3D rendering | Very good | Best | Adequate (Forward+/Vulkan) | Must build |
| Editor tooling / UI | Mature | Mature, heavy | Good, lightweight, Control-node UI is strong for data-heavy screens | None |
| Asset pipeline | Mature; GUID `.meta` files | Binary `.uasset` | Text `.tscn`/`.tres`, glTF import | Must build |
| Modding | Needs third-party injection or asset bundles | Hard (pak/UGC tooling) | Loose files, PCK loading, open source | Whatever we build |
| Save / data-driven | Fine (C#) | Fine (C++/USTRUCT) | Fine (C#, System.Text.Json) | Fine |
| Iteration speed | Medium (domain reloads) | Slow (C++ compile) | Fast | Fast for logic, slow for tooling |
| AI-assisted development | Scenes/prefabs are YAML with GUIDs; editor-centric | Binary assets; blueprints opaque to text tools | Everything is text; C# is statically typed | Text, but huge surface to author |
| Headless CI / tests | Requires license activation | Heavy | `--headless` works; core tests need no engine at all | Easy |
| License / long-term risk | Proprietary; 2023 runtime-fee episode | 5% royalty > $1M | MIT, no royalties, source available | None |
| Small-team maintainability | Good | Poor for a small team | Good | Poor (engine maintenance cost) |

### Decision
**Godot 4.7 .NET (C#) as the presentation/runtime engine**, with the entire mechanical simulation in
a **pure .NET 8 class library (`CarSim.Core`) that has zero Godot references**.

Rationale:
1. The simulation is the product. Keeping it in a plain .NET library means it is tested with
   `dotnet test` in seconds, runs identically in the game, the CLI tools and CI, and is portable to
   another C# engine (Unity) if Godot ever becomes a limitation. This sharply reduces engine lock-in.
2. C# gives static typing, value types and JIT performance for per-tick math (tire/drivetrain at
   hundreds of Hz), which GDScript does not.
3. Godot's text-based scenes and resources are diff-able and reviewable, which suits AI-assisted
   development and code review.
4. MIT license, no royalties, source available (we can patch or build the engine ourselves — which we
   already had to do, see §8).
5. Godot's weaknesses (3D fidelity below UE/Unity, a simplistic built-in vehicle body) hit our lowest
   priorities. We intend to write our own tire and drivetrain model anyway; Godot supplies rigid-body
   integration (Jolt), collision, rendering, input and UI.
6. Modding: base content ships as loose JSON next to the executable (§5), the same format mods will
   use.

Rejected: Unreal (binary assets, heavy iteration, overkill for the team size); Unity (licensing risk,
editor-centric GUID-heavy serialization, license-gated headless CI; otherwise a reasonable second
choice — and the core stays portable to it); custom engine (tooling cost dwarfs the benefit).

### Versions
- Godot **4.7.2-stable** (.NET edition) — GodotSharp targets `net8.0`.
- .NET SDK **8.0** (`global.json` pins major 8, rolls forward within it).
- Test framework: **xUnit**.

---

## 2. Repository layout

```
CarTuningSim.sln
Directory.Build.props      shared compiler settings (nullable, warnings-as-errors, LangVersion)
global.json                .NET SDK pin
content/base/              base-game content as JSON (parts, engines, fuels, tunes, vehicles)
src/CarSim.Core/           game-engine-agnostic simulation + domain model (NO Godot references)
src/CarSim.Gameplay/       garage, economy, scenarios, save/load (NO Godot references)
tests/CarSim.Core.Tests/   xUnit tests for the core
tools/CarSim.Cli/          headless command-line tool (inspect, validate, dyno) built on the core
game/                      Godot 4.7 project (presentation layer); references CarSim.Core
```

`CarSim.Core` must never reference Godot. The game project references the core; the reverse is
forbidden. This is enforced structurally: the core project has no Godot package reference.

---

## 3. Layer boundaries

### Simulation core (`CarSim.Core`)
Deterministic, fixed-timestep, SI units internally. No rendering, no input, no file-format concerns
beyond the content loader. Namespaces:

- `CarSim.Core.Common` – unit conversions, physical constants, interpolation tables, root finding.
- `CarSim.Core.Parts` – part definitions (typed per category), part instances (wear), spec registry.
- `CarSim.Core.Content` – JSON content loading, validation and the content database.
- `CarSim.Core.Fuels` – fuel definitions.
- `CarSim.Core.Engines` – engine family definitions (slot graph), engine assembly (install/remove
  order), compatibility validation, derived geometry, valvetrain limits.
- `CarSim.Core.Ecu` – editable tunes (tables + calibration) and the runtime ECU controller.
- `CarSim.Core.Simulation` – the mean-value engine model: configuration, air path, fuel system,
  combustion, thermal, lubrication, telemetry.
- `CarSim.Core.Dyno` – incremental dyno runner (sweep/steady-state), run records, comparison, CSV.
- `CarSim.Core.Damage` – stress evaluation, fatigue accumulation, failures, diagnostic reports,
  inspection findings, warnings.
- `CarSim.Core.Vehicles` – vehicle definitions and chassis assemblies, tyre model, planar vehicle
  dynamics with load transfer, clutch/gearbox/differential driveline, brakes, engine coupling.

### Domain / data
Content (parts, engines, fuels, tunes, vehicles) is JSON under `content/`. Definitions are immutable
after load. Runtime state (installed parts, wear, fatigue) lives in separate instance objects so
definitions can be shared.

### Gameplay (`src/CarSim.Gameplay`)
Pure C# (no Godot): `Garage` (project car engine and chassis parts, shelf inventory, money, fuel,
tune, engine-in-car access rule, buy/sell, building a drivable `VehicleSimulation`), data-defined
new-game scenarios (`content/base/scenarios/`), versioned JSON save/load (`SaveSystem`), and
`DrivingSession` (fixed 2 ms stepping from variable frame times, lap timing, queued gear-shift
requests, recovery to the track, autopilot hand-over, failure hand-off) with `KeyboardInputFilter`
(digital keys to smooth inputs with a grip-based steering assist). Jobs, reputation and a parts
market are planned.

### Physics
Decision (2026-09-26): vehicle dynamics run **in the core**, not in Godot's physics engine. The test
facility is flat, and a deterministic planar model with load transfer captures what the game is about
(gearing, differential, tyres, suspension balance, weight, power) while staying unit-testable
(0–100, braking distance, skidpad, LSD behaviour are all tested without Godot). Friction parts
(clutch, brakes, tyres) carry heat and wear in `ChassisWearModel`, which writes wear and failures to
the same part instances and `FailureReport` type as the engine's damage model. Godot renders the
car from the core's pose and handles presentation. If terrain, kerbs or collisions become important,
the planned path is to add vertical dynamics per corner in the core and use Godot/Jolt only for
collision queries (ray casts against the track mesh) feeding the core.

### Presentation (`game/`)
Godot scenes and C# scripts that display core state and send player intent to the core. Scripts
must not contain mechanical formulas; if a UI needs a number, the core exposes it.

The prototype UI is built in C# code (`game/scripts/UI/`) rather than in large `.tscn` files: views
are reviewable in diffs and the scene file stays trivial (`scenes/Main.tscn`). `GameState` (plain C#)
holds the session (content, garage, dyno runs, failure reports) and raises `Changed`; views rebuild
from it. Tabs: Garage (car, fuel, shelf, save/load, log, test-track button), Workshop (engine and
chassis component tree, inspection, specs, removal order, shelf/shop), Setup (every adjustable
setting of the installed parts, balance figures, chassis bench test via `ChassisBench`), Tuning (ECU limits,
calibration, tables), Dyno (sweep/steady runs, live telemetry, warnings, graphs, comparisons, failure
reports), Reports (failure history, inspection of engine and chassis).

The test track is a separate scene (`scenes/Drive.tscn`, `game/scripts/Drive/`): the circuit mesh
is generated from the same `TrackLayout` the tyres read their grip from (asphalt, kerbs, grass), the
car is a simple primitive model posed from the core state (roll/pitch from the suspension
load-transfer states, wheel spin and steer), with chase/bumper/trackside cameras and a HUD. Input
actions are registered at runtime (keyboard and gamepad). The scene owns no physics: it feeds a
`DrivingSession` and draws it. Mapping: simulation (x, y) → Godot (x, 0, −y); heading → rotation
about +Y; the car model faces +X.

The Godot project uses the Compatibility (OpenGL) renderer: the UI and the simple 3D track do not
need Forward+.

### Tools
- `tools/CarSim.Cli` – validate content, inspect assemblies, run a dyno sweep and print/CSV-export
  results, generate base maps for a tune (`calibrate-cams`, `calibrate-ve`, `calibrate-spark`) and measure the step
  cost of any family (`bench`). Commands take an engine-family id (required once more than one family is loaded).
  Used for development and as a regression harness; unlike the game UI it may print model internals (best-torque
  timing, knock limit).
- Dyno, telemetry and debugging views in the game UI. *(in progress)*

---

## 4. Simulation architecture

- **Units:** SI internally (m, kg, s, Pa, K, rad/s, N·m, W). Content files use conventional
  automotive units with the unit in the field name (`bore_mm`, `flow_cc_min`, `max_pressure_bar`);
  definitions convert to SI once at load time and expose SI properties to the simulation.
- **Determinism:** no randomness in the core model. Any future variation (manufacturing tolerance)
  must come from explicit seeded inputs so tests stay deterministic.
- **Time stepping:** the engine model is a *mean-value* model advanced with a fixed `dt`. Fast
  algebraic relationships (air path, fueling) are solved quasi-statically each step; slow states
  (temperatures, turbo shaft energy, engine speed when not held, damage) are integrated.
- **Data flow:** `Assembly (parts + instances) → EngineConfiguration (validated, derived specs) →
  EngineSimulation (state + step) → EngineTelemetry (per-step outputs)`. Parts affect the model only
  through their spec properties; there are no part-ID special cases in simulation code.
- **Failure:** thresholds plus accumulated fatigue (see SIMULATION_SPEC.md). Every failure produces a
  structured report with cause, measured values, ratings and recommendations.

See SIMULATION_SPEC.md for equations and PARTS_DATABASE.md for the data schema.

---

## 5. Content and modding

- Base content lives in `content/base/` as JSON (comments and trailing commas allowed).
- The loader discovers every `*.json` file under a content root, reads each object's `category`
  (or document `kind`), and deserializes it into the typed definition for that category.
- Shipping plan: exported builds carry `content/` as loose files beside the executable.
- Mods: each folder under `content/mods/` is a content layer loaded after `base` in name order
  (`ContentLoader.LoadWithMods`). Later layers may add content and replace definitions by id (reported
  as overrides); duplicates within one layer are errors; cross-references are validated once all
  layers are loaded. See PARTS_DATABASE.md, "Mods".
- New part *categories* require code (the simulation must know what the properties mean); new
  *parts* in existing categories require only data.

---

## 6. Save/load
Runtime state (inventory, part instances with wear/fatigue/failures, the assembly, tune, fuel, money,
engine-in-car flag, the scenario the game started from) serializes to versioned JSON (`SaveSystem`, version 4; the
scenario id is optional display data, so older saves load without it and no version step was needed). Definitions are referenced
by id, never embedded, so content updates flow into saves. Loading validates every reference and
reports all missing content at once (e.g. a removed mod).

Older saves are migrated in place before validation (`SaveSystem.Migrate`), one version step at a
time. Version 1 → 2: tunes gained the speed-density `volumetric_efficiency` table and
`displacement_cc`; a version-1 tune gets the engine's stock-tune table resampled onto its own axes and
the stock displacement, so the car runs as before on a stock engine and drifts off target λ as far as
its breathing differs from stock. Version 2 → 3: parts gained a damage ledger (`exposure`), which
older saves start empty. Version 3 → 4: tunes gained the injector dead time and fuel density the ECU meters with;
an older tune gets the installed injectors' dead time and the save's fuel density, so it runs as before. Saves newer than the game are rejected, and so are unknown failure-mode names
(they used to be dropped, silently healing a failed part).

---

## 7. Testing strategy

- **Core unit tests** (`tests/CarSim.Core.Tests`, xUnit): geometry, unit conversions, airflow,
  fueling limits, combustion, knock, thermal, turbo, damage, compatibility, content validation.
  Deterministic; no Godot.
- **Behavioral tests** assert directional physics ("more boost → more airflow", "restrictive exhaust
  → less top-end power") rather than brittle exact numbers, plus a small set of pinned reference
  values for regression.
- **Invariant and property tests** check the model against independent physics rather than its own
  formulas: a per-step energy balance of the engine, turbo shaft power balance, steady-state load
  transfer against `m·a·h`, a coast-down against drag and rolling work, the friction circle, a
  time-domain Monte Carlo quarter car against the ride model's frequency response, timestep
  convergence (engine and vehicle), bit-identical save → load → drive replays, every part in every
  slot (builds and runs to finite numbers, or is refused with reasons), and a per-step allocation
  budget.
- **Validation-pass additions** (2026-09-26): independent first-law checks (a warm-up closing the energy balance
  with stored heat, brake efficiency under the Otto limit, released ≤ supplied fuel energy, no failure raising
  torque); ECU observability (delivered fuel reconstructed exactly from what the ECU can see across hardware whose
  true breathing differs by > 15 % λ; no hidden closed loop; dyno logs hold only measurable channels); turbo
  invariants far from calibration (first/second law at every compressor point, a 24-case closed-loop matrix to
  350 kPa with zero reversals, square waves, target steps, overspeed as a reported failure); spec fuzzing (every
  numeric field of every fitted part scaled from −1× to 10³×; wherever the part's validator accepts the value, the
  engine or car must be refused with reasons or run finite); clamp-activation tests (guards on fitted laws never
  carry normal running); tyre-width and suspension trade-offs; Miner additivity across a save/load.
- **Mutation checks:** new invariants are shown to fail on the bug they guard — the previous physics (overrun
  exhaust heat), an ECU fed the true air mass or the true fuel density, boost feedback from the compressor outlet,
  choke that removes compressor work, no boost anti-windup, a raised VE floor. A test that cannot fail on its bug is
  not counted as evidence.
- **Second engine family** (2026-09-26): a real engine (BMW M54B30 reference) authored as data and tested against its
  published figures with stated acceptance bands; both families through one gameplay pipeline; the family under
  renamed ids bit-identical; a content-only variant (8 cylinders, other bore/stroke/CR/limit/cam) that the physics
  follows; a source audit of `src/` for family tokens, content ids and size-specific branches; the first family's
  output pinned to its pre-milestone value. The audit and identity tests were mutation-checked with injected hacks.
- **Content tests** load every file under `content/` and validate references, ranges and that the
  stock engine assembles and runs.
- **CLI**: CI runs `carsim validate` and a short `carsim sweep` after the tests.
- **Godot**: the game project is part of the solution, so every `dotnet build` compiles it
  (Godot.NET.Sdk from NuGet). CI also downloads Godot 4.7.2 .NET and runs
  `godot --headless --path game -- --smoke-test`, which loads content, builds the garage and runs a
  dyno pull through the game layer. Screenshots for visual review: `-- --screenshot=file.png`.

---

## 8. Build environment notes

- `dotnet build CarTuningSim.sln` and `dotnet test` build and test everything except Godot runtime
  behaviour.
- In the development container used for the initial implementation, Godot release downloads were
  blocked by network policy, so the Godot 4.7.2 .NET editor was built from source
  (`scons platform=linuxbsd target=editor module_mono_enabled=yes`). This is an environment
  workaround, not a project requirement: normal development uses the official Godot 4.7.2 .NET build.

---

## 9. Decision log

| Date | Decision | Reason |
|---|---|---|
| 2026-09-26 | Godot 4.7 .NET + pure C# core | See §1 |
| 2026-09-26 | JSON content with unit-suffixed field names, SI at runtime | Moddable, explicit units, one conversion point |
| 2026-09-26 | Mean-value engine model, fixed timestep | Captures torque/airflow/thermal/turbo behaviour cheaply and deterministically; extensible |
| 2026-09-26 | Parts sold/installed as sets per slot (e.g. a set of 4 pistons), single condition per set | Keeps the first prototype small; per-cylinder state is a later extension |
| 2026-09-26 | Assembly order defined as data (`install_after` graph per engine family) | Disassembly/reassembly gameplay without hardcoded sequences |
| 2026-09-26 | Vehicle dynamics in the core (planar + load transfer), Godot renders | Deterministic, testable; flat test track needs no 3D physics |
| 2026-09-26 | Dyno pulls end at the rev limiter | An absorption dyno cannot motor the engine; over-revs come from tuning or missed shifts |
| 2026-09-26 | Friction parts (clutch, brakes, tyres) carry heat and energy-based wear; gearbox/differential use the engine's stress-ratio fatigue | Upgrades interact (a stronger clutch moves the weak link to the gearbox); wear persists on part instances |
| 2026-09-26 | Automated clutch engages smoothly after shifts (engine torque + 80 N·m while syncing) | Full-throttle snap engagements put engine inertia through the gearbox on every shift, which no driver does |
| 2026-09-26 | Mods are content layers after `base`, override by id | Mods can rebalance as well as add, without code; overrides are visible, not silent |
| 2026-09-26 | Setup settings are adjustable spec fields declared by parts, stored on part instances; the sim reads an effective spec | One generic mechanism for pressures, camber, damping, bars, bias, preload; no per-setting code paths; settings travel with the part |
| 2026-09-26 | Closed-form compressor: efficiency a function of flow and speed only, choke flow ∝ √speed, work retained past choke, continuous surge penalty; no shaft-speed clamp | The iterative efficiency↔PR solve did not converge above the shipped boost level (step-to-step torque ripple, PR falling with speed) and choke removed work so shafts ran away; see review of PR #1 |
| 2026-09-26 | Engine heat flows go through one energy split that sums to the released fuel power; rich mixtures release less heat (partial oxidation, Thornton's rule) instead of an empirical EGT offset | A blown head gasket created ≈ 19 kW and rich EGT used a −300 K·(1−λ) fudge; a per-step energy-balance test now guards every state |
| 2026-09-26 | Boost PI with anti-windup; turbine inlet temperature from manifold heat loss; surge wears the turbo bearings | Wound-up integrator spiked boost 21 kPa over target; surge had no consequence |
| 2026-09-26 | Speed-density ECU: fuel from a VE table × MAP × displacement / (R · IAT), never the true air mass; content VE tables generated by a dev-only calibrator; save format v2 with migration; MBT and knock limit off the player's screens and CSV | The ECU read the simulator's air mass, so every breathing mod was fuelled perfectly and fuel tuning did not exist; the dyno displayed model internals no real dyno can measure. Dead time, warm-up enrichment and closed-loop λ are left out: the open-loop VE table is the tuning task |
| 2026-09-26 | Residual factor `1/(1+x)` instead of `1 − x` clamped at 0.4 | The clamp gave long-overlap cams a flat, kinked part-load VE no fuel table could fit (found by the VE calibrator) |
| 2026-09-26 | `EngineTopology` states what the engine model can represent (one part or set per modelled category, turbo/intercooler optional); the content loader rejects other families and the validator reports `missing_category`/`unsupported_topology`, so `EngineConfiguration.Build` never throws on loaded data. Chassis roles resolve by category and a data `axle` field | A family with an optional radiator passed validation and crashed `Build`; duplicate categories were silently read as the first slot; chassis slot ids were hard-coded. Multi-part topologies (twin turbo, per-bank air paths) are deliberately rejected until the model supports them, not half-supported |
| 2026-09-26 | Damage laws: per-load-cycle Miner/Basquin fatigue (combustion, firing or revolution cycles), stress ∝ speed² for rpm-rated parts, Arrhenius (kelvin) for hot parts, stress rupture for the turbo wheel; a persisted per-part damage ledger | Time-based cubic fatigue broke rods at 90 % of their rating in 4 minutes, used Celsius ratios and forgot earlier sessions, giving "why now?" failures. Ratings stay game-compressed (minutes at the rating, hours at 90 %) rather than real 10⁶–10⁷-cycle lives |
| 2026-09-26 | Tyre size enters through the contact patch: nominal load ∝ width (load sensitivity) and peak slip angle ∝ width^−0.5; no relaxation length | Width only set the rolling radius (165/225/305 mm were bit-identical) and the street tyres were separated by hand-edited load sensitivities. Relaxation length is not needed for width to matter and would add a stiff low-speed state |
| 2026-09-26 | Ride over road roughness via a frequency-domain quarter car per axle (ISO 8608 surfaces, variance ∝ speed), anti-roll bars in the roll mode, unsprung mass from parts, bump travel and bump stops; grip × 1/(1 + 0.6·(σ/Fz)²) | Springs, dampers, bars and ride height had no downside, so the optimum was always an end stop. A frequency-domain solve adds the physical trade-offs without per-corner time integration (which would need a smaller step and roll-centre geometry); roll centres remain future work |
| 2026-09-26 | Knock from an end-gas autoignition integral (Livengood–Wu, Douaud–Eyzat delay) over a single-zone Wiebe cycle tied to the MBT model, with residual-gas heating and Kalghatgi's octane index (fuels gain `octane_mon`); factory spark table re-limited on RON 95 | The linear knock limit with a light-load patch sat above MBT everywhere at WOT, so octane never limited the NA engine. The integral makes rpm, boost, temperatures, compression, mixture and octane act through one mechanism; the richness and octane-index terms are the documented empirical parts |
| 2026-09-26 | Exhaust-port wall heat exchange (exact pipe law, constant UA from bore² × cylinders, sink = coolant) replaces the one-way motoring pickup; the in-cylinder coolant fraction goes 0.28 → 0.265 so the full-load coolant total is unchanged | Validation pass: routing pumping work into the exhaust (to close the energy balance) let a few g/s of overrun gas carry ~10 kW and leave at 1,500–4,500 °C; every lift-and-reapply burned the T28's turbine. The balance stays exact; the walls now bound the gas temperature. A big-turbo gearbox test had passed only because its harness never lifted between pulls |
| 2026-09-26 | ECU calibration beliefs complete: fuel density and injector dead time are tune fields (injectors carry a real dead time); the boost PI closes on the ECU's MAP reading above 80 % pedal; the knock sensor reports a level, not degrees past the limit; the dyno's IAT channel is the manifold air | Validation pass: the ECU converted fuel mass to injector volume with the true fuel density, closed the boost loop on the true compressor-outlet pressure (a sensor it does not have, unclipped), and the dyno showed knock intensity (= advance − knock limit), so one knocking reading gave the knock limit away. Mutation tests (true air, true density, compressor-outlet feedback) are each caught |
| 2026-09-26 | Not (yet) modelled: intake/exhaust volume filling dynamics, a blow-off valve part, tabulated compressor/turbine maps | Turbo lag is dominated by rotor inertia (modelled); the quasi-static air path is stable at the 2–5 ms steps used. Filling dynamics would need an implicit solver. Parametric maps are closed-form and authorable from four map numbers; tables are a later content feature |
| 2026-09-26 | Second engine family (Isar M54 = BMW M54B30 reference) added as content only; no simulation code knows it | The test of whether families are data. The only generic model change it needed is the next row; everything else (geometry, six cylinders, ECU, damage, dyno, vehicle, saves) already worked from specs |
| 2026-09-26 | Cam timing is data: optional installed lobe centrelines, an intake phaser range, `cam_phase_control` ECUs and an `intake_cam_advance_deg` tune table; the tuned-speed correlation gains the intake-closing shift (0.30 m/s per degree, from its own 0.15 per degree of duration) | The VE correlation silently assumed every cam at the K20's ≈ 112° centreline. The M54's true cams on it made 130 kW instead of 170 (A0); an "effective duration" would have been a false cam. Cams without centrelines are untouched (K20 bit-identical). Exhaust phasing, part-load EGR strategy and phaser oil pressure are not modelled |
| 2026-09-26 | Level-setting constants (Otto realisation, FMEP, the new 112° reference) were **not** re-fitted for the second family | Re-fitting to hit the M54's published power would be exactly the hidden output correction the milestone forbids and would move the K20. The M54 lands at +1 % torque, −10 % power; documented as a generic high-piston-speed calibration question for both families |
| 2026-09-26 | Base maps for new families come from dev calibrators (`calibrate-cams`, `calibrate-ve`, `calibrate-spark`) | The K20's spark map was hand-authored; a reproducible tool keeps a second (and third) family's calibration honest and reviewable |
| 2026-09-26 | Parts with a mounting interface must declare it (K20 gaskets, oil pumps, flywheels gained `requires`); parts without one are universal | With two families, interface-less family parts bolted across (a K20 gasket passes the bore check on the 84 mm six) |
| 2026-09-26 | Post-shift sync slip limited by the engagement controller is not a "clutch slipping" warning | A heavier flywheel and wider ratio step (the second family's car) made every upshift a Danger warning; the slip still heats and wears |
| 2026-09-26 | The CLI requires an engine-family id when several families are loaded; the game picks scenarios (`--scenario=<id>` headless) | The first family in load order became the M54, so id-less CLI commands would silently have switched engines; the Garage tab titled every game after the first scenario |
| 2026-09-26 | The M54's torque-curve shape discrepancy is classified as missing generic physics — one VE filling hump for intake closing and runner gas dynamics, moved whole by a cam phaser — and documented and pinned (`TorqueCurveDiagnosisTests`) rather than fixed in this PR | A prototype of the missing term (a gas-dynamic share tuned at the straight-up cam/runner speed) makes the low end rise as the real engine's does, but without DISA's open stage it cuts M54 power to 139.5 kW (−18 %). Completing it needs a new shared constant with no source and DISA's unpublished geometry; choosing them to match the reference would be per-engine fitting. SIMULATION_SPEC.md, "M54 torque-curve investigation"; ROADMAP next task 2 |
