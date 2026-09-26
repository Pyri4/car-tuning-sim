# Engine architecture audit (2026-09-26)

What the codebase assumed about engines before the engine-architecture milestone, and what was done about each
assumption. It was written before any code changed, as the plan for that milestone; the outcome column records what
was actually done. [ENGINE_AUTHORING_GUIDE.md](../ENGINE_AUTHORING_GUIDE.md) describes the architecture that resulted.

Classification:
1. **Already generic** — works for any engine the data describes.
2. **Acceptable temporary limitation** — a real simplification, documented, not a blocker for new engines.
3. **Must fix now** — it blocks the synthetic engine matrix or hides an engine-specific assumption.
4. **Can defer** — needed eventually (swaps, economy, visuals), not by this milestone.
5. **Requires a new generic capability** — a physical feature real engines have that the model cannot represent.

Method: the audit read every file in `src/`, `tools/` and `game/scripts/` looking for structural assumptions, not
just engine ids: fixed counts, "the first part of this category", slot-id and part-id checks, calibration constants
fitted on one engine, and UI or CLI code that only makes sense for one topology.

## A. Engine assumptions

| # | Finding | Where | Class | Outcome |
|---|---|---|---|---|
| A1 | No engine id, part id or cylinder-count branch in simulation code (the second-family source audit enforces it) | `src/` | 1 | Kept; the audit test now also forbids content ids of the synthetic matrix |
| A2 | Cylinder count is an integer from the block (1–16), used everywhere as `n`; no `for (i < 4)` loops | `EngineGeometry`, `AirPath`, `DamageModel` | 1 | — |
| A3 | `layout` is a free label ("inline", "v", "flat"); there are no banks, bank angle or firing order | `EngineDefinition`, `EngineTopology.Layouts` | 3 | Engine families gained banks (ids + cylinder numbers), bank angle and firing order, all validated. Layout must agree with the bank count |
| A4 | A V engine can only be authored as "a pair of heads sold as one set" — the model cannot tell the banks apart | `EngineTopology` doc | 3 | Bank-scoped slots: one head, gasket, spring set, camshaft set, exhaust manifold (etc.) per bank |
| A5 | Valvetrain type (OHV/SOHC/DOHC) is not represented; a pushrod cam in the block and a DOHC head are indistinguishable | `CamshaftSpec`, `CylinderHeadSpec` | 3 | `valvetrain` on heads and cams, checked against each other per bank; an OHV cam serves both banks from one slot |
| A6 | Valves per cylinder exist (2–5) and feed friction | `CylinderHeadSpec`, `CombustionModel.FrictionMep` | 1 | Now per bank |
| A7 | Intake cam phasing is the only variable-valvetrain feature; no variable lift | `CamshaftSpec` | 5 | New capability: two-stage variable valve lift (a second cam profile, ECU switch speed) |
| A8 | Exhaust cam phasing is not modelled (no exhaust-opening term) | SIMULATION_SPEC "Cam timing" | 2 | Documented, unchanged |
| A9 | Firing order and crank phasing have no physics (mean-value model: no torque pulsation, balance or per-cylinder variation) | — | 2 | Firing order is validated data (a permutation of the cylinders); physics deferred |
| A10 | The VE correlation's constants were fitted on the K20 (112° centreline, 15 m/s @ 220°, runner exponent) | `EngineConfiguration` | 2 | Documented as class-C calibration; not re-fitted (see the M54 investigation) |

## B. Topology assumptions

