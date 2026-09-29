# Engine Authoring Factory 1.0 — Phase 3 gate report: derived values and deterministic baseline tune generation

**Status:** Phase 3 is implemented and stops here for the owner's review. **Not merged.** No PR has been opened (none
was asked for). The LS3 pilot and every later phase are **not started**.

| | |
|---|---|
| **Branch** | `claude/gallant-gauss-jkbqvi` |
| **Base** | `main` at `df9c987` (Merge PR #9; PRs #8 and #9 merged, no open PR at the start) — no divergence |
| **Implementation commits** | `74e7480` — code, tests, mutation entries; `aec1a2e` — the selected fuel bound as a non-null local after the refusals (so the fuel mutant compiles, §10) and a report-text fix (no doubled full stop after an approximation) |
| **Documentation commit** | on top of `aec1a2e`: this report, the docs, the skills, the CI step; the branch head (no code) |
| **Verified on** | `74e7480`: CLI, generation sweep, Godot smoke, CI run 85, a first mutation run. `aec1a2e`: build, the full test suite (its `Fingerprint*Tests` are the fingerprint), the full mutation harness; `carsim fingerprint` and `carsim regenerate-tunes` ran on the same code without the fuel binding (neither uses the generator). The head: CI — §9 |

Pipeline delivered (the owner's brief):

    ENGINE DATA → DERIVED VALUES → CHECK-ENGINE → BASELINE TUNE GENERATION → VALID STOCK BUILD

**Scope held:**
- no physics, simulation equation, constant, tolerance, engine parameter or Intake Gas Dynamics value changed;
- no content changed: no engine, part, tune or scenario file; no LS3 or B58 data;
- no fingerprint re-baseline (the baseline file is untouched and every case is identical);
- no checked-in tune rewritten (`regenerate-tunes`: 12 of 12 reproduced);
- no new calibration mathematics, no new tuning system, no GUI, no importer, no slot-layout generation.

## 1. Changed systems

| Area | Files | Change |
|---|---|---|
| Core, engines | `src/CarSim.Core/Engines/EngineDerivedValues.cs` (new) | The derived-values view (§2) |
| Core, engines | `src/CarSim.Core/Engines/EngineCheck.cs` | DERIVED section; the stock tune's displacement belief validated against the derived value; the tune ↔ build rules extracted as `EngineCheck.CheckTune` (same codes, same messages for the stock tune), shared with the generator |
| Verification tools | `tools/CarSim.Verification/Calibration/TuneGenerator.cs` (new) | The generator (§3–§7) |
| Verification tools | `tools/CarSim.Verification/Calibration/TuneRegenerator.cs` | `Run` split into `Settle` (its core: the recipe, the 2-cycle rule) and `BuildAssembly` (the calibrated build), both public; `TuneRecipe` gains an optional `Generated` record. Behaviour unchanged: `regenerate-tunes` 12/12 and the fingerprint case `k20_regen_stock_ve` identical |
| CLI | `tools/CarSim.Cli/GenerateTuneCommand.cs` (new), `Program.cs` | `carsim generate-tune` in its own file (the start of the `Program.cs` split B3 asks for); `inspect` lists the derived values |
| Tests | `EngineDerivedValuesTests`, `TuneGeneratorTests`, `TuneGeneratorV8Tests` (+ `…RegenerationTests`, `…DeterminismTests`), `GeneratorFixtures` (new); `EngineAgnosticTests` | 74 new tests; the source audit's scope now includes `tools/CarSim.Verification/Calibration` |
| Mutation harness | `tools/CarSim.MutationCheck/mutations.json` | 16 new entries (§10) |
| CI | `.github/workflows/ci.yml` | A `generate-tune kestrel_k20` step, run twice: the second with `--check 1` must print `UNCHANGED` |
| Docs and skills | ENGINE_AUTHORING_GUIDE.md (§6 step 6, §6a DERIVED, new §6b), ARCHITECTURE.md, ROADMAP.md, PARTS_DATABASE.md, README.md, docs/VERIFICATION.md, the milestone doc, PROJECT_STATE.md + JSON, `car-sim-add-engine`, `car-sim-verify` (reinstalled; lock updated) | Describe what is implemented |

Nothing in `game/` changed; the Godot project builds against the changed core (full-solution build) and its smoke tests
pass (§9).

## 2. Derived values (`EngineDerivedValues`)
**One source of truth per quantity.** The class is a view: every number is read from `EngineGeometry` or
`RunnerStageConfiguration`, the classes the simulation itself builds. No equation is restated for reporting (the only
arithmetic added is the reporting of two existing values together: displacement × rpm / 2 for the theoretical airflow,
and 720° / cylinders for the mean firing interval). A test requires the displacement to equal
`EngineGeometry.Displacement` bit for bit and each runner tuned speed to equal the simulation's own
`BankConfiguration.RunnerStages[s].TunedRpmAtReference`.

| Entry | Status | From |
|---|---|---|
| cylinders, bore, stroke (with part ids); the rev limit (with its tune) | authored | the installed block and crankshaft; a tune |
| runner diameter left to 0.40 × bore | defaulted | assumption A5 (per bank and stage) |
| displacement, swept volume per cylinder, rod ratio, bore/stroke | derived | `EngineGeometry` |
| clearance volume, cylinder volume, compression ratio — **per bank** | derived | `EngineGeometry.TryCreate(assembly, bank)` |
| mean piston speed, theoretical airflow (L/s) and air mass flow (kg/s, VE 1, the model's reference ambient) at the rev limit | derived | `EngineGeometry.MeanPistonSpeed`, displacement |
| mean firing interval | derived | 720° / cylinders (the model's four-stroke cycle); even or uneven firing is not claimed |
| firing phasing of the banks | **unavailable** | crank-pin phasing is not in the schema; the mean-value model has no per-cylinder events |
| runner tuned speed per bank and stage | derived | `RunnerStageConfiguration` |
| a tune's `displacement_cc` belief | **validated** / **mismatch** | compared with the derived displacement by the existing rule (1 %, `tune_displacement_mismatch`) |
| any value whose inputs are missing | **unavailable**, with the reason | e.g. "geometry incomplete: missing crankshaft", "needs a rev limit" |

**Authoritative vs derived.** The chain is *authored input → derived property → consistency check*. The tune's
`displacement_cc` is the ECU's belief (a player may fit a stroker crank without telling the ECU), so it is not a second
source of displacement: `check-engine` validates it against the derived value and `generate-tune` sets it from the
derived value. One pre-existing duplicate is reported rather than changed: the family's `cylinders` and the block's
`cylinders` are both authored; the validator's `cylinder_count` error already reconciles them (a block fits a family only
if its bore count matches), and the derived values say which one they use (the block's).

Surfaces: `check-engine` DERIVED (a summary; every entry with `--verbose 1`), `inspect` (every entry). Deterministic
(same assembly → same lines, tested on all 11 engines); computed on the **resolved** assembly (a variant and the parent
with the same parts swapped in by hand give identical values; a gasket on one bank changes only that bank's values).
It lives in `src/CarSim.Core/Engines`, so the engine-agnostic source audit covers it.

## 3. The generator (`carsim generate-tune`)
`carsim generate-tune <engine-id> --fuel <fuel-id> [--policy <tune-id>] [--id <tune-id>] [--out <file.json>]
[--check 1] [--strict 1] [--verbose 1] [--manifest <file>] [--mods <dir>]` — exit 0 generated (written or unchanged),
2 refused, 4 `--check` differs, 1 usage.

The pipeline (`TuneGenerator.Plan` then `Generate`):
1. Content is loaded as `check-engine` loads it (errors included); variants arrive resolved by the loader.
2. **`check-engine` runs; any error refuses** (`check_failed`, with the check's errors in the report); `--strict 1`
   refuses on warnings too (`check_warnings`).
3. **The fuel is explicit** (§5).
4. The **resolved stock assembly** is built (`EngineAssembly.CreateStock` on the resolved definition) and its derived
   values computed.
5. The **policy tune** (the engine's `stock_tune`, or `--policy`) supplies what no calibrator produces; without one:
   `no_policy_tune` (NOT GENERATABLE).
6. **Beliefs** are set from the build and the fuel (§4).
7. The **recipe** is chosen (§6), and the calibrators' build (`TuneRegenerator.BuildAssembly` from the recipe) must be
   the checked build slot for slot (`calibration_build_mismatch`).
8. The starting tune is **validated** before any calibrator runs (§8).
9. The recipe runs through `TuneRegenerator.Settle` — the regeneration driver's own core — until a pass reproduces every
   calibrated value (≤ 5 iterations, else `not_settled`).
10. The generated tune is validated again and written in the **existing tune format**; the text must load back to the
    same tune field by field (`round_trip_failed`).
11. A **deterministic report**: CHECK, ENGINE, DERIVED, FUEL, POLICY, RECIPE, FIELDS (every field's origin), NOT
    GENERATED, CALIBRATION, VALIDATION, AGAINST THE POLICY TUNE (what changed), RESULT. Elapsed time goes to stderr only.

It lives beside the driver it runs (`tools/CarSim.Verification/Calibration`); the source audit was extended to that
folder, and a mutant injecting an engine-id branch into the generator is caught (§10).

## 4. Generated vs hand-authored (field origins)

| Origin | Fields | Source |
|---|---|---|
| derived | `displacement_cc` | the derived displacement, full precision (no rounding rule invented) |
| derived | `injector_flow_cc_min`, `injector_dead_time_ms` | the installed injectors' spec (the resolved variant) |
| derived | `fuel_stoich_afr`, `fuel_density_kg_l` | the selected fuel |
| calibrated | the fields of the recipe's steps (`volumetric_efficiency`, `ignition_advance_deg`, `intake_cam_advance_deg`, switch speeds) | the dev calibrators via `TuneRegenerator.Settle` |
| hand-authored | `target_lambda`, `rev_limit_rpm`, `idle_rpm`, `knock_control_enabled`, the axes, `boost_target_kpa`, and any table the recipe lists as hand-authored (the K20's factory spark map) | the policy tune, unchanged |

Each hand-authored value is listed under NOT GENERATED with its reason (λ targets: calibration policy, no calibrator
and no named λ policy; rev limit and idle: reference data or a design decision, never guessed; axes: a layout choice;
a boost target: needs a sourced figure or an explicit decision). Hardware the ECU cannot drive is NOT GENERATABLE (no
cam map for an uncontrolled phaser, tested). A declared feature the simulator does not model is NOT MODELLED, with its
approximation (the M54's five; the B58-style fixture's direct injection and exhaust cam phasing).

**The record.** `--out x.json` writes the tune and `x.recipe.jsonc`: a one-recipe tune manifest (the format
`regenerate-tunes` reads) whose recipe carries a `generated` block — generator and version, policy tune, recipe source,
the resolved assembly slot by slot, every field's origin and basis, what was not generated and why, the iterations, and
the SHA-256 of the tune file. No timestamp, no path, no machine value. The record's extension keeps content loaders
(which read `*.json`) from loading it. The tune file itself starts with a comment naming the generator.

## 5. Fuel handling
- `--fuel` is required. Without it the CLI stops with a usage error listing the loaded fuels (exit 1); the library
  refuses with `fuel_required` (an empty or blank id too). An unknown id is refused (`unknown_fuel`, listing the fuels).
- **No default was adopted.** Inspected: engines have no fuel; scenarios name a fuel for a game situation; the tune
  manifest names each recipe's calibration fuel for *that* recipe. None is an authoritative per-engine default, so none
  is used. The report and the tune's description name the fuel.
- The fuel reaches the calibration (the recipe's fuel, which `TuneRegenerator` builds the engine with) and the beliefs
  (stoichiometric AFR, density). Tested: the M54 generated on RON 95 calibrates on RON 95 although the manifest's recipe
  says RON 98; E85 gives 9.8 and 0.781.

## 6. Recipe handling
- **The manifest's recipe is used unchanged** when it belongs to the policy tune and calibrates this engine's stock
  build (steps and parameters: the M54 keeps its 2.5° cam step; the K20 its single `ve` step with the factory spark map
  hand-authored).
- **Otherwise the recipe is derived from the hardware** by the rule every manifest recipe follows: the hardware schedule
  first — `cams` for an intake phaser the ECU drives, `runner_switch` for a switched intake, `lift_switch` for
  two-stage cams — then `ve`, and `spark` → `ve` when the spark map is calibrated; a table listed as hand-authored gets no
  step. **A test applies the rule to every stock-build recipe in the manifest (11 of 12; the K20 turbo base has a
  modified build) and gets the same steps.** A variant never borrows its parent's recipe (it calibrates the parent), only
  its hand-authored list.
- No new calibrator, no new step, no new parameter. The one orchestration rule added — iterate the recipe until a pass
  reproduces it — is the documented procedure after a physics change ("`--write 1`, then check; repeat",
  docs/VERIFICATION.md), automated.

## 7. Determinism, regeneration and the K20/M54 tunes
**Proof of determinism and regeneration:**
- `carsim generate-tune syn_v8_ohv --fuel gasoline_95 --mods content/test --out …` twice (the second with `--check 1`):
  `UNCHANGED` — tune and record byte-identical.
- `TuneGeneratorV8DeterminismTests`: a second independent generation gives the same tune text, record and report.
- `TuneGeneratorV8RegenerationTests`: the recipe run again (`TuneRegenerator.Settle`) on the generated tune loaded as
  content reproduces every value — what `regenerate-tunes` would report.
- Planning is deterministic for three engines (report, tune text, record); derived values for all 11.
- The record carries no time or path (tested).

**The K20 and M54 tunes stayed stable.** Nothing writes into `content/`; `regenerate-tunes` reproduces all 12
checked-in tunes (§9) and the fingerprint is identical.

**Generating the existing engines (all 11, each on its manifest fuel):** every one generates, settles in 2 iterations
and validates. Against its checked-in tune:

| Engine (fuel) | Recipe | Differences from the checked-in tune | Cause |
|---|---|---|---|
| kestrel_k20 (95) | ve | VE 12/180 cells ±0.001 | displacement belief 1998 → 1998.229 cc |
| isar_m54 (98) | cams (2.5°) → runner_switch → ve → spark → ve | VE 6/120 ±0.001; cam map, DISA switch, spark identical | 2979 → 2979.255 cc |
| syn_i4_sohc (95) | ve → spark → ve | VE 6/120 ±0.001; spark 3/120 ±0.5° | density 0.745 → 0.748; 1597.9 → 1597.944 cc |
| syn_i3_turbo_vvl (98) | lift_switch → ve → spark → ve | VE 6/195; spark 26/195 (max 1°); lift switch identical | density 0.745 → 0.750 |
| syn_i4_turbo (98) | cams → ve → spark → ve | VE 31/208; spark 14/208 (0.5°); cams 2/208 (2.5°) | density 0.745 → 0.750 |
| syn_i6_vis (95) | runner_switch → ve → spark → ve | VE 2/128; spark 4/128; runner switch identical | density 0.745 → 0.748 |
| syn_h4_turbo (98) | ve → spark → ve | VE 17/208; spark 7/208 | density 0.745 → 0.750 |
| syn_v6_na (95) | ve → spark → ve | VE 8/120; spark 5/120 (max 1°) | density 0.745 → 0.748 |
| syn_v6_tt (98) | cams → ve → spark → ve | VE 12/208; spark 7/208; cams 3/208 (3°) | density 0.745 → 0.750 |
| syn_v8_ohv (95) | ve → spark → ve | VE 5/104; spark 1/104 | density 0.745 → 0.748 |
| syn_v8_dohc_vvt (98) | cams → ve → spark → ve | VE 7/136; spark 5/136 | density 0.745 → 0.750 |

(VE differences are all ±0.001; displacement beliefs of the matrix differ from the derived value by < 0.005 %.)

**Investigation (no change made):** the only input the generator changes is the ECU's beliefs; everything else is the
checked-in tune and its recipe. With the checked-in beliefs the same code path (`Settle`) reproduces every checked-in
table exactly — that is `regenerate-tunes`, 12 of 12 — so the differences are caused by the beliefs alone, as expected
and in the direction expected: the checked-in beliefs were typed by hand (the K20's and M54's displacement rounded to
the cc; every matrix tune's fuel density is RON 91's 0.745 although each is calibrated on RON 95 or 98 — a 0.4–0.7 %
fuelling error). The generator does what the brief requires (beliefs from the build and the fuel); the checked-in tunes
are **not** changed (that would move the fingerprint and needs the owner's decision). Classified in §12.

## 8. Validation of the generated tune; refusal of invalid engines
Before calibration (on the starting tune) and after it, with the same function (`TuneGenerator.Validate`):
- the tune format's rules and ranges (`TuneDocument.Validate`: what the ECU can be set to) → `tune_invalid`;
- beliefs exactly the build's and the fuel's → `belief_not_derived` (tested for each of the five beliefs);
- a rev limit the ECU honours → `calibration_rev_limit_above_ecu`: the ECU cuts fuel at min(tune, ECU ceiling) while the
  calibrators treat every column up to the tune's limit as running, so a higher policy limit would calibrate columns on
  the limiter (tested with an 8,500 rpm policy on the 8,000 rpm ECU). `check-engine` keeps its warning
  `tune_rev_limit_above_ecu` for a hand-authored tune; this is a precondition of *calibration*, not a new severity for
  existing tunes;
- the tune-dependent assembly rules (valve float, crank and flywheel ratings, MAP sensor and springs) and
  `check-engine`'s tune ↔ build rules (`EngineCheck.CheckTune`) with their own severities (warnings reported, not
  fatal);
- after writing: the file loads back to the same tune, field by field (every existing tune round-trips through the
  writer — tested on all 12).
The generated V8 tune also passes `check-engine` as the engine's stock tune (belief validated, no tune warning) and holds
its λ targets within 2 % at full load at 2,000, 4,000 and 5,500 rpm.

**Refused (tested, each with its code):** an unknown engine and an orphan variant (`check_failed` ← `unknown_engine`,
`content_error`), an abstract family base (`abstract_engine`), a stock part that does not resolve (`content_error`), an
assembly `check-engine` rejects (97 mm pistons in 94 mm bores: `piston_bore_mismatch`), warnings under `--strict`, no
fuel, an unknown fuel, no policy tune, an unknown policy tune, a policy the calibrators cannot run, a belief not derived.

## 9. Verification (numbers seen, not remembered)
Container: Ubuntu 24.04, .NET SDK 8.0.131 (distro), 4 cores; Godot 4.7.2.stable.mono.official downloaded from the
official release (the URL CI uses).

| Check | Baseline (`df9c987`) | Phase 3 |
|---|---|---|
| `dotnet build CarTuningSim.sln -c Release` (includes the Godot C# project) | 0 warnings, 0 errors | 0 warnings, 0 errors |
| `dotnet test CarTuningSim.sln -c Release` | 766 passed, 0 failed, 0 skipped (1 m 51 s) | **840 passed, 0 failed, 0 skipped** (2 m 45 s; +74 tests), on `74e7480` and again on `aec1a2e` |
| `carsim validate` / `validate --mods content/test` | OK (105 parts, 2 engines, 5 fuels, 3 tunes) / OK (247, 11, 5, 12) | same |
| `carsim check-engine` × 11 engines | all exit 0; K20 `--strict 1` → 0; M54 PASS with 6 warnings | same (with the DERIVED section; every stock tune's belief **validated**); M54 `--strict 1` → 3; unknown → 2 |
| `carsim generate-tune` × 11 engines (manifest fuels) | — | **11 of 11 generated**, 2 iterations each, validation ok (12 m 34 s, 2 in parallel) |
| `generate-tune syn_v8_ohv … --check 1` after writing | — | **UNCHANGED** (byte-identical) |
| `carsim fingerprint` (35 cases, 100 sections) | IDENTICAL | **IDENTICAL** (43 s); the baseline file untouched |
| `carsim regenerate-tunes` (12 tunes) | 12 of 12 reproduced (263 s) | **12 of 12 reproduced** (326 s, sharing the CPU); the same 2-cycle counts as the baseline; no file written |
| Mutation harness | 43 of 43 caught | **59 of 59 caught** on `aec1a2e` (20 m 33 s); a first run on `74e7480`: 58 caught, 1 INVALID, fixed (§10) |
| Skills | CI step green | reinstalled with `npx skills add ./tools/agent-skills -a claude-code -a codex -y`; sources and installed copies identical (`diff -r`); lock hashes updated |
| Godot headless smoke, **local** | not run before (no Godot) | **8 of 8 passed, 0 `ERROR` lines**: K20 dyno (134.1 hp) and drive; M54 dyno (203.2 hp) and drive; `syn_v8_swap` dyno (263.0 hp) and drive; `syn_v6_tt_swap` dyno (441.5 hp) and drive |
| GitHub CI | run 84 on `main`: success | [run 85](https://github.com/Pyri4/car-tuning-sim/actions/runs/36561845744) on `74e7480`: **success** (core: build, tests, CLI, skills, check-engine, verification tools; Godot smoke); the mutation job is manual-only (skipped). The documentation head: pending when this commit was written (recorded by the next commit) |

## 10. Mutation results
16 new entries, one per new invariant (the brief's seven are the first seven rows):

| Mutant | Guards | Result |
|---|---|---|
| `generate-skips-derived-displacement` | generate-tune sets the ECU's displacement belief from the build's derived displacement, not from the policy tune. | CAUGHT (15 failed) |
| `generate-parent-engine-instead-of-variant` | generate-tune builds the resolved variant's stock assembly, never its parent's. | CAUGHT (3 failed) |
| `generate-parent-part-instead-of-variant` | generate-tune reads the installed (resolved) part variant's spec, never its parent part's. | CAUGHT (1 failed) |
| `generate-ignores-injector-flow` | generate-tune sets the ECU's injector scaling from the installed injectors, not from the policy tune. | CAUGHT (1 failed) |
| `generate-bypasses-check-engine` | generate-tune refuses an engine that check-engine rejects. | CAUGHT (4 failed) |
| `generate-calibrates-on-another-fuel` | generate-tune calibrates on the fuel it is given, never the manifest's or a default. | CAUGHT (1 failed) |
| `generate-ignores-fuel-beliefs` | generate-tune sets the ECU's fuel beliefs from the selected fuel. | CAUGHT (14 failed) |
| `generate-recipe-ignores-manifest` | generate-tune runs the manifest's recipe for the engine's stock tune unchanged (steps and parameters). | CAUGHT (1 failed) |
| `generate-recipe-drops-cam-step` | The recipe derived from the hardware calibrates the cam map of a phaser the ECU drives (the rule every manifest recipe follows). | CAUGHT (3 failed) |
| `generate-skips-tune-validation` | generate-tune refuses a tune that fails validation (before and after calibration). | CAUGHT (1 failed) |
| `generate-single-pass` | generate-tune runs the recipe until a pass reproduces it: the generated tune is a fixed point (the regeneration guarantee). | CAUGHT (4 failed) |
| `generate-overwrites-hand-authored` | generate-tune never writes over a file it did not write. | CAUGHT (3 failed) |
| `generate-overwrites-hand-edits` | generate-tune never rewrites a generated tune edited by hand since (its digest no longer matches). | CAUGHT (1 failed) |
| `generator-identity-branch` | No engine or part identity in the tune generator (the source audit covers tools/CarSim.Verification/Calibration). | CAUGHT (1 failed) |
| `derived-displacement-second-source` | The derived displacement is the geometry's own value (one source of truth), not a rounded copy. | CAUGHT (24 failed) |
| `derived-first-bank-for-all` | Each bank's derived chamber values come from that bank's head and gasket. | CAUGHT (1 failed) |

**Full harness on `aec1a2e`: 59 of 59 caught** (the 43 earlier entries and the 16 above; 20 m 33 s). Every guarding test
passed on the unmutated sources first, and every file was restored.

A first run on `74e7480` caught 58 and reported `generate-calibrates-on-another-fuel` **INVALID**: the mutant did not
compile, because it made the code's only null-forgiving `fuel!` conditional and nullable analysis (warnings are errors)
then rejected a later use. An invalid mutant proves nothing, so it was not counted as caught. `aec1a2e` binds the selected
fuel once, as a non-null local after the refusals (clearer code, no behaviour change), and the entry's `find` text
follows it; the mutant then compiles and is caught.

## 11. Tests added (74)
- `EngineDerivedValuesTests` (39 cases): displacement is the geometry's and π/4·bore²·stroke×cylinders of the installed
  parts (11 engines); volumes, ratios, values at the limit consistent (11); runner tuned speeds are the simulation's own
  stages (11); determinism; a variant equals its parent with the same parts swapped in by hand; a part on one bank
  changes only that bank; unavailable values with reasons; the stock tune's belief validated for every engine and a
  mismatch for the stroker variant; the report's summary vs `--verbose`.
- `TuneGeneratorTests` (29 cases, no calibrator run, ≈ 1 s): every refusal of §8; the resolved stock assembly and the
  calibrators' build; beliefs from the build and the fuel; the selected fuel reaches calibration; an engine variant on
  part variants; each belief checked; the manifest's recipe unchanged; the hardware rule reproduces every manifest
  recipe; schedule before maps; uncontrolled hardware NOT GENERATABLE; unmodelled features NOT MODELLED; planning
  deterministic; every tune round-trips through the writer; the record; the overwrite policy (write, unchanged, check,
  regenerate an untouched output, refuse a hand-edited one; refuse a hand-authored tune file and a content file holding a
  tune, on temporary copies — the real files are never at risk, even under a mutant).
- `TuneGeneratorV8Tests` (4), `TuneGeneratorV8RegenerationTests` (1), `TuneGeneratorV8DeterminismTests` (1): the synthetic
  pushrod V8 end to end (§7–§8). Cost: ≈ 3.5 CPU-minutes, ≈ 2 minutes wall in three parallel classes.

## 12. Known limitations, findings not fixed, and classification
Classes: **A** generic, **B** documented limitation, **C** must fix before the next milestone, **D** future.

| # | Item | Class | Why not fixed here / what fixes it |
|---|---|---|---|
| 1 | The checked-in tunes' beliefs are hand-entered and differ from the build and fuel (matrix density 0.745 on RON 95/98 tunes; K20/M54 displacement rounded) — so a generated tune differs slightly from its checked-in one (§7) | B (content); owner decision | Correcting them rewrites 11 tunes and moves the fingerprint: a deliberate content change for the owner to authorize, in its own commit, re-baselined with the diff. Not done |
| 2 | No skeleton generation: a policy tune (axes, λ, rev limit, idle, boost) is hand-written | B → D | Generating axes or λ would invent values; needs the owner's decision on named λ policies and axis rules (audit §9.7) |
| 3 | Registering a generated tune as an engine's shipped stock tune is manual (move the file into a content layer; copy the recipe into `tune-manifest.json`) | D (the pilot's "0 hand pastes") | A later phase; `regenerate-tunes` reads repository-relative recipes, the record is relative to itself |
| 4 | Only the stock build is generated | B | A tune for a modified build is a manifest recipe with `build`, run by `regenerate-tunes` |
| 5 | Test-suite wall time +54 s (1 m 51 s → 2 m 45 s) from the V8 end-to-end generation | B (accepted cost, reported) | The three V8 classes run in parallel; a lighter recipe would not prove the real pipeline |
| 6 | The engine's `cylinders` and the block's `cylinders` are both authored (reconciled by the validator's `cylinder_count` error) | B | Physically meaningful as a fit rule; the derived values state which one they use |
| 7 | Firing phasing of the banks unavailable | B | Crank-pin phasing is not in the schema (not needed by the mean-value model) |
| 8 | Generator determinism is proven on this platform (linux-x64, .NET 8); across libm implementations last bits may differ, as for the fingerprint | B | docs/VERIFICATION.md, "Platform" |
| 9 | M54 figures: ROADMAP says 152 kW, the Intake Gas Dynamics gate report 153.9 kW (PROJECT_STATE §10) | documentation, not reconciled | Outside this phase; unchanged |
| 10 | `k20_t35_98` is a hard fingerprint case while the owner's handoff calls T35 diagnostic (PROJECT_STATE §10) | open owner decision | Verification change; not touched |

No class C item was found: nothing blocks the next phase. No missing physical capability was met: generation needed no
physics.

## 13. Acceptance criteria (the owner's list)

| Criterion | Result |
|---|---|
| `carsim generate-tune` exists | yes (§3) |
| uses the resolved stock assembly | yes; the calibrators' build equals it slot for slot |
| runs check-engine before generation; invalid stock assemblies refused | yes (§8) |
| fuel selection explicit | yes (§5) |
| existing generic calibrators reused | yes: `TuneRegenerator.Settle` and the manifest's recipes |
| generated tunes deterministic; regenerated tunes identical | yes (§7) |
| generated tunes validate | yes (§8) |
| hand-authored tunes cannot be silently overwritten | yes (§4, tests, mutants) |
| variants respected | yes (engine and part variants; mutants) |
| derived values deterministic, one source of truth, reflect resolved content | yes (§2) |
| synthetic V8 baseline tune generates | yes (§7, §8) |
| no engine-specific branches | yes: source audit extended to the generator; mutant caught |
| mutation tests cover the new failure modes | yes: 16 new mutants, all caught; 59 of 59 in total |
| existing tests pass; fingerprint unchanged; tune regeneration unchanged | yes (840/840) / yes (IDENTICAL) / yes (12 of 12) |
| content validation passes | yes |
| CI passes | run 85 on `74e7480`: success; the head: pending when this commit was written (recorded by the next commit) |
| documentation reflects the implementation | yes (§1) |
| this report exists | yes |

## 14. Model routing (as practised)
One model, no subagents. **T3:** the design (derived values as a view; beliefs derived, policy authored; the recipe
rule; iterate to a fixed point; the record and the overwrite policy), the validation rule for a rev limit above the
ECU, the investigation of the differences from the checked-in tunes, the classifications and this report. **T1/T2
work done inline** (cheaper than delegating): fixtures, repetitive tests, mutation entries, CLI plumbing, doc edits, the
sweeps.

## 15. Next action
**STOP for the owner's review of Phase 3.** Nothing is merged; the LS3 pilot is not started.
1. Owner reviews this report and the branch; if accepted, a PR from `claude/gallant-gauss-jkbqvi` to `main` (not opened
   here), merged after CI.
2. Owner decisions recorded in PROJECT_STATE §13: (a) whether to correct the checked-in tunes' hand-entered beliefs
   (§12 #1; a content change with a fingerprint re-baseline); (b) whether a later phase may add named λ policies and
   axis rules for skeleton generation (§12 #2).
3. The next phase (not started, needs authorization) is chosen from the milestone's remaining work: slot layouts,
   `validate --engines`, `list`/`schema`, verification from data (C2), the anti-hack extension (C3), then the pilot (D).
