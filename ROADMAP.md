# Roadmap

## Current state (2026-09-26)
**The first playable prototype is complete.** One car (Kestrel S2 coupe), one engine family (Kestrel
K20), one garage, one engine dyno and one test track, and the whole required loop works in the game:

inspect → remove the engine → disassemble → replace parts → reassemble → start (dyno or car) →
tune the ECU → dyno pull → drive the test track → break something through bad parts, builds or tuning
→ read the failure report → repair in the workshop.

- Simulation lives in pure C# (`CarSim.Core`, `CarSim.Gameplay`); Godot 4.7 .NET only presents it.
- 246 automated tests (simulation, content, damage, dyno, vehicle dynamics, wear, gameplay, saves,
  mods). CI runs them plus two headless Godot smoke tests (dyno pull; autopilot drive) on the official
  Godot 4.7.2 .NET build.
- Content: 77 parts, 1 engine family, 1 vehicle, 5 fuels, 2 base tunes, 1 scenario; mods load as extra
  content layers.
- Reference numbers: stock K20 ≈ 148 hp / 189 N·m (the worn project car ≈ 137 hp); T28 turbo build ≈ 230
  hp / 300 N·m; stock car 0–100 km/h ≈ 8.5 s, ≈ 0.9 g skidpad, ≈ 51.7 s autopilot lap.

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
      reversion, speed-density ECU, fuel pump/regulator/injectors, combustion with MBT, knock-limited
      advance (now knock-free at light load), FMEP/PMEP, peak cylinder pressure
- [x] Turbocharger: compressor map (surge, choke, efficiency island), turbine and wastegate, shaft
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

### Phase 6 (early) — Modding ✅
- [x] Mods as content layers under `content/mods/` with override-by-id, reported overrides, example mod

## Next recommended tasks
1. **Toe and more set-up physics.** Toe (turn-in vs stability, scrub), bump/rebound damping,
   spring-rate swaps, aero parts; engine-side adjustments (cam gears, wastegate spring preload).
2. **Chassis dyno.** Run the whole car on rollers (wheel power, driveline loss, clutch slip under
   boost) using `VehicleSimulation`.
3. **Tracks as content and lap analysis.** Move the circuit definition to JSON; add a second layout;
   record lap telemetry (speed/throttle/brake vs distance) and compare laps.
4. **Progression (Phase 5).** Customer jobs with faults to diagnose, repair labour/time, a used-parts
   market with seeded random condition, reputation and money loop.
5. **Repairs, not just replacement.** Machining operations (bore oversize, crank regrind, head
   skim), per-cylinder state for the key failure modes.
6. **Audio.** Engine sound from rpm/load/boost (presentation only).
7. **Exported builds.** Godot export templates in CI and downloadable artifacts.

## Known issues
- The vehicle model is planar: no vertical dynamics, bumps, kerb height, walls or collisions. Grass
  only lowers grip; leaving the track is punished by time, not damage.
- Driving uses an automated clutch: there is no clutch pedal or manual rev-matching, and no ABS,
  traction control or stability control.
- Launch shock through shafts (wind-up, wheel hop) is not modelled, so wheelspin always protects the
  driveline; the gearbox fails only from sustained torque above its rating.
- A broken gearbox or differential means no drive at all (no "lost third gear").
- The autopilot follows the centreline (not a racing line). With powerful rear-drive builds on street
  tyres it can still spin; it recovers after 2 s.
- Engine state resets to warm each time you drive or run the dyno; dyno runs and failure reports are
  not saved.
- Tyres have one lumped temperature each (no surface/core split, no per-edge temperatures) and no
  toe. Camber, pressure and temperature effects are calibrated to be plausible, not fitted to measured
  tyre data.
- Visuals are stand-ins (primitive car, flat circuit, no audio).
- This environment builds Godot from source (release downloads were blocked); CI uses the official
  4.7.2 .NET binaries, and both agree.

## Technical debt
- Chassis slot ids (`clutch`, `tires_front`, ...) are hard-coded in `VehicleConfiguration` and
  `ChassisWearModel`; slot roles should come from data like the engine's categories.
- `FailureMode` mixes engine and chassis modes; the fatigue fields of `FailureModeInfo` are unused by
  the wear-driven chassis modes. Chassis warnings reuse the `EngineWarning` type.
- `VehicleSimulation.Step` is long (driveline, wheels and body in one loop) and should be split.
- `TrackLayout.TestFacility()` is code, not content.
- The CLI `Program.cs` has grown; split commands into classes.
- UI views rebuild their subtrees on every change event (fine at this scale; revisit with more data).
- The full test suite takes ≈ 40 s, mostly lap and wear tests; tag them if it grows.

## Rule
Each phase should produce a demonstrable playable/testable increment before proceeding.
