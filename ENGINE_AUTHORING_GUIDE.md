# Engine Authoring Guide

How engines are represented, how to add one, how to add a capability when an engine needs something the model cannot
yet represent — and what not to do. This is the canonical reference for engine work; read
[GAME_VISION.md](GAME_VISION.md) first for why it matters (*adding the 100th engine should be almost as easy as adding
the 2nd*).

**The rule:** adding an engine is a content task. It is JSON — an architecture, parts with specs and interfaces, and a
calibrated tune — checked by the validator and the test suite. Simulation code never learns an engine's name. If an
engine cannot be expressed as data, the model is missing a *capability*, and the capability is added generically
(§7), never as a special case.

---

## 1. Vocabulary

| Term | Meaning | Where it lives |
|---|---|---|
| **Engine family** | A named engine: its architecture, slots, stock parts and stock tune ("Isar M54", "Synth V8 3.9 pushrod") | `engines` in content JSON → `EngineDefinition` |
| **Architecture** | The structural facts of a family: layout (`inline`/`v`/`flat`), cylinder count, **banks** (which cylinders each holds), bank angle, firing order, and which slot serves which bank | the family's `banks`, `bank_angle_deg`, `firing_order`, slot `banks` |
| **Slot** | A place a part goes, with a category, an assembly order (`install_after`) and, for bank-scoped categories, the banks it serves | the family's `slots` |
| **Part** | A buyable, wearable component with a typed spec (geometry, flow, strength, limits) and interfaces | `parts` → `PartDefinition` + a `*Spec` |
| **Interface** | A string key a part `provides` or `requires` (a deck pattern, a flange, a bellhousing, a cam tunnel) | part `provides`/`requires` |
| **Capability** | A physical feature some engines have, enabled by part data and (usually) an ECU output: cam phasing, variable valve lift, a variable intake runner, a turbocharger | part spec fields + ECU flags + tune fields; summarised by `EngineCapabilities` |
| **Calibration** | The tune's tables and ECU settings (VE, spark, λ, cam, boost, switch speeds) — what the player tunes | `tunes` → `TuneDocument` / `EcuTune` |
| **Model constant** | A generic physics or correlation constant shared by every engine | code (`SIMULATION_SPEC.md` lists them) — never per engine |

Family ≠ architecture: two families can share an architecture (every inline-4), and one family's variants differ
only in parts. Capabilities are not flags you set on an engine — they exist exactly when a part provides them
(`EngineCapabilities.Resolve` derives the summary from the installed parts, and the physics reads the same part data).

## 2. The engine model in one page

The engine is a **mean-value** model (SIMULATION_SPEC.md): per step it solves the air path quasi-statically, burns
the trapped charge, and integrates speed, temperatures, turbo shafts and damage. Its resolution is the **bank**:

