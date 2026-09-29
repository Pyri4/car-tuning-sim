# Project State — canonical cross-conversation handoff

**Read this before substantial work.** It is the project's memory between AI conversations. It summarizes and links;
the documents it links stay authoritative for their own subjects (vision, architecture, physics, schema, verification).
A machine-readable summary lives beside it: [project-state.json](project-state.json).

| | |
|---|---|
| **Snapshot date** | 2026-09-29 (Phase 3 gate; required fixes after its adversarial review) |
| **`main` at snapshot** | `df9c987` (Merge pull request #9, this handoff document) on top of `c07804a` (PR #8, Phases 1 & 2) |
| **This document written on** | branch `claude/gallant-gauss-jkbqvi` (Phase 3: code `74e7480`, `aec1a2e`; docs `aed05e8`; CI record `69582a9`; the review's required fixes in one commit on top), **not merged, no PR opened** |
| **Current milestone** | Engine Authoring Factory 1.0 — Phases 1 and 2 merged; **Phase 3 implemented, reviewed adversarially, required fixes applied — at its gate** ([report](milestones/engine-authoring-factory-1/PHASE3_GATE_REPORT.md), §16 for the fixes) |
| **Next action** | §13 |

Everything below is verified against the repository and GitHub at the snapshot unless marked **UNVERIFIED** or
**STALE ELSEWHERE**. When this document and the repository disagree, the repository wins: re-verify and update this file.

---

## 1. What the project is
A deep car building, repair, tuning and simulation game (inspired by Car Mechanic Simulator, Street Legal Racing:
Redline, Automation and BeamNG.drive) whose emphasis is mechanical construction, part compatibility, diagnostics,
realistic failure, the dyno, telemetry and rebuilding — not arcade racing. Canonical statement:
[GAME_VISION.md](../GAME_VISION.md).

- Long-term loop: BUY → INSPECT → DISASSEMBLE → DIAGNOSE → REPAIR → BUILD → MODIFY → SWAP → TUNE → DYNO → DRIVE →
  BREAK → DIAGNOSE → REBUILD. The prototype implements inspect, disassemble, replace, reassemble, start, tune, dyno,
  drive, break, read the report and repair. Buying/market, machining repairs, customer jobs and full swap flows are
  later.
- Scale goal: hundreds of engine families, thousands of parts, many cars, engine swaps. **"Adding the 100th engine
  should be almost as easy as adding the 2nd."**
- Current strategic direction (since 2026-09-28): **content scale is a first-class goal** — make a new engine a
  data-entry + validation task with zero new C#, without weakening physics, verification or provenance.

## 2. Rules every agent must follow
These restate [AGENTS.md](../AGENTS.md) and [ENGINE_AUTHORING_GUIDE.md](../ENGINE_AUTHORING_GUIDE.md) §8/§11; those
files are authoritative.

1. **Never branch on identity** in simulation, damage, ECU, calibration, tune-generation or gameplay code: no
   `engine.Id ==`, `part.Id ==`, family names, cylinder-count or layout special cases, no `specialCaseM54()`, no
   `power += …` / `torque *= …` corrections. `EngineAgnosticTests` audits `src/`; mutants `family-id-branch` and
   `cylinder-count-branch` prove the audit bites.
2. **Physics is code, engines are content.** A missing physical feature becomes a generic, data-driven, tested
   capability (guide §7), bit-identical for engines without the hardware.
3. **Reference data is evidence, never a target.** Do not re-fit shared constants, loosen tolerances or add per-engine
   curves to make one engine match. Document and classify the miss (A generic / B documented limitation / C must fix
   before the next milestone / D future).
4. **Parts change the engine only through their specifications** (no "Stage 1/2/3", no percentage bonuses). Parts fit
   through bank-aware `provides`/`requires` interfaces; cars and engines fit through interfaces (bellhousing today).
5. **Provenance:** never present an estimated or fitted value as published or measured; never invent a source; a
   value of unknown origin has no record. Facts with citations only; nothing copied from other games or mods.
6. **Regression:** the K20, M54 and synthetic-matrix outputs are pinned by `tests/baselines/fingerprint.txt`. Do not
   re-baseline unless a change is deliberate, approved and reported with its diff (docs/VERIFICATION.md).
7. **Milestones:** start only when the owner authorizes; *proposed* is not authorized. Each ends with a project gate
   (`car-sim-project-gate`). Dependent phases are based on the merged previous phase; stacking on an unmerged PR needs
   explicit owner authorization.
8. **Git:** never merge, close, or force-push without explicit authorization; never rewrite unrelated history.
9. **When a gate fails, stop and investigate.** Do not weaken tests or tolerances to pass it.
10. **Model routing** (AGENTS.md table): small models gather evidence and do patterned content/docs/tests; they never
    decide abstractions, tolerances, regressions or physical validity. Escalate instead of guessing. Fable is not in
    the small-model pool.

## 3. Technology and layout (verified)
- Godot **4.7.2 .NET** for presentation; simulation in pure **C# / .NET 8** with zero Godot references
  ([ARCHITECTURE.md](../ARCHITECTURE.md) §1–2). `global.json` pins SDK 8.0.100, `rollForward: latestFeature`.
- `src/CarSim.Core` — simulation and domain: `Common`, `Content` (loader, database, provenance, tune documents),
  `Damage`, `Dyno`, `Ecu` (ECU plus the dev calibrators `VeCalibrator`, `SparkCalibrator`, `CamPhaseCalibrator`),
  `Engines` (definition, topology, geometry, capabilities, assembly, `AssemblyValidator`, `EngineFeatures`,
  `EngineCheck`, `EngineDerivedValues`), `Fuels`, `Parts`, `Simulation` (air path, banks, `IntakeGasDynamics`, turbo, thermal…), `Vehicles`.
- `src/CarSim.Gameplay` — garage, driving session, chassis bench, saves.
- `game/` — Godot project (UI, scenes); consumes the core.
- `tools/CarSim.Cli` (`carsim`) — commands: `validate`, `inspect`, `check-engine`, `sweep`, `hold`, `drive`,
  `calibrate-ve`, `calibrate-spark`, `calibrate-cams`, `derive-stage`, `bench`, `fingerprint`, `fingerprint-diff`,
  `regenerate-tunes`, and (on the Phase 3 branch) `generate-tune` (`GenerateTuneCommand.cs`, its own file).
- `tools/CarSim.Verification` — fingerprint recorder/runner/baseline, `TuneRegenerator` (core `Settle`, `BuildAssembly`),
  `TuneGenerator` (Phase 3 branch), `RunnerStageDerivation`, `tune-manifest.json` (one recipe per tune: calibrator
  steps + `hand_authored` tables).
- `tools/CarSim.MutationCheck` — mutation harness, `mutations.json` (43 entries on `main`; 59 on the Phase 3 branch).
- `tools/agent-skills/` — project skill sources.
- `tests/CarSim.Core.Tests` — xUnit; `tests/baselines/fingerprint.txt` — regression baseline (35 cases, 100 sections).
- `content/base/` — shipped content; `content/test/engine-matrix/` — synthetic engines (test-only, never shipped);
  `content/mods/` — mod layers; `docs/example-mod/`.

Content at snapshot (`carsim validate`): base **105 parts, 2 engines, 5 fuels, 3 tunes**; with the matrix
(`--mods content/test`) **247 parts, 11 engines, 5 fuels, 12 tunes**. Two vehicles (Kestrel S2, Isar C30); scenarios
for both real engines plus 18 matrix scenarios.

## 4. Architecture summary
Details: [ARCHITECTURE.md](../ARCHITECTURE.md), [SIMULATION_SPEC.md](../SIMULATION_SPEC.md),
[ENGINE_AUTHORING_GUIDE.md](../ENGINE_AUTHORING_GUIDE.md), [docs/ENGINE_ARCHITECTURE_AUDIT.md](ENGINE_ARCHITECTURE_AUDIT.md).

- **Engine = architecture + parts + interfaces + assembly.** An engine definition declares layout, cylinders, banks,
  bank angle, firing order and slots; parts fill slots through interfaces; `AssemblyValidator` enforces physical rules
  (bore, pins, journals, clearances, compression, coil bind, valve float, ratings vs rev limit, turbo/MAP/boost).
- **Mean-value engine model**, per bank: compressible-orifice air path, VE from cams/runners/headers, residuals,
  speed-density ECU with a VE table, fuel system (pump, regulator, injectors with dead time), combustion with MBT,
  end-gas knock, FMEP/PMEP, thermal and lubrication, damage with fatigue and explanatory failure reports.
- **Generic architecture (Architecture 2.0, PR #5):** inline/V/flat layouts, multiple banks with per-bank air path,
  geometry, combustion, knock limit, heat; any number of turbos each with its own shaft/wastegate/boost state;
  OHV/SOHC/DOHC; intake cam phasing; two-stage VVL; N-stage variable intake runners; engine↔car fit by interfaces.
  Proven by a nine-engine synthetic matrix.
- **Intake Gas Dynamics 2.0 Phase 1 (PR #7):** runner wave gain separated from valve-event filling; one
  runner–cylinder mode per stage and bank. Shared constants in `src/CarSim.Core/Simulation/IntakeGasDynamics.cs`
  (verified): `TunedFrequencyRatio` K = **2.1** (empirical, band 2.0–2.2), `Damping` ζ = **0.35**, `AmplitudePerMach`
  κ = **0.5** (unsourced, pre-registered). Solved via the distributed model (x·tan x = β form, see SIMULATION_SPEC.md).
- **Vehicle:** planar model, combined-slip tyres with temperature and wear, gearbox with automated clutch,
  differentials, brakes, suspension set-up with ride over roughness; test-track scene in Godot.
- **Authoring layer (Engine Authoring Factory Phases 1–2):** `identity` (real/fictional/synthetic, features), `sources`
  documents, `provenance` maps on parts and engines, `extends` + `abstract` for engines and parts (resolved after all
  layers into plain definitions), feature vocabulary with modelled/not-modelled/missing-data statuses,
  `carsim check-engine`. **Phase 3 (branch, not merged):** `EngineDerivedValues` (a view over `EngineGeometry` and
  `RunnerStageConfiguration`, one source per derived quantity, statuses authored/defaulted/derived/validated/mismatch/
  unavailable; `check-engine` DERIVED, `inspect`) and `carsim generate-tune` (check gate → explicit fuel → beliefs from
  build and fuel → hand-authored policy from a policy tune → the manifest's recipe or its rule applied to the hardware →
  `TuneRegenerator.Settle` until the tune is a fixed point → validation → the tune format + a `.recipe.jsonc` record;
  never over a file it did not write).

**What is genuinely supported vs limited** is tabulated in ENGINE_AUTHORING_GUIDE.md §5 (capabilities) and §9
(limitations). Rejected at load today: superchargers, dry sumps, direct injection, exhaust cam phasing. Inline engines
have one bank (no parallel twin-turbo inline yet: "cylinder groups" is a roadmap item).

## 5. Completed milestones (all merged into `main`)
Verified from `git log --first-parent origin/main` and GitHub PR data.

| # | Milestone | PR / merge | Result (from its report; key numbers re-checked where noted) |
|---|---|---|---|
| 1 | Prototype (Phases 0–4, set-up, modding) | PRs #1–#3 (`5ed22f4`, 2026-09-26) | Full loop playable for one car/engine |
| 2 | Simulation-correction phase + validation pass | PR #2 | 21 review issues classified (ROADMAP "Validation pass") |
| 3 | Second engine family (Isar M54 = BMW M54B30 reference) | PR #4 (`0970bbe`, 2026-09-27) | Families are data; one generic capability added (cam timing); cost measured: 2,568 lines total, 380 engine content |
| 4 | Engine Architecture 2.0 | PR #5 (`b3322ef`, 2026-09-27) | Banks, per-bank air paths, N turbos, capabilities as data, nine-engine synthetic matrix; K20/M54 bit-identical |
| 5 | Agent skills | PR #6 (`ea63265`, 2026-09-27) | `car-sim-verify`, `car-sim-add-engine`, `car-sim-project-gate`, `find-skills` |
| 6 | Intake Gas Dynamics 2.0 Phase 0 + design resolution + authorization gate + Phase 1 | PR #7 (`af0b289`, 2026-09-28) | Fingerprint, tune driver and mutation harness added; generic runner resonance + DISA; reports in `docs/milestones/intake-gas-dynamics-2/` |
| 7 | Engine Authoring Factory audit | in PR #8 | Authoring cost measured; milestone defined |
| 8 | Engine Authoring Factory 1.0 **Phase 1** (schema + provenance) | PR #8 (`c07804a`, 2026-09-29) | 728 tests, 35/35 mutants; [PHASE1_GATE_REPORT.md](milestones/engine-authoring-factory-1/PHASE1_GATE_REPORT.md) |
| 9 | Engine Authoring Factory 1.0 **Phase 2** (`check-engine`) | PR #8 (`c07804a`, 2026-09-29) | 766 tests, 43/43 mutants; [PHASE2_GATE_REPORT.md](milestones/engine-authoring-factory-1/PHASE2_GATE_REPORT.md) |

Phase 2 details worth knowing without opening the report:
- `carsim check-engine <id> [--verbose 1] [--strict 1] [--content/--mods]`. Exit codes: **0** no errors (warnings
  allowed), **2** errors, **3** warnings under `--strict`, **1** usage error.
- Sections: CONTENT, IDENTITY, ARCHITECTURE, PARTS, TOPOLOGY, INTERFACES, GEOMETRY, LIMITS, FEATURES, PROVENANCE,
  COMPLETENESS. Reuses `EngineTopology`, `AssemblyValidator`, `EngineGeometry`, `EngineCapabilities`, `FeatureReport`,
  `VehicleCompatibility`. Static only (no simulation run).
- Severity: unmodelled feature *with* an approximation → warning; declared unmodelled feature *without* one → load
  error (blocks); modelled feature declared without providing hardware → error (`feature_missing_data`); estimated data
  alone → never an error.
- Stock tune ↔ build checks exist already: displacement, injector flow, dead time, rev limit vs ECU, cam table and
  switch speeds for controlled hardware, switch speeds inside the rpm axis, load axis vs boost target.
- B58-style fixture (test-only): direct injection, Valvetronic-type VVL, exhaust VANOS and twin scroll reported as
  not modelled. **No B58 content exists or should be added** until those capabilities exist.
- LS3-style fixture (test-only, synthetic pushrod V8 under a real identity) passes. Findings for a future LS3 pilot:
  no loaded gearbox takes a GM LS bellhousing (content fix); L99 cylinder deactivation not modelled; cam-in-block
  phaser moves intake and exhaust together, the model's phaser moves intake only (generic limitation); V8 slot lists
  are hand-written (~104 lines); fuel-system capacity is not checked.

## 6. Current milestone: Engine Authoring Factory 1.0
Definition: [docs/milestones/ENGINE_AUTHORING_FACTORY_1.md](milestones/ENGINE_AUTHORING_FACTORY_1.md) (status:
authorized phase by phase). Evidence: [docs/ENGINE_AUTHORING_FACTORY_AUDIT.md](ENGINE_AUTHORING_FACTORY_AUDIT.md).

**Phase numbering vs the milestone's work packages** (the milestone doc uses B1…E; the owner's briefs use Phase N):

| Owner phase | Work packages | Status |
|---|---|---|
| Phase 1 | B1 schema **except slot layouts** | Merged (PR #8) |
| Phase 2 | B2 check + part of B3 | Merged (PR #8) |
| Phase 3 | the derived-value part of B3 + C1 (`generate-tune`) | **Implemented; adversarial review → required fixes applied; at its gate** on `claude/gallant-gauss-jkbqvi`, not merged — [PHASE3_GATE_REPORT.md](milestones/engine-authoring-factory-1/PHASE3_GATE_REPORT.md) (§16: the fixes); brief in §7 |
| later | slot layouts (rest of B1), `validate --engines`, `list`/`schema` (rest of B3), C2 verification from data, C3 anti-hack extension (beyond the generator's audit scope, done in Phase 3), C4 docs/skills as the full pipeline, D pilot (LS3-type V8; B58 negative test), E gate | Not started; each needs owner authorization |

## 7. Phase 3 brief (issued by the owner 2026-09-29) and what was delivered
**Adversarial review and required fixes (2026-09-29):** before any merge the owner asked for an adversarial review; it
concluded READY AFTER SPECIFIC FIXES. The fixes, and only they, are applied (gate report §16): **P3-001** — a
`--manifest` or repository manifest that cannot be loaded stops generation (exit 1); none found → `manifest: none`, a
WARNING in the report, `"manifest": "none"` in the record; **P3-002** — a generated output is replaced only by the same
request (engine, fuel, tune id, policy tune); **P3-007** — the report lists every check-engine warning; plus the
documentation drift the review found. Every other review finding stays open (§10).

**Delivered (2026-09-29, branch `claude/gallant-gauss-jkbqvi`, not merged):** every acceptance criterion below is met
and evidenced in [PHASE3_GATE_REPORT.md](milestones/engine-authoring-factory-1/PHASE3_GATE_REPORT.md) (§13 of the
report). Decisions taken inside the brief: no default fuel exists in content, so `--fuel` is required; λ, rev limit, idle,
axes and boost come from a *policy tune* (the stock tune by default) and are reported NOT GENERATABLE, never invented; the
ECU's `displacement_cc` belief is set from the derived value at full precision; the generator iterates the existing
recipe until it is a fixed point (the documented "write, check, repeat" procedure, automated); output goes to an explicit
`--out` file plus a `.recipe.jsonc` record, and only an untouched generator output is ever overwritten. The brief as
issued:

"Derived Values + Deterministic Baseline Tune Generation." Pipeline: ENGINE DATA → DERIVED VALUES → CHECK-ENGINE →
BASELINE TUNE GENERATION → VALID STOCK BUILD. Orchestrate existing machinery; do not invent a new tuning system.

- **No physics change** of any kind; no K20/M54 parameter, tolerance or fingerprint change. A missing physical
  capability found on the way is stopped, classified and documented — never hacked.
- **Inspect first:** calibrators (`src/CarSim.Core/Ecu/*Calibrator.cs`), `TuneRegenerator`, `tune-manifest.json`,
  `TuneDocument`, `EngineGeometry`, `ContentLoader`, `EngineCheck`, fingerprint, matrix, skills. Do not duplicate.
- **Derived values:** only what is derivable from resolved inputs (displacement, swept/clearance/cylinder volume,
  firing interval, mean piston speed, runner tuned speed, theoretical airflow, …); deterministic, unit-safe,
  identity-free, computed on the **resolved assembly** (variants included). Make authoritative vs derived explicit —
  no duplicate sources of truth (e.g. an authored displacement disagreeing with bore × stroke × cylinders). Report
  authored / derived / validated / unavailable (extend `check-engine` or `inspect`). Note: `EngineGeometry` already
  derives displacement, CR, clearance volume, rod ratio, mean piston speed; the tune carries an ECU *belief* of
  displacement that `check-engine` compares (`tune_displacement_mismatch`).
- **`carsim generate-tune <engine> [--fuel <id>]`:** resolve engine → resolve stock parts → assemble → run
  `check-engine` → refuse on errors → run existing calibrators/recipes → validate → write the **existing** tune format
  → report. Fuel selection explicit, using the existing fuel system. Only automate what calibrators already support
  (cams, runner/lift switch, VE, spark; λ/boost only from a named policy or explicit input). Unsupported → reported as
  NOT MODELLED / NOT GENERATABLE.
- **Generated vs hand-authored:** distinguishable; never silently overwrite a hand-authored tune (explicit replace
  mode or a generated location). Manifest records engine, assembly, fuel, generator + version, recipe, inputs; no
  timestamp in deterministic identity.
- **Regeneration:** generated → regenerate → identical. Existing K20/M54 tunes must stay stable; if they change, STOP
  and investigate.
- **Clear failures:** missing data, unsupported feature (warning, left unchanged), invalid assembly, conflicting
  authoritative values.
- **Tests:** derived determinism, displacement derivation, geometry consistency, derived values change with a relevant
  part, generation, regeneration, determinism, validation, refusal on invalid engine, unsupported features, multiple
  fuels, engine and part variants, stock-assembly dependency, no engine-specific generator branches.
- **Mutants:** e.g. skip derived displacement, parent part instead of resolved variant, ignore injector flow, bypass
  `check-engine`, ignore fuel, change recipe, skip tune validation.
- **Synthetic V8 demonstration** (existing `syn_v8_ohv` or `syn_v8_dohc_vvt`): V8 assembly, bank resolution, derived
  values, tune generation, determinism. **No LS3 content.**
- **Do not build:** LS3 production content, bulk import, GUI editor, reference-data verification, dyno fitting, new
  physics, DI, Valvetronic, turbo or intake models, slot-layout generation (unless strictly required).
- **Docs:** ENGINE_AUTHORING_GUIDE.md, ARCHITECTURE.md, ROADMAP.md, the milestone doc, `car-sim-add-engine`
  (`car-sim-verify` if needed), and this file.
- **Gate:** `docs/milestones/engine-authoring-factory-1/PHASE3_GATE_REPORT.md` (clean Release build, tests, content
  validation, `check-engine` sweep, generation + regeneration, fingerprint, mutation harness, skills, CLI, CI, Godot
  smoke; exact branch/commit; limitations; next action). Then **STOP**; do not merge; do not start the LS3 pilot.
- Acceptance (owner's list): `generate-tune` exists; uses the resolved stock assembly; refuses invalid assemblies;
  derived values deterministic; generated tunes deterministic and regenerable and validated; fuel explicit; K20/M54
  tunes unchanged; a synthetic V8 generates a valid baseline tune; variants respected; no engine-specific branches;
  mutants catch generator failures; CI green.

## 8. Verification state at the snapshot
Re-run in this session (Ubuntu 24.04 container, .NET SDK 8.0.131 from the distro, 4 cores; Godot 4.7.2 .NET downloaded
from the official release — it now works in the container).

| Check | `main` `df9c987` (baseline, re-run) | Phase 3 branch |
|---|---|---|
| `dotnet build CarTuningSim.sln -c Release` | 0 warnings, 0 errors | 0 warnings, 0 errors |
| `dotnet test CarTuningSim.sln -c Release` | 766 passed, 0 failed, 0 skipped (1 m 51 s) | **840 passed, 0 failed, 0 skipped** (2 m 45 s) |
| `carsim validate` / `validate --mods content/test` | OK / OK | OK / OK |
| `carsim check-engine` on all 11 engines | all exit 0; K20 `--strict 1` exit 0; M54 PASS with 6 warnings | same, plus DERIVED; every stock tune's displacement belief validated |
| `carsim generate-tune` on all 11 engines (manifest fuels) | — | 11 of 11 generated, validated, 2 iterations each; re-run `--check 1`: UNCHANGED |
| `carsim fingerprint` (35 cases, 100 sections) | **IDENTICAL** | **IDENTICAL** (no re-baseline) |
| `carsim regenerate-tunes` (all 12 tunes) | **12 of 12 reproduced** | **12 of 12 reproduced** |
| Mutation harness | **43 of 43 caught** | **59 of 59 caught** (on `aec1a2e`) |
| Skills consistency | CI green | reinstalled; sources = installed copies; lock updated |
| Godot headless smoke | not run locally before | **local: 8 of 8 passed, 0 ERROR lines** (K20, M54, `syn_v8_swap`, `syn_v6_tt_swap`; dyno + drive) |
| GitHub CI | [run 84](https://github.com/Pyri4/car-tuning-sim/actions/runs/36556162699) on `df9c987`: success | [run 85](https://github.com/Pyri4/car-tuning-sim/actions/runs/36561845744) on `74e7480`: success (core + Godot); the documentation head: [run 86](https://github.com/Pyri4/car-tuning-sim/actions/runs/36566315580) on `aed05e8`: **success** (core: build, tests, CLI, skills, check-engine, the new generate-tune step — generated, then `--check 1` UNCHANGED — verification tools; Godot smoke) |

### 8b. After the adversarial review's required fixes (`a7df7ab`)
Re-run in the same container on the fix commit's tree (gate report §16): Release build 0 warnings, 0 errors; **846
passed**, 0 failed, 0 skipped; `validate --mods content/test` OK; `check-engine` 11 PASS (M54 with 6 warnings);
`generate-tune` 11 of 11, every tune file **byte-identical** to the Phase 3 sweep (records gain `"manifest":
"loaded"`), 11 of 11 `--check 1` UNCHANGED; fingerprint **IDENTICAL** (35 cases, 100 sections); `regenerate-tunes` **12
of 12**; mutation harness **66 of 66** (22 m 9 s, in the working tree); Godot **8 of 8**, 0 `ERROR` lines. GitHub CI:
[run 88](https://github.com/Pyri4/car-tuning-sim/actions/runs/36597586085) on `a7df7ab`: **success** (core — build,
tests, CLI, skills, check-engine, generate-tune with the new `--manifest` exit-1 step, verification tools — and Godot
smoke).

### 8a. Mutation harness
Full harness (`dotnet run --project tools/CarSim.MutationCheck -c Release`) re-run in this session: **43 of 43** on the
baseline `df9c987`; **59 of 59** on the Phase 3 code `aec1a2e` (31 before the factory, 4 from Phase 1, 8 from Phase 2, 16
from Phase 3), 20 m 33 s, run in a separate `git worktree`. A first Phase 3 run found one entry INVALID (the mutant did
not compile); the code was clarified and the entry updated, then it was caught (gate report §10).

## 9. Git and GitHub state at the snapshot
- `main` = `df9c987` (PR #9 merged 2026-09-29: this handoff document). CI green (run 84).
- Phase 3 branch: `claude/gallant-gauss-jkbqvi`, based on `df9c987`, pushed: `74e7480` (implementation), `aec1a2e`
  (fuel binding for a valid mutant; report text), `aed05e8` (docs, skills, CI step, gate report), `69582a9` (CI run 86
  recorded), `a7df7ab` (the adversarial review's required fixes: P3-001, P3-002, P3-007, docs; CI run 88 green) and a
  docs commit recording run 88. **Not merged; no PR opened** (none was asked for).
- Open PRs: none. Open issues: none (at the start of this session).
- Previous gate reports that say "nothing is merged / no PR open" describe the moment they were written.

## 10. Known limitations and open questions (do not conceal, do not "fix" by hacks)
Full lists: ROADMAP.md "Known issues" and "Technical debt"; ENGINE_AUTHORING_GUIDE.md §9; the Intake Gas Dynamics
[PHASE1_REVIEW.md](milestones/intake-gas-dynamics-2/PHASE1_REVIEW.md) §4–5.

**Reference-engine discrepancies (preserved, classified, not patched):**
- **M54 vs BMW M54B30** (170 kW / 300 N·m): **153.9 kW** peak (−9.5 %), mid-range peak 297.8 N·m at 3,100 rpm
  (reference 300 at 3,500), **no second torque hump**. DISA: closed stage 469.1 mm (derived by the frozen A-D1
  procedure), stage crossover 3,924 rpm, tune switch **3,900 rpm** (sourced band 3,750–4,100). Review: the top end
  needs ≈ +12.5 %; no single deferred mechanism closes it; the group/plenum mode (deferred) and better flow data
  (M54 flows are *estimated*, possibly ≈ 20 % low — only flow-bench data can settle it) are both needed. Class B → D.
  ROADMAP's "Current state" quotes 152 kW / 298 N·m near 3,000 rpm (different run conditions or an older figure) —
  **not reconciled here**.
- **K20 anchor margin:** air-per-cycle max error **2.87 %** against ±3 % (floor ≈ 2.69 % for the locked κ). The anchor
  limits κ to ≈ ≤ 0.53. Class B. Do not change κ or the tolerance to widen the margin.
- **K20 T35 turbo build:** −7.1 % peak power after Phase 1 (generic VE change at 7,250 rpm amplified ≈ ×4.6 by a
  spool-limited turbo, plus an off-recipe tune). The turbo model is not validated against a reference.
  **Discrepancy with the owner's handoff:** the handoff says T35 is treated as a diagnostic, not a hard fingerprint;
  in the repository `k20_t35_98` is still an ordinary (hard) fingerprint case — only the worn project-car lap is a
  knife-edge diagnostic (`FingerprintMatrix.KnifeEdgeSections`). Changing that is a verification change needing owner
  approval; it has not been made.
- **Owner decisions on PHASE1_REVIEW.md §5.1–5.3** (anchor margin, T35, M54 flow data) were not recorded in the
  repository before this document. The owner's 2026-09-29 handoff states them as: do not alter tolerance or κ to fit;
  treat T35 as diagnostic; no M54-specific constants. Recorded here as the owner's statement.

**Model limitations (selection):** level-setting constants fitted on the K20 (Otto realisation, FMEP, 112° cam
reference, v₀/ceiling); one runner mode per stage (no plenum/group resonance, harmonics, intake-closing coupling);
quasi-static air path; open-loop speed-density ECU (no closed-loop λ, warm-up enrichment, decel fuel cut); one MAP
sensor/fuel command/knock retard for all banks; shared elements split by cylinders without cross-feed; mean-value
knock; planar vehicle; game-compressed fatigue; idle MAP low (no accessory load); synthetic engines are plausible, not
realistic.

**Unsupported features (rejected or declared not-modelled):** superchargers, dry sump, direct injection, exhaust cam
phasing, continuous VVL (Valvetronic-type), twin-scroll turbines, cylinder deactivation, cam-in-block phaser moving
both lobes, inline cylinder groups (parallel twin-turbo inline engines).

**Authoring-factory gaps still open:** slot layouts (V/flat slot lists hand-written, ≈ 104–138 lines per multi-bank
engine); tune skeletons (policy tunes) hand-written, and a generated tune's recipe copied into the manifest by hand
(`generate-tune` exists since Phase 3); fuel-system capacity not checked (needs a sweep,
not a static check); per-engine C# still needed in the verification layer (fingerprint cases, reference tests, family
lists) until C2; `--strict` is all-or-nothing; tunes and reference figures have no provenance home; `Program.cs`
monolithic.

**Authoring cost (measured, audit §5/§13):** M54 (PR #4): 2,568 added lines, 392 content JSON (380 in the content
commit), 409 `src/` (the cam-timing capability), 1,227 tests. Estimated today for a real V8 without capability gaps:
≈ 1,000–1,150 lines, ≈ 450 of them C#/YAML. **Targets** (unmeasured until the pilot): ≈ 300–400 data lines, 0 C#,
≈ 5 commands; a variant ≈ 20–60 lines.

**Found in Phase 3 (not fixed; owner decisions):**
- **The checked-in tunes' ECU beliefs are hand-entered and differ slightly from the build and the fuel:** every matrix
  tune believes fuel density 0.745 (RON 91's) though calibrated on RON 95 or 98; the K20 and M54 displacement beliefs
  are rounded to the cc (1998 vs derived 1998.229; 2979 vs 2979.255). `generate-tune` sets beliefs from the build and the
  fuel, so a generated tune differs slightly from the checked-in one (VE cells ±0.001, a few spark cells ≤ 1°, 2–3 cam
  cells on flat optima; no switch speed moves). Correcting the checked-in tunes would move the fingerprint: owner
  decision (gate report §7, §12 #1). Class B.
- **No skeleton generation:** the policy tune (axes, λ, rev limit, idle, boost) stays hand-written; named λ policies or
  axis rules would be invented values without an owner decision. Class B → D.
- **Registering a generated tune as shipped content is manual** (move it into a content layer, copy its recipe into
  `tune-manifest.json`). Class D (the pilot's "0 hand pastes").
- Test-suite wall time +54 s (the synthetic V8 generated end to end). Class B, accepted and reported.

**Open findings of the Phase 3 adversarial review (not fixed; the required ones — P3-001, P3-002, P3-007 — are, gate
report §16):** P3-003 no check that a policy tune suits the build (axis coverage, boost target; a variant inherits its
parent's policy and hand-authored list); P3-004 no generation from engine data alone (the policy tune, placeholder
tables and beliefs included, is hand-written); P3-005 the tune does not record its calibration fuel and check-engine
has no fuel-belief rule (the 0.745 beliefs above); P3-006 a generated tune's displacement belief equals the geometry by
construction (no sourced nominal displacement to check the geometry against); P3-009 the generated name's suffix
compounds when a generated tune becomes the policy; P3-010 the skeleton's placeholder cam table and upper switch list
have no test (the cam path was shown to work); P3-011 the source audit does not scan `tools/CarSim.Cli` nor catch
`Banks`/`Layout` comparisons; P3-012 format defaults (`idle_rpm`, `knock_control_enabled`) reported as hand-authored;
P3-013 the calibrators treat the tune's rev limit, not the ECU's effective limit, as the boundary (VE `<=`, spark and
cams `<`; pre-existing); P3-014, P3-016 observations (derived values are a reporting view; end-to-end generation is
tested on one engine).

## 11. Documents that are stale or inconsistent (fix in the next phase that touches them)
- Fixed in the Phase 3 docs commit: ROADMAP.md and the milestone doc now record Phases 1 and 2 as passed and merged;
  ROADMAP's "Current state" is dated 2026-09-29; the guide no longer calls the milestone "proposed; not yet in the code".
- **PHASE1/PHASE2_GATE_REPORT.md** say "Nothing is merged and no PR is open" — true when written (left as history).
- ROADMAP M54 figures (152 kW) vs the Intake Gas Dynamics gate report (153.9 kW): see §10 — still **not reconciled**.
- The milestone doc's C1 "proof" (a matrix tune regenerated from nothing reproduces its checked-in tables) is not met as
  written; the doc now records what Phase 3 delivered instead and why (hand-entered beliefs, no skeleton generation).
- Fixed in the review-fix pass: the guide §6b and the gate report called `calibration_build_mismatch` a check against
  "the checked build" (it compares two constructions of the same resolved stock assembly); docs/VERIFICATION.md said
  the generator "never writes into `content/`" (it writes where `--out` points) and verified "a byte-exact round trip"
  (field by field); ARCHITECTURE.md and README.md described `generate-tune` without its policy tune.

## 12. Roadmap beyond Phase 3
Status vocabulary: CONFIRMED (done, merged) / PLANNED (in an authorized milestone definition) / POSSIBLE (listed in
ROADMAP "Next recommended tasks", not authorized) / NOT YET AUTHORIZED (explicitly excluded for now).

- **IMPLEMENTED, AT ITS GATE:** Phase 3 (derived values, `generate-tune`) on `claude/gallant-gauss-jkbqvi`, with the
  adversarial review's required fixes applied.
- **PLANNED** (Engine Authoring Factory 1.0, authorized phase by phase — each phase still needs the owner's go):
  slot layouts; `validate --engines`; `list`, `schema`; verification from data (profiles, light fingerprint cases,
  conformance suite, `compare-engine`, `verify-engine`); anti-hack extension (content-derived audit tokens, calibrators
  and generator in the audit); docs/skills; **pilot: LS3-type 6.2 L pushrod V8** (fictional marque, real reference,
  own bellhousing + gearbox); **B58 negative test** (identity only); stretch EJ20-type flat-4; milestone gate with the
  authoring-cost benchmark.
- **POSSIBLE** (ROADMAP order): Intake Gas Dynamics 2.0 Phase 2 (group-plenum mode, IVC coupling, second reference
  with published intake geometry — not defined or authorized); cylinder groups on inline engines; missing capabilities
  (supercharger, DI, exhaust cam phasing, per-bank fuel/knock, dry sump); swap interfaces beyond the bellhousing;
  multi-family calibration; chassis dyno; machining repairs; progression/economy; toe and set-up physics; tracks as
  content; audio; exported builds.
- **NOT YET AUTHORIZED:** more real engines beyond the pilot, bulk part catalogues, open world, multiplayer, GUI editor,
  UI ahead of capabilities, matching a single engine's dyno curve, B58 as supported content.

## 13. NEXT ACTION (exact)
**The owner reviews Phase 3 and the required fixes** ([PHASE3_GATE_REPORT.md](milestones/engine-authoring-factory-1/PHASE3_GATE_REPORT.md), §16 for the fixes).
Nothing is merged; the LS3 pilot and every later phase are not started. The next agent does **not** start new work
until the owner decides:
1. Accept Phase 3 → open a PR from `claude/gallant-gauss-jkbqvi` to `main` (only when the owner asks), merge after CI.
   Before relying on the branch: `git fetch origin`; confirm `main` is still `df9c987` (else re-verify the branch
   against the new `main`).
2. Owner decisions pending: (a) correct the checked-in tunes' hand-entered beliefs (a content change with a fingerprint
   re-baseline, its own commit) or leave them; (b) whether a later phase may define named λ policies and axis rules for
   skeleton generation; (c) the older open items in §10 (T35 as a hard fingerprint case; the M54 figure in ROADMAP);
   (d) the review's open findings (§10): whether hand-authored status and the calibration fuel belong in content
   (P3-001's root, P3-005), policy-suitability warnings (P3-003), and the generic rev-limit correction (P3-013).
3. Then the next phase, **only when authorized**, from the milestone's remaining work (§6): slot layouts,
   `validate --engines`, `list`/`schema`, verification from data (C2), the anti-hack extension (C3), then the pilot (D).
   Start with the fresh-agent procedure (§14) and re-run the Phase 3 gate checks (§8) first.

## 14. Fresh-agent bootstrap procedure
1. Read [AGENTS.md](../AGENTS.md), then this file.
2. Read the current milestone doc and its latest gate report (§6).
3. `git status`, `git branch -a`, `git log --oneline -15`, `git fetch origin`, compare the branch with `origin/main`.
4. Check GitHub: open PRs, whether prerequisite phases are merged, CI on `main`.
5. If anything differs from §9, trust the repository, and update this file.
6. Run only the checks the state calls for (§8 lists the commands); `car-sim-verify` packages them.
7. Continue from §13. Do not start unauthorized work; stop at gates.

Environment notes: this cloud container had no .NET SDK preinstalled; `apt-get install dotnet-sdk-8.0` worked
(dot.net install script was blocked by the network policy). Godot 4.7.2 .NET **can** be downloaded from the official
release URL in `.github/workflows/ci.yml` (it worked on 2026-09-29): unzip, `dotnet build game/CarTuningSim.csproj`,
`godot --headless --path game --import`, then the smoke tests. The full test suite takes ≈ 2 m 45 s (the synthetic V8
tune generation ≈ 2 min of it, in parallel), full tune regeneration ≈ 4.5 minutes, the fingerprint ≈ 35 s, one
`generate-tune` 15 s (K20) to ≈ 4 min (twin-turbo V6), the mutation harness (66 entries) ≈ 22 minutes. The mutation
harness mutates source files: run it in a separate `git worktree` when you keep editing.

## 15. Maintaining this document
Update it (and `project-state.json`) at every milestone or phase gate, merge, or major decision: snapshot date and
commit, §5, §6, §8, §9, §10, §11, §13. Keep it a summary with links; do not copy whole reports. Mark anything not
re-verified as UNVERIFIED.
