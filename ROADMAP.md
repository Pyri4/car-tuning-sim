# Roadmap

North Star: [GAME_VISION.md](GAME_VISION.md) — hundreds of engines and thousands of parts; *adding the 100th engine
should be almost as easy as adding the 2nd*. Architecture first, content scale second.

## Current state (2026-09-27)
**The first playable prototype is complete, a second engine family validates that families are data, and the engine
architecture is generic: banks, per-bank air paths and geometry, any number of turbos, valvetrain types, variable valve
lift and variable intakes, engine ↔ car fit by interfaces — proven by a nine-engine synthetic matrix.** Two
cars (Kestrel S2 coupe, Isar C30 coupé), two engine families (the fictional Kestrel K20 four and the Isar M54 straight
six, a real-engine reference: the BMW M54B30), one garage, one engine dyno and one test track, and the whole required
loop works in the game for both:

inspect → remove the engine → disassemble → replace parts → reassemble → start (dyno or car) →
tune the ECU → dyno pull → drive the test track → break something through bad parts, builds or tuning
→ read the failure report → repair in the workshop.

- Simulation lives in pure C# (`CarSim.Core`, `CarSim.Gameplay`); Godot 4.7 .NET only presents it.
- 649 automated tests: 644 run and 5 are the pending acceptance tests of Intake Gas Dynamics 2.0 (586 before its
  Phase 0, 484 before the engine-architecture milestone, 435 before the second engine family, 348
  before the validation pass): simulation, content, damage, dyno, vehicle dynamics, wear, gameplay, saves, mods,
  physical invariants, property sweeps, spec fuzzing, clamp-activation checks, and architecture invariants over the
  synthetic engine matrix. CI runs them, CLI content checks and dyno sweeps (with and without the matrix), and headless
  Godot smoke tests (dyno pull; autopilot drive) for both real families and for synthetic engines swapped into a car,
  on the official Godot 4.7.2 .NET build.
- A simulation-correction phase (below) addressed the review of PR #1; a validation pass then re-checked every
  finding against independent evidence, found and fixed a regression the correction phase itself introduced
  (overrun exhaust heat burning turbines) and three remaining ECU oracles. See "Validation pass" for the status of
  every review issue.
- Content: 105 parts, 2 engine families, 2 vehicles, 5 fuels, 3 base tunes, 2 scenarios; mods load as extra
  content layers. Test content (`content/test/engine-matrix/`, not shipped): 142 more parts, 9 synthetic engine
  families with calibrated tunes, 18 scenarios (each engine on the stand and swapped into the Isar C30).
- Reference numbers (re-measured after the validation pass): stock K20 ≈ 149 hp / 189 N·m (the worn project car
  ≈ 137 hp); T28 turbo build ≈ 238 hp / 293 N·m; stock car 0–100 km/h ≈ 8.8 s, ≈ 0.90 g skidpad (≈ 0.84 g on
  kerb-grade roughness). K20 output bit-identical after the second-family milestone. M54 (RON 98): 304 N·m (reference
  300), 153 kW (reference 170: −10 %), 52.1 s laps in the Isar C30.