- **Engine-wide** (one part or set for the whole engine): block, main bearings, crankshaft, rod bearings, connecting
  rods, pistons, injectors, fuel pump, oil pump, sump, radiator, flywheel, ECU. Rods, pistons and injectors are sets
  of N (N = the family's cylinder count).
- **Bank-scoped** (`EngineTopology.BankCategories`): head gasket, cylinder head, valve springs, camshafts, intake
  manifold, throttle body, exhaust manifold, exhaust, turbocharger, intercooler. Every bank must be served by exactly
  one part of each required category and at most one turbocharger and intercooler. **One part may serve several banks**
  (a pushrod V8's single camshaft, a common plenum, one turbo fed by both banks of a flat-four, a Y-pipe exhaust).
- **Per bank**, the simulation keeps its own air path (`AirPath`), geometry (the shared bottom end with that bank's
  gasket and head: its own chamber, quench and compression ratio), cam profiles, valve float speed, λ, knock limit,
  peak pressure, EGT, exhaust heat, and (if fitted) its own phaser, lift stage and runner stage.
- **Shared elements** are divided by cylinders: a part serving several banks enters each bank's path with
  area × (bank cylinders / cylinders served); a shared compressor is evaluated at the bank's flow ÷ its share, and the
  bank takes that share of its power. This is exact for alike banks — two alike banks give the same answer as one
  bank holding every cylinder (tested) — and an approximation when banks sharing an element differ (no cross-feed).
- **Per turbo**, a shaft speed, wastegate position and boost-control integrator (`TurboConfiguration`, `TurboState`).
  A turbo serving one bank is a parallel twin; one serving several banks is a single turbo.
- **Engine-wide states:** speed, coolant and oil temperature, the knock controller's retard (the knock sensor hears the
  worst bank), the piston-crown temperature (the hottest bank), one MAP sensor (on the first bank's plenum — one
  sensor, as on real engines).
- **Telemetry** reports engine totals and means (sums, cylinder-weighted means, worst-bank limits) plus `Banks[]` and
  `Turbos[]` for per-bank and per-turbo channels. A single-bank engine's numbers are exactly what they were before banks
  existed (K20 and M54 are bit-identical across the refactor).

Not in the model yet (so not authorable): superchargers, direct injection, dry sumps, exhaust cam phasing, per-cylinder
state, mounts and clearances. See §9.

## 3. Anatomy of a family

```jsonc
{
  "engines": [{
    "id": "syn_v6_tt", "name": "Synth V6 3.0 twin turbo",
    "cylinders": 6, "layout": "v", "bank_angle_deg": 60,
    "banks": [{"id": "left", "cylinders": [1, 3, 5]}, {"id": "right", "cylinders": [2, 4, 6]}],
    "firing_order": [1, 2, 3, 4, 5, 6],
    "slots": [
      {"id": "block", "category": "block", "display_name": "Cylinder block"},
      {"id": "crankshaft", "category": "crankshaft", "display_name": "Crankshaft", "install_after": ["main_bearings"]},
      // ... engine-wide slots have no "banks"
      {"id": "cylinder_head_left",  "category": "cylinder_head", "display_name": "Cylinder head (left)",
       "banks": ["left"],  "install_after": ["head_gasket_left"]},
      {"id": "cylinder_head_right", "category": "cylinder_head", "display_name": "Cylinder head (right)",
       "banks": ["right"], "install_after": ["head_gasket_right"]},
      {"id": "intake_manifold", "category": "intake_manifold", "display_name": "Intake plenum",
       "install_after": ["cylinder_head_left", "cylinder_head_right"]},          // no "banks": serves both
      {"id": "turbocharger_left",  "category": "turbocharger", "banks": ["left"],  "required": false, ...},
      {"id": "turbocharger_right", "category": "turbocharger", "banks": ["right"], "required": false, ...}
    ],
    "stock_parts": {"block": "syn.v6tt.block", "cylinder_head_left": "syn.v6tt.head.left", "...": "..."},
    "stock_tune": "syn_v6_tt_base"
  }]
}
```

- `banks` may be left out: the family then has one implicit bank (`"main"`) holding every cylinder — every inline
  engine so far. `layout` must agree: `inline` = one bank, `v` ≥ 2 banks with 0° < angle < 180°, `flat` = 2 banks at 180°.
- Bank cylinder lists must partition 1..N; `firing_order` (optional) must be a permutation of 1..N.
- A slot's `banks` lists bank ids; no `banks` = every bank. Engine-wide categories must not name banks.
- `install_after` is the assembly graph (disassembly is its reverse); it must be acyclic.
- The loader rejects a family that breaks these rules with a message naming the problem ("Bank 'right' has no slot for
  'cylinder_head'", "Slots 'a', 'b' all serve bank 'left' with 'cylinder_head': each bank takes one").

## 4. Parts and interfaces

Parts are typed by category; their spec fields are listed in PARTS_DATABASE.md, with units in the field names. The
simulation reads **only specs** — never a part id.

**Interfaces** decide what bolts to what. A part `requires` keys that another installed part `provides`:

- A head requires its block's deck pattern (`"syn.v8.deck"`); the block provides it. A K20 gasket requires the K20
  deck, so it cannot go on an M54 even though its bore check would pass.
- **Bank-aware:** a bank-scoped part's requirement must be met by an engine-wide part or one serving one of the same
  banks — the right exhaust manifold's head flange must come from the right head ("…needs a part providing
  'syn.v8.exhaust_flange' on bank 'right', but only another bank has one").
- A turbo manifold provides a turbine flange (`turbo_flange.t25`) and requires `turbo.fitted`; turbochargers provide
  `turbo.fitted` and require the flange — so a turbo manifold without a turbo, or a turbo on an NA manifold, is refused.
- **Car ↔ engine:** chassis parts' requirements are checked against the engine too (`VehicleCompatibility`): a
  gearbox requires a bellhousing pattern the block provides (`k20.bellhousing`, `m54.bellhousing`). This is how an
  engine swap is decided — by interfaces, never by which engine a car was built with.
- A part with no `requires` fits anything its category fits (universal injectors, ECUs, fuel pumps). Give a part an
  interface whenever physically only some engines can take it.
- Per-cylinder sets (pistons, rods, injectors) must match the family's cylinder count.
- **Valvetrain type** (`"valvetrain": "dohc" | "sohc" | "ohv"`, default `dohc`) is declared by heads and camshafts and
  must match bank by bank. A pushrod V8's camshaft lives in the block (it requires the block's cam tunnel) and serves
  both banks from one slot.

Naming keys: `<family>.<feature>` for family-specific patterns (`m54.bellhousing`), a neutral name for shared
standards (`turbo_flange.t25`). Prefer a physical description of the interface over "fits engine X".

## 5. Capabilities available today

| Capability | Part data that enables it | ECU / tune | What the physics does |
|---|---|---|---|
| Cam timing | camshafts `intake_centerline_deg`, `exhaust_centerline_deg` | — | Intake-closing shift of the valve-event filling speed; overlap from installed centrelines |
| Intake gas dynamics (every engine) | intake manifold `runner_length_mm` (acoustic length, head port included), optional `runner_diameter_mm` | — | Each bank's runner and cylinder form one acoustic mode; its wave gain peaks at the runner's tuned speed (geometry, speed of sound of the manifold gas), independently of cam timing |
| Intake cam phasing (VVT) | camshafts `intake_phaser_range_deg` | ECU `cam_phase_control`; tune `intake_cam_advance_deg` table | Each bank's phaser follows the table (0.15 s lag; parked without oil pressure) |
| Variable valve lift (two-stage, VTEC-type) | camshafts `high_lift_profile` (`intake/exhaust_duration_deg`, `_lift_mm`) | ECU `valve_lift_control`; tune `valve_lift_switch_rpm` | Above the switch speed (150 rpm hysteresis) the bank breathes, overlaps, floats and loads its springs on the high-lift profile |
| Variable intake runner (N-stage, DISA/VIS-type) | intake manifold `switched_runner_length_mm` (two stages) or `switched_stages` (any number) | ECU `intake_runner_control`; tune `intake_runner_switch_rpm` (stage 1), `intake_runner_upper_switch_rpm` (stages 2+) | The ECU runs the highest stage whose switch speed is reached (150 rpm hysteresis at each); each stage's geometry sets the wave gain |
| Turbocharging (any count) | turbocharger parts in `turbocharger` slots; turbo manifold flanges | ECU `boost_control`, MAP range; tune `boost_target_kpa` | One shaft per turbo; each bank's air path through its turbo |
| Intercooling | intercooler part | — | Charge cooling at the bank's share of flow |
| Valvetrain type | heads and camshafts `valvetrain` | — | Compatibility (a DOHC head cannot take OHV cams); friction via valve count |
| Banks and air paths | family `banks`, slot `banks` | — | Per-bank air, combustion, geometry, heat, damage |

Hardware without the matching ECU output is a **warning**, not an error, and the hardware stays in its base state
(phaser parked, base lift, primary runner): `cam_phaser_uncontrolled`, `valve_lift_uncontrolled`,
`intake_runner_uncontrolled`. `carsim inspect` prints the resolved architecture, e.g. *"V8 (90°), 2 banks, OHV
(pushrod), 2 valves/cyl, naturally aspirated, 2 exhaust paths, firing order 1-8-4-3-6-5-7-2"*.

**Authoring an intake** (Intake Gas Dynamics 2.0; SIMULATION_SPEC.md, "Intake gas dynamics"):
- `runner_length_mm` is the **acoustic length**: from the runner's mouth in the plenum (or airbox) to the valve seat,
  **including the head's intake port**. Measure it along the centreline, or estimate it and say so in PARTS_DATABASE.md.
- `runner_diameter_mm` is the mean inner diameter (a round runner of the same mean cross-section). Leave it out only if
  unknown: the model then assumes 0.40 × bore and the validator reports `default_intake_geometry` (Info).
- `carsim inspect <id>` prints each bank's stages with β, the fundamental and the tuned speed (at 298 K): check that they
  fall where a production intake of that kind is tuned (mid to upper rev range).
- A switched intake whose switch speed is **sourced** but whose closed-stage geometry is not (a resonance flap such as
  BMW's DISA): derive the effective lower stage from the switch speed with `carsim derive-stage <id> --switch <rpm>`
  (assumption A-D1: the maker switches at the stages' full-load crossover), freeze it **before** looking at the engine's
  output, and classify it as derived (the M54 is the worked example: docs/milestones/intake-gas-dynamics-2/
  M54_DISA_DERIVATION.md). Never back-solve a stage from a dyno curve.
- Switch speeds come from the tune driver's `runner_switch` step (the crossover of adjacent stages held), set before the
  fuel and spark maps. For three or more stages, add a placeholder `intake_runner_upper_switch_rpm` list to the tune
  first.
- Not representable yet: plenum or group resonance, pipe harmonics, the intake-closing coupling of the runner's best
  speed (see §9).

## 6. Adding an engine — the procedure

1. **Reference data.** Collect geometry (bore, stroke, rod length, deck height, compression height, chamber, gasket),
   cams (durations, lifts, centrelines), flows (ports, manifolds, throttle, exhaust), strengths and limits, masses. For a
   real engine, record every value's provenance — published, measured, derived, converted, estimated or calibrated — as
   PARTS_DATABASE.md does for the M54. Do not derive compression ratio: author the volumes and let the model compute it.
2. **Architecture.** Choose layout, banks (ids and cylinder numbers), bank angle, firing order. Decide which
   bank-scoped parts are per bank and which are shared (one plenum or two? one turbo or a turbo per bank? a Y-pipe or
   dual exhausts?). The air-path topology is exactly this choice.
3. **Parts.** Author the engine-wide parts and the bank parts. Mirror-image bank parts (left and right exhaust
   manifolds) are separate parts with bank-specific flanges when they cannot swap sides; identical parts (heads on many
   V engines, gaskets) are one part id used in both slots. Give each part the interfaces it physically has. Reuse
   existing universal parts (injectors, ECUs, fuel pumps, turbos) where they fit.
4. **Family.** Slots (categories, bank scopes, `install_after`), `stock_parts`, `stock_tune`.
5. **Validate.** `carsim validate --mods <dir>` (or put the files under `content/base`) and `carsim inspect <id>`:
   fix every error and read every warning (valve float below the rev limit, coil bind, quench, compression, missing
   interfaces). The messages name the bank and slot.
6. **Calibrate the base tune with the dev tools** — never by hand-fitting a curve:
   - start from a tune with sensible limits (rev limit, idle, λ targets, injector flow, displacement, fuel density)
     and any table axes you want;
   - first the hardware schedule the maps are measured on:
     - with a phaser: `carsim calibrate-cams <id> --fuel <fuel>` → `intake_cam_advance_deg`;
     - with switched hardware: switch speeds (`valve_lift_switch_rpm`, `intake_runner_switch_rpm` and, for three or
       more intake stages, `intake_runner_upper_switch_rpm`) from full-load sweeps with each stage held (the torque
       crossover of adjacent stages; the driver's `runner_switch` / `lift_switch` steps);
   - `carsim calibrate-ve <id> --fuel <fuel>` → `volumetric_efficiency`;
   - `carsim calibrate-spark <id> --fuel <fuel>` → `ignition_advance_deg` (MBT and knock margins);
   - `calibrate-ve` again (spark changes exhaust temperature and residuals).
   A switch speed moved after the fuel map leaves the VE cells between the old and the new speed measured on the other
   stage (it happened to `syn_i6_vis`; `TuneRegenerationTests` now checks every recipe's order).
   The tune's `description` should say how it was made. The synthetic matrix was calibrated this way (its switch speeds
   were re-set in the schedule-first order by the 2026-09-27 regeneration).
   Record the recipe in `tools/CarSim.Verification/tune-manifest.json` (steps, fuel, build, hand-authored tables; a test
   requires one per tune) so `carsim regenerate-tunes` can reproduce and regenerate it (docs/VERIFICATION.md).
7. **Measure.** `carsim sweep <id> --fuel <fuel>`; for a real engine compare against its published figures with
   acceptance bands stated in advance. If it misses, find out why (content? a missing capability? a shared model
   simplification?) and document it. **Do not change model constants to hit one engine's numbers.**
8. **Car and swaps.** Give the block a bellhousing interface (reuse an existing pattern only if it is physically the
   same). Test it in a car: `carsim drive <id> --vehicle <car>`; a scenario puts it in the game.
9. **Tests.** Real engines get reference tests (as `M54ReferenceTests`) and join the cross-family pipeline tests. Run the
   whole suite: the architecture tests, the source audit and the K20/M54 pins — the regression fingerprint — must stay
   green. A real engine joins the fingerprint matrix (`FingerprintMatrix`) and the baseline is re-written.
10. **Game.** `godot --headless --path game -- --smoke-test --scenario=<scenario>` (and `--drive --smoke-test`); look at
    the Workshop, Tuning and Dyno tabs.

## 7. Adding a capability (when an engine needs code)

Code changes are justified when a real, physical feature cannot be expressed with existing specs — not when an engine's
numbers are off. Before writing code, write down the physics and which engines have it. Then:

1. **Data first.** Spec fields on the part that physically provides it (with units, ranges and validation), ECU flags
   for any new output, tune fields for any new setting. `TuneDocument` is a record copied with `with`, so a new tune
   field survives loading and save migration automatically — still add a round-trip test.
2. **Topology** if the capability adds a category or a scope (`EngineTopology`, loader checks, validator coverage).
3. **Validation**: incompatible or uncontrolled hardware gets a clear, bank-named message.
4. **Physics**: in the generic model, per bank where it acts per bank; an engine without the hardware must be
   **bit-identical** to before (run the K20/M54 identity probe or pin tests).
5. **Capability summary** (`EngineCapabilities`), CLI (`inspect`, `sweep` columns), UI (Tuning/Dyno) where the player
   needs to see or set it.
6. **Tests**: the capability's effect and its absence without ECU control; a synthetic engine in the matrix that uses
   it through the whole pipeline; a mutation check (disable the physics and show a test fails).
7. **Docs**: this guide's §5 table, SIMULATION_SPEC.md, PARTS_DATABASE.md, the ARCHITECTURE.md decision log.

## 8. What not to do

- `if (engine.Id == "…")`, `if (part.Id == "…")`, `if (cylinders == 6)`, `if (layout == "v")` in simulation, damage,
  ECU or gameplay code. (`EngineAgnosticTests` scans `src/` for family tokens, content ids and size branches.)
- Per-engine constants, or re-fitting a shared constant to make one engine match. Document the miss instead.
- "Effective" specs that lie about the part (an effective duration standing in for cam timing) to make a curve fit.
- Reading "the" part of a bank-scoped category (`FindByCategory(CylinderHead)` returns the first bank's only — use
  `PartFor(category, bank)`, `PartsOf(category)` or the bank's configuration).
- Assuming one bank, one turbo, one head or one exhaust anywhere (`Banks[0]`, `Turbos[0]`) except where the model
  deliberately has one of something (the MAP sensor) — and then say so in a comment.
- Hand-editing a calibrated table without saying so in the tune's description; tables come from the calibrators.
- Bypassing interfaces with a list of "compatible engines", or tying a car to its stock engine.
- Special-casing a test to make a new engine pass.

## 9. Current limitations and temporary assumptions

| Assumption | Why it is acceptable now | What removes it |
|---|---|---|
| Engine-wide lubrication, cooling, fuel system, ECU, knock control | True of the piston engines the game targets; one retard for all banks is how most ECUs work | Per-bank trims/knock if a feature needs them |
| One MAP sensor on the first bank's plenum; one fuel command for all banks | Speed-density ECUs have one; banks with different breathing run at different λ (a real symptom) | Per-bank fuel trim as an ECU capability |
| Shared elements split by cylinders, no cross-feed between banks | Exact for alike banks; dissimilar banks sharing a plenum or turbo are an approximation (a failed turbo's bank runs as NA even with a shared plenum) | A network solve of shared plenums/collectors |
| Mean-value, bank resolution | Captures torque, breathing, heat and damage; firing order is validated data without physics | Per-cylinder state (a later milestone) |
| One runner–cylinder mode per intake stage; no plenum/group resonance, pipe harmonics or intake-closing coupling of the runner's best speed | Implemented and validated generically (Intake Gas Dynamics 2.0 Phase 1); the deferred parts have no sourced formulation or no engine with the geometry | Phase 2 of Intake Gas Dynamics 2.0 |
| Wave amplitude κ = 0.5 unsourced, K = 2.1 from secondary sources | Shared, pre-registered, never fitted; its effect is reported at the band edges | A second real reference engine with published intake geometry and curves |
| Exhaust cam phasing, superchargers, direct injection, dry sump | Not modelled; the loader rejects families that need them | New capabilities (§7) |
| Level-setting constants fitted on the K20 | Re-fitting per engine would be hidden correction | A calibration pass over several families at once |
| Mounts, clearances, wiring, driveshafts, cooling capacity for swaps | Interfaces decide fit today (bellhousing) | Swap interfaces on mounts and chassis parts |
| An inline engine has one bank, so one turbo and one exhaust manifold per head | Every shipped and synthetic inline engine has one air path | Cylinder groups on inline engines (ROADMAP next task 2): RB26/N54-type parallel twins |

## 10. The synthetic engine matrix

`content/test/engine-matrix/` is a content layer of nine fictional engines that exist only to prove the architecture.
It is loaded as a mod (`--mods content/test`, `CARSIM_MODS_DIR=<repo>/content/test` for the game; tests use
`TestContent.Matrix`), never by the base game.

| Family | Architecture and capabilities |
|---|---|
| `syn_i4_sohc` | NA inline-4, SOHC, 2 valves/cyl, basic ECU |
| `syn_i3_turbo_vvl` | Turbo inline-3, two-stage variable valve lift |
| `syn_i4_turbo` | Turbo inline-4, intake cam phasing |
| `syn_i6_vis` | NA inline-6, variable intake runner |
| `syn_h4_turbo` | Flat-4, two banks, one turbo fed by both banks' manifolds |
| `syn_v6_na` | NA 60° V6, shared plenum, Y-pipe exhaust |
| `syn_v6_tt` | 60° V6, a turbo per bank, shared intercooler and plenum, dual exhausts, phasers |
| `syn_v8_ohv` | 90° pushrod V8, one camshaft in the block for both banks, 2 valves/cyl, dual exhausts |
| `syn_v8_dohc_vvt` | 90° DOHC V8, mirror-image heads, phasers |

Every family runs the same pipeline in `EngineMatrixTests` (load, validate, strip to the block and rebuild, start,
idle, dyno, tune response, save/load, wear and failure with a report naming the part), and `EngineMatrixDriveTests`
drives each one a lap swapped into the Isar C30 (the `<family>_swap` scenarios; `<family>_bench` puts it on the engine
stand). `ArchitectureInvariantTests` edits their JSON to
prove that cylinder count, bank count, intake paths, valve count, valvetrain type, interfaces, turbo count, per-bank
geometry, valve lift and runner switching are all followed from data. The source audit forbids their ids in `src/`.
Rules: the matrix stays content-only; if one of its engines needs code to run, that is an architecture bug to fix
generically. Its tunes were calibrated with the dev tools (§6) — regenerate them the same way after model changes.

## 11. Architectural violations

A violation is any code that makes an engine's behaviour depend on its identity rather than its data, or that
silently assumes one of something the architecture allows several of. The test suite catches the common ones:

- `EngineAgnosticTests` — source audit (family tokens, content ids, size branches), renamed-id identity, content-only
  variants.
- `ArchitectureInvariantTests` — two alike banks equal one bank of all cylinders; a part on one bank changes only that
  bank; counts and types follow the data; a renamed multi-bank family is bit-identical.
- `EngineMatrixTests` — the same pipeline for every architecture.
- K20/M54 pins and reference tests — nothing moved by accident.

If you find a violation the tests missed: add a test that fails on it (mutation-check it), fix it generically, and add
it to `docs/ENGINE_ARCHITECTURE_AUDIT.md`.
