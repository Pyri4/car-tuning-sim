# Roadmap

## Current state (2026-09-26)
**The first playable prototype is complete, and a second engine family validates that families are data.** Two
cars (Kestrel S2 coupe, Isar C30 coupé), two engine families (the fictional Kestrel K20 four and the Isar M54 straight
six, a real-engine reference: the BMW M54B30), one garage, one engine dyno and one test track, and the whole required
loop works in the game for both:

inspect → remove the engine → disassemble → replace parts → reassemble → start (dyno or car) →
tune the ECU → dyno pull → drive the test track → break something through bad parts, builds or tuning
→ read the failure report → repair in the workshop.

- Simulation lives in pure C# (`CarSim.Core`, `CarSim.Gameplay`); Godot 4.7 .NET only presents it.
- 474 automated tests (435 before the second engine family, 348 before the validation pass): simulation, content, damage, dyno, vehicle
  dynamics, wear, gameplay, saves, mods, physical invariants, property sweeps, spec fuzzing and clamp-activation
  checks. CI runs them, a CLI content check and dyno sweep, and two headless Godot smoke tests (dyno pull;
  autopilot drive) on the official Godot 4.7.2 .NET build.
- A simulation-correction phase (below) addressed the review of PR #1; a validation pass then re-checked every
  finding against independent evidence, found and fixed a regression the correction phase itself introduced
  (overrun exhaust heat burning turbines) and three remaining ECU oracles. See "Validation pass" for the status of
  every review issue.
- Content: 105 parts, 2 engine families, 2 vehicles, 5 fuels, 3 base tunes, 2 scenarios; mods load as extra
  content layers.
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
| 12 | Twin turbos, per-bank air paths, superchargers, dry sumps | **INTENTIONALLY DEFERRED** | Rejected at load with a reason; docs no longer claim otherwise | Needs model work |
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
- Answer: **yes, primarily through data.** Generic simulation code gained one abstraction (cam timing) and no
  engine-specific branch; see "Known issues" for what the M54 still cannot match.

### Phase 6 (early) — Modding ✅
- [x] Mods as content layers under `content/mods/` with override-by-id, reported overrides, example mod

## Next recommended tasks
1. **Toe and more set-up physics.** Toe (turn-in vs stability, scrub), bump/rebound damping,
   spring-rate swaps, aero parts; engine-side adjustments (adjustable cam gears are now a data change: an adjustable
   `intake_centerline_deg`; wastegate spring preload).
2. **Two-family calibration.** Re-fit the level-setting constants on both families at once (not per engine), make the
   VE ceiling depend on the tuned speed, add an exhaust-opening term so exhaust phasing and scavenging mean something.
3. **Chassis dyno.** Run the whole car on rollers (wheel power, driveline loss, clutch slip under
   boost) using `VehicleSimulation`.
4. **Tracks as content and lap analysis.** Move the circuit definition to JSON; add a second layout;
   record lap telemetry (speed/throttle/brake vs distance) and compare laps.
5. **Progression (Phase 5).** Customer jobs with faults to diagnose, repair labour/time, a used-parts
   market with seeded random condition, reputation and money loop.
6. **Repairs, not just replacement.** Machining operations (bore oversize, crank regrind, head
   skim), per-cylinder state for the key failure modes.
7. **Audio.** Engine sound from rpm/load/boost (presentation only).
8. **Exported builds.** Godot export templates in CI and downloadable artifacts.

## Known issues
- Second engine family vs its reference: peak power −10 % (153 vs 170 kW) with torque +1 %; the model's torque plateau
  runs from idle to ≈ 3,000 rpm and falls earlier than the M54's. With a cam phaser the VE shape reaches its ceiling at
  any speed it is tuned to (real low-speed filling lacks ram), DISA's mid-range resonance is one effective runner, and
  high-piston-speed losses cost both families about equally (the K20 sits ≈ 7 % under the real K20A3 it resembles).
  Not modelled for the M54: exhaust VANOS (the model has no exhaust-opening effect), part-load VANOS/EGR strategy,
  hot-film MAF metering (speed-density stands in), the returnless 3.5 bar fuel system (manifold-referenced regulator
  stands in), the map-controlled thermostat, dual-mass-flywheel torsional isolation.
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
- The engine model supports one part (or set) per modelled category: twin turbos, per-bank air paths,
  dry sumps and superchargers are rejected at load, not supported (`EngineTopology`).
- Level-setting calibration is fitted to the K20 (Otto realisation 0.80, FMEP coefficients, the cam correlation's
  112° reference centreline), and the crown heat-flux and wave-tuning correlations are fitted on one family. The second
  family ran through them unchanged (+1 % torque, −10 % power against its reference); re-fitting on both families
  together — not per engine — is the honest next step, and would move the K20's pinned numbers deliberately. The
  valvetrain and thermal-conductance terms are size-aware since the validation pass.
- The VE shape's ceiling does not depend on the speed it is tuned to, which only matters with a cam phaser (optimistic
  low-speed torque); exhaust phasing has no effect to model until there is an exhaust-opening term.
- Tunes are rebuilt field by field in two places (`ContentLoader.AddTune`, `SaveSystem.Migrate`): a new tune field
  must be added to both (the cam map was silently dropped by the loader until caught).
- The engine step allocates 10.6 KB (NA) / 13.9 KB (turbo), the vehicle step 11.9 KB: closures in the orifice root
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