## Completed work
### Phase 0 — Architecture ✅
- [x] Engine/framework chosen and documented (Godot 4.7 .NET + engine-agnostic C# core; ARCHITECTURE.md §1)
- [x] Simulation boundaries (core/gameplay/presentation layers, no Godot types in the core)
- [x] Data schemas (JSON, snake_case, unit-suffixed fields, SI at runtime; PARTS_DATABASE.md)
- [x] Testing strategy (xUnit, deterministic fixed-step sims, CI with headless Godot smoke tests)
- [x] Skeleton, solution, CLI tool (`carsim validate | inspect | sweep | hold | drive`)

### Phase 1 — Workshop prototype ✅
- [x] One vehicle, part inventory (shelf), buy/sell
- [x] Engine removal (engine-in-car access rules), disassembly order from data (`install_after`)
- [x] Part inspection (wear and fatigue findings, compression test)
- [x] Assembly validation (interfaces + physical rules: bore, pins, journals, clearances, CR, coil
      bind, valve float, ratings vs rev limit, turbo/MAP/boost rules)
- [x] Chassis parts in the workshop (clutch, gearbox, differential, tyres, suspension, brakes)
- [x] Save/load (versioned JSON, byte-identical round trip, clear errors for missing content)

### Phase 2 — Engine simulation ✅
- [x] Mean-value engine: compressible-orifice air path, VE from cams/runners/headers, residuals and
      reversion, speed-density ECU (VE table since the correction phase), fuel pump/regulator/injectors,
      combustion with MBT, knock (end-gas autoignition since the correction phase), FMEP/PMEP, peak
      cylinder pressure
- [x] Turbocharger: parametric compressor model (speed lines, surge, choke, efficiency island — not a
      tabulated map), turbine and wastegate, shaft
      inertia (lag), ECU boost control, intercooler
- [x] Thermal (coolant with boiling/loss, oil, piston crowns, EGT) and lubrication (bearing clearance,
      oil surge/aeration)
- [x] Damage: stress ratios per failure mode with fatigue, special processes (detonation, oil
      starvation, overheating, valve float), catastrophic vs degraded, collateral damage, explanatory
      reports (cause, measurements, contributing factors, recommendations)

### Phase 3 — ECU + dyno ✅
- [x] Tuneable maps (fuel, ignition, boost), ECU hardware limits, base maps
- [x] Dyno (sweep/steady, coolant modes, speeds), graphs, comparisons, CSV, warnings, failure reports
- [x] Live telemetry; repeatable, deterministic testing

### Phase 4 — Driving ✅ (planar model)
- [x] Vehicle physics in the core: combined-slip tyres, load transfer as suspension dynamics
- [x] Gearbox (automated clutch, manual gears, smooth post-shift engagement), differentials (open,
      clutch LSD, locked), brakes, handbrake
- [x] Test facility circuit with asphalt/kerb/grass grip, lap timer, autopilot test driver
- [x] Godot test-track scene: generated circuit, car posed from the sim (roll/pitch, wheel spin and
      steer), chase/bumper/trackside cameras, HUD, keyboard (steering assist) and gamepad, recovery
- [x] Friction-part heat and wear: clutch (slip → heat → fade → burnout), brakes (disc temperature,
      fade, pad wear), tyres (tread wear → grip)
- [x] Gearbox/differential overload fatigue (a big-turbo build breaks the stock gearbox)
- [x] Failures on track pause the drive and show the report; wear persists in the garage and saves

### Car set-up ✅
- [x] Parts declare adjustable spec fields with ranges; settings live on the part instance and in saves
- [x] Tyre pressure (grip window, response, rolling resistance, wear), static + roll camber with
      geometry gain, brake balance bar; ride height, damping, anti-roll bars and LSD preload through the
      same mechanism
- [x] Setup tab with balance figures and a chassis bench (skidpad, threshold braking) that wears nothing
- [x] Tyre temperature: sliding and flexing heat, airflow cooling, a grip window per compound, cold
      pressure set in the garage rising with temperature, out laps on cold tyres, HUD temperatures

### Simulation-correction phase ✅ (after the review of PR #1)
- [x] Compressor: closed-form speed lines, choke flow ∝ √speed, work retained through choke, continuous
      surge; no shaft-speed clamp; stable above the shipped boost (was divergent)
- [x] One closed engine energy balance (fuel = brake + coolant + oil + exhaust) tested every step; rich
      mixtures cool the exhaust through partial oxidation, not an offset
- [x] Boost PI anti-windup, turbine inlet temperature from manifold heat loss, surge wears the turbo
- [x] Speed-density ECU (VE table × MAP × displacement / (R·IAT)); VE calibrator dev tool; save format
      v2 with migration; MBT and the knock limit off the player's screens
- [x] Engine topology stated in code (`EngineTopology`): families the model cannot represent are
      rejected at load, `Build` never throws; chassis roles by category and axle
- [x] Damage laws: per-cycle Miner/Basquin fatigue, speed² stress for rpm-rated parts, Arrhenius (kelvin)
      for hot parts, stress rupture for turbo wheels, persisted damage ledger (save v3), unknown failure
      modes rejected on load
- [x] Tyre width through the contact patch (nominal load ∝ width, slip angle ∝ width^−0.5)
- [x] Ride over road roughness (frequency-domain quarter car per axle), bump travel and bump stops:
      suspension settings have trade-offs and interior optima on rough surfaces
- [x] Knock from an end-gas autoignition integral with residual gas and an octane index; the NA engine is
      knock-limited at low-mid rpm on pump fuel; factory spark table re-limited
- [x] Invariant tests: load transfer statics, coast-down energy, timestep convergence, save → load →
      drive replay, every part in every slot, allocation budget

### Validation pass ✅ (PR #2 checked against the review of PR #1)
The review of PR #1 is not stored in the repository; its findings are reconstructed from the correction phase's
"before" descriptions and the validation brief. A status counts as FIXED only with evidence that does not restate
the implementation, and new regression tests were shown to fail on the bug they guard.

| # | Original issue | Status | Independent evidence | Remaining |
|---|---|---|---|---|
| 1 | ECU read the true trapped air mass (every mod fuelled perfectly) | **FIXED** | Delivered fuel reconstructed exactly from rpm/MAP/IAT/tune across cams, head, stroker, turbo, coolant temperature (true breathing spreads λ > 15 %); an ECU fed the true air fails 21 of the 435 tests | — |
| 2 | …and still used the true fuel density; boost PI read the true compressor-outlet pressure | **FIXED** (validation pass) | Race fuel on a pump-fuel density runs 4 % lean; a 2.5-bar MAP sensor chasing 280 kPa holds the gate shut and over-boosts; mutants caught | — |
| 3 | Player could read MBT and the knock limit | **FIXED** | MBT/limit off screens and CSV (PR #2). Validation pass: the "knock sensor N" readout was degrees past the limit (limit = advance − N); now a coarse level, onset still exact; CSV `iat_c` was the charge temperature | Debug telemetry keeps MBT/limit for developers |
| 4 | Open-loop tuning could not be wrong; no dead time | **FIXED** | VE, displacement, injector flow and dead time (0.4 ms ≈ 20 % at idle, 2 % at WOT), fuel stoich and density, regulator pressure (√(4/3) rich), coolant temperature all move λ | Warm-up/transient enrichment not modelled |
| 5 | Closed-loop λ | **INTENTIONALLY DEFERRED** | Explicitly absent: an open-loop error persists unchanged over 30 s (tested) | — |
| 6 | Compressor iteration diverged above 175 kPa (torque 305 ↔ 375 N·m) | **FIXED** | Closed form; one air-path root at every point (2001-point scans); 24-case closed-loop matrix to 350 kPa with zero reversals; first/second law at every compressor point | — |
| 7 | Choke removed compressor work → shaft runaway, hidden 2× clamp | **FIXED** | Work never below (1 − 2β)σU²; no clamp; an unreachable target settles ≤ 1.1× rated and fails `TurboOverspeed` with a report; the choke-collapse mutant fails 12 of the 435 tests | — |
| 8 | Boost integrator wind-up (196 vs 175 kPa) | **FIXED** | Tip-in ≤ 8 % overshoot; target steps settle within 4 kPa; square waves bounded; the no-anti-windup mutant fails 5 of the turbo tests | Wastegate duty is PI only (no feed-forward table) |
| 9 | Energy created with a blown head gasket; rich-EGT fudge | **FIXED** | 60 s warm-up closes the first law with stored heat; brake efficiency < Otto limit (NA and turbo); no degraded failure raises torque | — |
| 10 | …the correction made overrun exhaust adiabatic: 1,500–4,500 °C port gas, every lift burned the T28's turbine | **FIXED** (validation pass) | Exhaust-port wall exchange (same exact-pipe law as the manifold); lift/re-apply at 3000–6500 rpm causes no failure; overrun cooler than full load; the previous physics fails all five new tests | Constant port UA (no flow dependence) — documented |
| 11 | Valid content crashed `Build` (optional radiator, duplicate slots, hard-coded chassis ids) | **FIXED** | `EngineTopology`; every part in every slot; spec fuzz: 905 validator-accepted engine variants (0 crashes/NaN), 199 chassis variants driven (0 crashes/NaN) | — |
| 12 | Twin turbos, per-bank air paths, superchargers, dry sumps | **PARTLY DONE** (engine-architecture milestone) | Twin turbos and per-bank air paths are supported (banks, per-turbo shafts); superchargers and dry sumps are still rejected at load with a reason | Superchargers, dry sumps: new capabilities |
| 13 | Tyre width had no effect | **FIXED** | 165→305 mm: grip +10 %, braking 55.8→49.0 m, sub-linear, no power; with authored mass/inertia/thermal mass, 0–100 slower and warm-up slower | **PARTIAL:** width's costs are authored per part (shipped tyres checked); no aero drag, relaxation length or aligning torque |
| 14 | Suspension had no trade-offs | **FIXED** | On kerb-grade roughness ride height, damping, camber and spring choice have interior optima; camber always costs braking; bars trade balance for bump grip | Toe and roll centres not modelled (no toe slider exists) |
| 15 | Octane never limited the NA engine | **FIXED** | Knock-limited below ≈ 3000 rpm on RON 95; the limited range widens with compression and narrows with octane; every factor acts the same way over a 36-point grid | Mean single-zone cycle; coolant only raises knock above 90 °C |
| 16 | Fatigue per second, Celsius ratios, rpm as stress, forgotten history | **FIXED** | Per-cycle Miner law, Arrhenius in kelvin, speed² stress, persisted ledger; identical sessions add identical damage across a save/load | **PARTIAL:** the overstress curve is far steeper than literature Basquin (documented game compression); engine thermal state restarts warm each session |
| 17 | Hidden clamps | **FIXED / DOCUMENTED** | 2× shaft clamp removed; every guard on a fitted law is flagged and tested inactive in normal running; the turbine efficiency floor no longer drives a windmilling wheel (validation pass) | Tyre light-load μ cap carries ≈ 1 % of the force on semi-slicks (a physical bound, documented) |
| 18 | K20-normalised calibration | **PARTIALLY FIXED** | Valvetrain FMEP, block/sump/oil-coolant and exhaust-port conductances now scale with geometry (K20 unchanged < 0.1 %); constants classed A–D in SIMULATION_SPEC | Otto realisation and FMEP coefficients set the level and are fitted to the K20; crown and wave-tuning correlations fitted on one family |
| 19 | Tests restated the implementation | **FIXED** | Independent invariants + mutation checks (see ARCHITECTURE.md §7); a big-turbo gearbox test that passed only through a harness artifact was restructured | — |
| 20 | Allocations / step cost | **NOT FIXED (measured)** | NA 62–75 µs & 10.6 KB/step, turbo ≈ 85 µs & 13.9 KB, vehicle ≈ 80 µs; an allocation-free root finder halved allocations but ran ≈ 25 % slower on NA/vehicle steps under .NET 8 dynamic PGO, so it was not merged | See Technical debt |
| 21 | Docs overclaimed (a "real compressor map", engine agnosticism) | **FIXED** | "Parametric", "game-engine-agnostic", modding limits in README, clamps/calibration section | — |

### Second engine family — real-world validation ✅
Question: can another engine family be added through data, or does the simulator hide engine-specific assumptions?
- [x] Reference chosen and documented: BMW M54B30, European E46 330i/330Ci (2000–2006, MS43), 170 kW / 300 N·m on
      RON 98, 10.2:1, 6,500 rpm (PARTS_DATABASE.md, "Isar M54 reference engine"; published vs measured vs estimated
      per value)
- [x] The engine as content through the existing specs: 11 bottom-end, 4 top-end, 2 manifold parts, 6 universal
      (6-injector set, 68 mm throttle, twin exhaust, fuel pump, radiator, ECU), a tune from the calibrators; the Isar
      C30 car with its own clutch, 5-speed, 3.07 diff, springs and brakes; a scenario
- [x] Missing generic abstraction found and fixed generically: **cam timing** (the VE correlation assumed every cam at
      the K20's ≈ 112° centreline; with its true cams the M54 lost 23 % of its power). Installed centrelines, an
      intake phaser and an ECU cam map; K20 bit-identical
- [x] Other generic gaps the second family exposed: interface-less K20 parts that bolted across families, the content
      loader dropping new tune fields, post-shift sync slip flagged as clutch slip, the CLI's implicit first engine,
      the Garage tab's first-scenario title and fixed new-game scenario
- [x] Dev tools for new families: `calibrate-cams`, `calibrate-spark` (beside `calibrate-ve`), `bench`
- [x] Tests: reference bands, same-pipeline matrix over both families, renamed-id identity, a content-only 8-cylinder
      variant, source audit, K20 pinned; mutation-checked
- [x] Godot: both scenarios' dyno and drive smoke tests headless in CI; scenario picker; cam map and phaser in the UI
- [x] Torque-curve investigation (review of PR #4): 11 controlled experiments classify the M54's shape discrepancy as
      missing generic physics (a phaser moves the one filling hump whole), not content or calibration; the missing
      term was prototyped but not shipped (it needs an unsourced constant and DISA data), and 8 regression tests pin
      the diagnosis. No simulation or content change
- Answer: **yes, primarily through data.** Generic simulation code gained one abstraction (cam timing) and no
  engine-specific branch; see "Known issues" for what the M54 still cannot match.

### Engine architecture ✅ (2026-09-27, branch `claude/engine-architecture`)
Question: is the engine architecture generic enough for hundreds of engines, or does it hide single-engine assumptions?
Audit first (`docs/ENGINE_ARCHITECTURE_AUDIT.md`, two passes), then:
- [x] **Banks:** families declare banks, bank angle and firing order; bank-scoped slots (one head, gasket, springs,
      cams, intake, throttle, exhaust manifold, exhaust, optional turbo and intercooler per bank; one part may serve
      several banks); diagnostic topology errors
- [x] **Per-bank simulation:** one air path, geometry (own gasket and head → own compression and quench), combustion,
      knock limit, heat and EGT per bank; shared elements split by cylinders (exact for alike banks); explicit
      engine-level aggregation; per-bank and per-turbo telemetry. K20 and M54 bit-identical to the PR #4 head
- [x] **Any number of turbos**, each with its own shaft, wastegate and boost-control state; a failed turbo stops only
      its own shaft
- [x] **Capabilities as part data:** valvetrain type (OHV/SOHC/DOHC, checked per bank), two-stage variable valve lift,
      two-stage variable intake runner (ECU outputs + tune switch speeds); `EngineCapabilities` as a derived summary
- [x] **Bank-aware validation, damage and diagnostics:** interfaces per bank, per-part stress and failure, collateral
      damage on the failed part's banks, compression test per bank, messages that name the bank
- [x] **Engine ↔ car by interfaces:** a car names its stock engine only; gearboxes require the block's bellhousing;
      scenarios, the garage and `carsim drive --vehicle` check the fit (engine swaps are possible)
- [x] `TuneDocument` is a record (no more field-by-field copies dropping new tune fields)
- [x] **Synthetic engine matrix** (9 content-only architectures, calibrated with the dev tools) through the whole
      pipeline, including a lap of the test track for each, swapped into the Isar C30; architecture invariant tests; mutation checks
- [x] CLI (`inspect` architecture and per-bank geometry, `sweep` per-bank/per-turbo columns, `--vehicle`), UI
      (per-bank/per-turbo dyno gauges, switch speeds in Tuning, per-bank compression in the Workshop), CI (matrix CLI and
      Godot smoke tests)
- [x] Project memory: GAME_VISION.md (North Star), ENGINE_AUTHORING_GUIDE.md (canonical engine reference), AGENTS.md
- Answer: **engines are data** for every architecture in the matrix; what still needs code is listed as B/D in the
  audit's second pass and in ENGINE_AUTHORING_GUIDE.md §9.

### Intake Gas Dynamics 2.0 — Phase 0 ✅ (2026-09-27; Phase 1 awaiting authorization)
Make the next physics change measurable, reproducible and reviewable before it is made (docs/milestones/INTAKE_GAS_DYNAMICS_2.md):
- [x] **Regression fingerprint** in the repo (`carsim fingerprint`, `tests/baselines/fingerprint.txt`): K20, M54 and every
      synthetic family, full precision, in every build, sweep, dyno mode, cold start, failure hold, scenario and lap
- [x] **Recalibration driver** (`carsim regenerate-tunes`, a recipe per tune, hand-authored tables audited, format-keeping
      writer) — finding: no shipped tune is an exact fixed point of its recipe today (docs/VERIFICATION.md)
- [x] **Mutation harness** (`tools/CarSim.MutationCheck`, 21 mutants, manual CI job)
- [x] **Model specification** with sources and assumptions (SIMULATION_SPEC.md, "Intake gas dynamics 2.0 — proposed model")
- [x] **Acceptance tests before the code**: five pending system-level tests (each recorded failing on today's model) and
      active guards; no physics, content or tune change

### Phase 6 (early) — Modding ✅
- [x] Mods as content layers under `content/mods/` with override-by-id, reported overrides, example mod

## Next recommended tasks
Chosen by long-term value, not ease: prefer work that improves every engine or unlocks many future systems. Each
milestone starts only when the owner authorizes it, and ends with a project gate (verify, review, merge order, define
the next milestone).

1. **Intake Gas Dynamics 2.0 (current milestone — Phase 0 complete; Phase 1, the physics, awaiting authorization).**
   Definition, phases, test matrix, acceptance criteria and Phase 0 results:
   [docs/milestones/INTAKE_GAS_DYNAMICS_2.md](docs/milestones/INTAKE_GAS_DYNAMICS_2.md). Separates
   valve-event filling from runner/plenum gas dynamics, so a cam phaser no longer carries the runner response, variable
   intakes act on phased engines, and the K20-fitted correlation constants give way to sourced ones. Phase 0 brought the
   verification tooling into the repo (regression fingerprint, recalibration driver, mutation harness; docs/VERIFICATION.md)
   and wrote the model specification and acceptance tests; open questions Q1–Q5 need the owner before Phase 1. Background:
   the M54 torque-curve investigation (SIMULATION_SPEC.md; its E10 prototype is an input, not the design).
2. **Cylinder groups on inline engines.** Banks are the unit of per-bank parts and air paths, and an inline engine may
   declare only one. That blocks an inline twin turbo (a turbo per three cylinders on one head: RB26-, N54-, 2JZ-type
   parallel twins) and split manifolds. Small: let an inline engine declare several cylinder groups that share its head
   slot (a topology rule and its tests; the per-bank model already supports shared heads), plus a synthetic I6 twin
   turbo in the matrix. See docs/ENGINE_ARCHITECTURE_AUDIT.md, gate review.
3. **Engine capabilities still missing** (ENGINE_AUTHORING_GUIDE.md §7 procedure): a supercharger category (crank-driven
   compressor with drive power), direct injection (charge cooling after the inlet valve closes), exhaust cam phasing
   (with an exhaust-opening term), per-bank fuel trim and knock control as ECU capabilities, a dry sump.
4. **Swap interfaces beyond the bellhousing:** engine mounts, clearances, cooling capacity, exhaust routing, wiring/ECU,
   driveshaft and differential; adapter parts; a swap flow in the garage.
5. **Multi-family calibration.** Re-fit the remaining level-setting constants (Otto realisation, FMEP) on several
   families at once, never per engine.
6. **Chassis dyno.** Run the whole car on rollers (wheel power, driveline loss, clutch slip under
   boost) using `VehicleSimulation`.
7. **Repairs, not just replacement.** Machining operations (bore oversize, crank regrind, head
   skim), per-cylinder state for the key failure modes.
8. **Progression (Phase 5).** Customer jobs with faults to diagnose, repair labour/time, a used-parts
   market with seeded random condition, reputation and money loop.
9. **Toe and more set-up physics.** Toe (turn-in vs stability, scrub), bump/rebound damping,
   spring-rate swaps, aero parts; engine-side adjustments (adjustable cam gears are a data change: an adjustable
   `intake_centerline_deg`; wastegate spring preload).
10. **Tracks as content and lap analysis.** Move the circuit definition to JSON; add a second layout;
    record lap telemetry (speed/throttle/brake vs distance) and compare laps.
11. **Audio.** Engine sound from rpm/load/boost (presentation only).
12. **Exported builds.** Godot export templates in CI and downloadable artifacts.

Not yet: more real engines (the architecture is proven; more engines before the intake model is right would each need
recalibration), bulk part catalogues, an open world, multiplayer, UI work beyond what a capability needs, matching any
single engine's dyno curve.

## Known issues
- Second engine family vs its reference: peak power −10 % (153 vs 170 kW) with torque +1 %. The model's curve is a plateau
  from 1,500 to 2,750 rpm and then a steady fall, where the M54 peaks at 3,500 rpm with a DISA dip near 4,000. Classified
  in SIMULATION_SPEC.md, "M54 torque-curve investigation" (11 experiments, pinned by `TorqueCurveDiagnosisTests`):
  - the shape is **missing generic physics**: a phaser moves the one filling hump whole, so it fills at its ceiling at
    every speed and the runner (and so DISA) has no effect;
  - part of the top end is **estimated flow content** (up to +6.6 % at 6,000 rpm);
  - the rest of the level is **the mean-value model's shared simplifications**; high-piston-speed losses cost both
    families about equally (the K20 sits ≈ 7 % under the real K20A3 it resembles).
  Not modelled for the M54: exhaust VANOS (the model has no exhaust-opening effect), part-load VANOS/EGR strategy,
  hot-film MAF metering (speed-density stands in), the returnless 3.5 bar fuel system (manifold-referenced regulator
  stands in), the map-controlled thermostat, dual-mass-flywheel torsional isolation.
- Engines whose banks share an element but differ (another head on one bank, a failed turbo on a shared plenum) are
  approximated: each bank takes its cylinder share of the shared element, with no cross-feed (a failed turbo's bank
  runs as naturally aspirated even though the other turbo pressurises the common plenum).
- The synthetic engines are calibrated to be plausible, not realistic (e.g. the twin-turbo V6's small turbos spool late);
  they test the architecture, not the physics against a reference.
- Idle manifold pressure is low for both families (K20 ≈ 21 kPa, M54 ≈ 15 kPa, real engines ≈ 30 kPa): no accessory
  load (alternator, pumps, A/C) is modelled.
- The vehicle model is planar. Road roughness acts through a frequency-domain ride model (grip and
  bottoming), but there is no time-domain wheel hop, kerb strike, roll-centre (geometric) load
  transfer, wall or collision. Leaving the track is punished by time, not damage.
- The air path is quasi-static (no plenum, intercooler or exhaust-manifold filling; no blow-off
  valve; no intercooler heat soak). Turbine drive pressure is slightly optimistic in the mid range.
- The ECU is open-loop speed-density only: no warm-up/transient enrichment, no coolant correction and no
  closed-loop λ trim (injector dead time is modelled, constant with voltage and pressure). A VE table calibrated
  on gasoline runs a few percent lean on E85 (charge cooling after the IAT sensor) — realistic, but the player
  has no autotune helper. There is no decel fuel cut: on closed-throttle overrun the engine keeps firing at
  manifold pressures of 4–15 kPa (the idle valve shuts; no dashpot air).
- Knock uses a mean-value cycle: no cycle-to-cycle or per-cylinder variation. Octane is worth
  ≈ 0.5° per RON at the limit (Douaud–Eyzat), on the low side of engine data.
- Tyres have no relaxation length, aligning torque or toe; the load-sensitivity law is capped at 1.3·µ₀ for
  nearly unloaded wheels (never on the stock car; ≈ 1 % of the tyre force on semi-slicks and track coilovers).
  A wider tyre's costs (mass, inertia, thermal mass) come from its part data; no tyre aero drag.
- Driving uses an automated clutch: there is no clutch pedal or manual rev-matching, and no ABS,
  traction control or stability control.
- Launch shock through shafts (wind-up, wheel hop) is not modelled, so wheelspin always protects the
  driveline; the gearbox fails only from sustained torque above its rating.
- A broken gearbox or differential means no drive at all (no "lost third gear").
- The autopilot follows the centreline (not a racing line). With powerful rear-drive builds on street
  tyres it can still spin; it recovers after 2 s.
- Engine state resets to warm each time you drive or run the dyno (so thermally activated damage in many short
  sessions is slightly lower than in one long run); dyno runs and failure reports are not saved.
- Fatigue lives are game-compressed and the overstress curve is far steeper than literature Basquin (100 → 110 %
  of a rating: rods 11×, gearbox 64×, turbo overspeed ≈ 900× shorter life). Knock is a mean single-zone cycle;
  coolant only raises knock above 90 °C (a colder wall does not lower it).
- Tyres have one lumped temperature each (no surface/core split, no per-edge temperatures) and no
  toe. Camber, pressure and temperature effects are calibrated to be plausible, not fitted to measured
  tyre data.
- Visuals are stand-ins (primitive car, flat circuit, no audio).
- This environment builds Godot from source (release downloads were blocked); CI uses the official
  4.7.2 .NET binaries, and both agree.

## Technical debt
- Engine topology (`EngineTopology`): banks with per-bank or shared parts are supported; superchargers, dry sumps,
  direct injection and exhaust cam phasing are rejected at load until they exist as capabilities. Shared elements are
  split by cylinders without cross-feed (exact for alike banks); a network solve is deferred until an engine needs it.
- Level-setting calibration is fitted to the K20 (Otto realisation 0.80, FMEP coefficients, the cam correlation's
  112° reference centreline), and the crown heat-flux and wave-tuning correlations are fitted on one family. The second
  family ran through them unchanged (+1 % torque, −10 % power against its reference); re-fitting on both families
  together — not per engine — is the honest next step, and would move the K20's pinned numbers deliberately. The
  valvetrain and thermal-conductance terms are size-aware since the validation pass.
- The VE model has one filling hump for intake closing and runner gas dynamics; a cam phaser moves all of it (optimistic
  low-speed torque with a phaser, runner length inert under one, no two-stage intakes). The split was prototyped and not
  shipped; see "Next recommended tasks" 2. Exhaust phasing has no effect to model until there is an exhaust-opening term.
- The ECU has one MAP sensor, one fuel command and one knock retard for all banks; per-bank trims are a future ECU
  capability. The engine-level `EngineConfiguration.Geometry` is the first bank's (used only for bottom-end values).
- The engine step allocates 10.4 KB (single-bank NA) / 13.1 KB (single-bank turbo) — two-bank engines ≈ 18 KB, the
  twin-turbo V6 28 KB, since each bank solves its own air path — and the vehicle step ≈ 13 KB: closures in the orifice root
  finds (≈ 5–8 KB) and live warning strings rebuilt every step (≈ 3 KB). An allocation-free root finder
  (struct-generic Brent, bit-identical) was measured ≈ 25 % slower on NA/vehicle steps under .NET 8's default
  dynamic PGO and not merged; revisit with a profiler (or cache warnings at display rate). An allocation-budget
  test guards regressions.
- Guards on fitted laws (VE floor, wall-heat scaling, tyre µ cap, weight-share and CG guards, turbine efficiency
  floor below the optimum) are flagged (debug telemetry / helpers) and tested inactive in normal running.
- `FailureMode` mixes engine and chassis modes; chassis warnings reuse the `EngineWarning` type.
- `VehicleSimulation.Step` is long (driveline, wheels and body in one loop) and should be split.
- `TrackLayout.TestFacility()` is code, not content.
- The CLI `Program.cs` has grown; split commands into classes.
- UI views rebuild their subtrees on every change event (fine at this scale; revisit with more data).
- The full test suite takes ≈ 45–65 s depending on the container (lap, wear, VE-calibration, fuzz and big-turbo gearbox
  tests); tag the slow ones if it grows.

## Rule
Each phase should produce a demonstrable playable/testable increment before proceeding.