| # | Finding | Where | Class | Outcome |
|---|---|---|---|---|
| B1 | Exactly one part per category; more than one slot of a category is rejected at load | `EngineTopology.CheckFamily` | 3 | Categories now have a scope. **Engine-wide** (block, crank, bearings, rods, pistons, injectors, fuel pump, oil pump, sump, radiator, flywheel, ECU): one part for the whole engine. **Bank-resolvable** (gasket, head, springs, cams, intake manifold, throttle, exhaust manifold, exhaust, turbo, intercooler): each bank is served by exactly one part (at most one for optional categories), and one part may serve several banks |
| B2 | One air path: filter → [compressor → intercooler] → throttle → plenum → ports; ports → manifold → [turbine ∥ wastegate] → system | `AirPath` | 3 | One air path per bank, built from the parts that serve it. Several banks may share an element (a common plenum, one turbo, a Y-pipe); the solve then gives each bank its cylinder share of the element (exact for symmetric banks — tested) |
| B3 | At most one turbocharger and one intercooler | `EngineConfiguration.Turbo` | 3 | Any number of turbos, each with its own shaft, wastegate and boost-control state; a turbo may serve one bank (parallel twins) or several (one turbo on a flat four) |
| B4 | No supercharger, no twin-charging, no sequential turbos | — | 5 (deferred) | Documented; a supercharger is a new category (crank-driven compressor) and needs a drive-power term |
| B5 | One crankshaft, one oil system, one cooling circuit, one fuel system, one ECU | — | 2 | Kept engine-wide, documented as intentional for piston engines this game targets |
| B6 | Wet sump only (sump capacity + surge limit) | `OilPanSpec` | 4 | Documented; a dry sump is a pump + tank capability for later |
| B7 | Injectors are one set for the engine; port injection implied | `InjectorSpec` | 2 | Documented; direct injection is a future capability (charge cooling and knock would change) |
| B8 | The ECU's MAP sensor reads "the" manifold | `EngineSimulation` | 3 | It reads the plenum of the first bank's air path (one sensor, as on real engines); documented |
| B9 | Interface requirements are satisfied by any installed part, wherever it is | `AssemblyValidator` | 3 | A bank-scoped part's requirement must be met by a part serving one of its banks (the right exhaust manifold cannot bolt to the left head) |

## C. Part-system assumptions

| # | Finding | Where | Class | Outcome |
|---|---|---|---|---|
| C1 | Parts are typed by category; specs carry units; no part-id logic | `PartSpecRegistry`, `PartSpec` | 1 | — |
| C2 | Interfaces are flat string keys (`provides`/`requires`) | `PartDefinition` | 1 | Kept; now also bank-aware (B9) and checked across engine and car (C4) |
| C3 | Per-cylinder sets (pistons, rods, injectors) are counted against the family's cylinder count | `AssemblyValidator` | 1 | — |
| C4 | Chassis parts' `requires` are never validated against the engine; the car hard-codes its engine family | `VehicleDefinition.Engine`, scenario check | 3 | A car names a **stock** engine only; whether an engine fits a car is decided by interfaces (the gearbox requires the block's bellhousing pattern), checked when the car is built and when a scenario loads |
| C5 | No adapter, mount, clearance or dimension data for swaps | — | 4 | Deferred; the interface mechanism is where adapters will plug in |
| C6 | Spec ranges are generous (cylinders 1–16, bore 40–150 mm) and fuzz-tested | `*Specs.cs` | 1 | — |

## D. Simulation assumptions

| # | Finding | Where | Class | Outcome |
|---|---|---|---|---|
| D1 | `EngineConfiguration` exposes one `Head`, `Cams`, `Springs`, `HeadGasket`, `Intake`, `Throttle`, `ExhaustManifold`, `Exhaust`, `Turbo`, `Intercooler` | `EngineConfiguration` | 3 | These moved into `BankConfiguration` (one per bank) and `TurboConfiguration` (one per turbo); nothing reads "the first bank" |
| D2 | Combustion, knock, EGT and friction are computed once for the whole engine | `EngineSimulation.Step` | 3 | Computed per bank (its own air, λ, knock limit, peak pressure, pumping and valvetrain friction, exhaust temperature) and summed or bounded explicitly |
| D3 | Engine-wide states (speed, coolant, oil, crown temperature, knock retard) | `EngineState` | 2 | Kept engine-wide; crown temperature follows the hottest bank, the knock sensor hears the worst bank (one retard for all — documented) |
| D4 | Damage finds "the" part of a category | `DamageModel`, `StressEvaluator`, `FailureDiagnostics`, `Inspection` | 3 | Stress readings name the part instance; a bank's gasket, head, springs and turbo are loaded from that bank's operating point; collateral damage stays on the failed part's banks |
| D5 | Mean-value, not per-cylinder | whole model | 2 | Documented; banks are the finest resolution |
| D6 | Under a cam phaser the intake runner is inert and DISA cannot act (single filling hump) | `AirPath.VeShape` | 2 → next milestone | Documented in the M54 investigation; the variable-runner capability added here acts through the existing runner term, so it has its full effect on fixed-cam engines and none under a phaser until the intake-filling model is split |
| D7 | Size-dependent constants scale with displacement or bore (friction, oil, heat) since the validation pass | various | 1 | — |

## E. UI assumptions

| # | Finding | Where | Class | Outcome |
|---|---|---|---|---|
| E1 | Workshop groups slots by category (not slot id) | `EngineView` | 1 | Bank-scoped slots list under their group with the bank in their name |
| E2 | Dyno gauges show one turbo, one cam position, one λ | `DynoView` | 3 | Per-bank λ/EGT/knock and per-turbo speed when an engine has more than one; the valve-lift stage and runner stage |
| E3 | Tuning shows the cam table only | `TuningView` | 3 | Valve-lift and runner switch speeds are editable tune fields |
| E4 | New game offers every scenario | `GarageView` | 1 | — |

## F. Save/load assumptions

| # | Finding | Where | Class | Outcome |
|---|---|---|---|---|
| F1 | Saves store the family id and a slot-id → part map; tunes field by field | `SaveSystem` | 1 (with a known trap) | New tune fields added to both copies (loader and migration) and covered by a round-trip test on an engine that uses them |
| F2 | Part instances, wear, fatigue ledgers keyed by instance | `SaveSystem` | 1 | — |

## G. CLI assumptions

| # | Finding | Where | Class | Outcome |
|---|---|---|---|---|
| G1 | Commands take an engine id (required with several families) | `Program.EngineId` | 1 | — |
| G2 | `drive` builds the engine's own car | `BuildCar` | 3 | It uses a scenario (`--scenario`) or the family's stock car; engines without a car are refused with a reason |
| G3 | `inspect` prints geometry and the report | `Inspect` | 3 | Also the architecture (banks, layout, firing order) and the resolved capabilities |
| G4 | `sweep` prints one set of columns | `Sweep` | 3 | Adds per-bank and per-turbo columns when the engine has several; valve-lift and runner stages |
| G5 | `calibrate-cams` uses the single intake phaser | `CalibrateCams` | 1 | Works per engine; every bank's phaser follows the same table |

## H. Test assumptions

| # | Finding | Where | Class | Outcome |
|---|---|---|---|---|
| H1 | `TestContent.Families` = {K20, M54}; helpers default to the K20 | `TestContent`, `SimFactory` | 1 | The synthetic matrix is a content layer loaded beside them |
| H2 | Source audit: family tokens, content ids, size branches | `EngineAgnosticTests` | 1 | Extended to the synthetic ids and to "first bank" shortcuts |
| H3 | Tests read `Config.Turbo`, `Config.Cams`, `State.TurboOmega` | turbo, cam tests | 3 | Read the bank or turbo they mean |
| H4 | The content-only variant test mutates one engine (8 cylinders) | `EngineAgnosticTests` | 1 | Joined by bank, valve-count, air-path and turbo-count mutations |

## Implementation sequence (as planned)
1. Architecture data: banks, bank angle, firing order, bank-scoped slots; the loader and topology checks with
   diagnostic messages. Existing families gain nothing (one implicit bank).
2. Specs and tune fields for the new capabilities: valvetrain type, two-stage valve lift, switched intake runner, ECU
   outputs; both tune copies (loader, save migration).
3. Validator: per-bank completeness, valvetrain match, bank-aware interfaces, per-bank coil bind, valve float,
   gasket bore; capability summary.
4. `EngineConfiguration` → per-bank and per-turbo configurations; `AirPath` per bank with shared-element shares.
5. `EngineSimulation`: per-bank air, combustion, heat and exhaust; per-turbo shafts; aggregated telemetry.
   **Gate:** K20 and M54 bit-identical to the PR #4 head (full-precision probe).
6. Damage, diagnostics and inspection per part.
7. Car ↔ engine by interfaces; scenarios may put any fitting engine in any car.
8. CLI, UI.
9. Synthetic engine matrix as a content layer, calibrated with the dev tools; architecture and mutation tests.
10. Documentation: ENGINE_AUTHORING_GUIDE.md, the vision and project memory, spec, parts schema, roadmap.
