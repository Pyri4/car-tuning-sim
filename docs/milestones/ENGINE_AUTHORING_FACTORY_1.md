# Milestone: Engine Authoring Factory 1.0

**Status: AUTHORIZED phase by phase.**
- **Phase 1** (content schema + provenance foundation) passed its gate and is merged (PR #8)
  ([PHASE1_GATE_REPORT.md](engine-authoring-factory-1/PHASE1_GATE_REPORT.md)). It covers the B1 schema below except
  slot layouts.
- **Phase 2** (assembly-aware validation, `carsim check-engine`: the B2 check and part of B3) passed its gate and is
  merged (PR #8) ([PHASE2_GATE_REPORT.md](engine-authoring-factory-1/PHASE2_GATE_REPORT.md)).
- **Phase 3** (derived values — the B3 derived-value part — and deterministic baseline tune generation, C1) is
  implemented and at its gate ([PHASE3_GATE_REPORT.md](engine-authoring-factory-1/PHASE3_GATE_REPORT.md)).
- **Not started:** slot layouts (rest of B1), `validate --engines`, the rest of B3 (`list`, `schema`), data-driven
  verification (C2), the anti-hack extension (C3, beyond the generator's audit scope), docs and skills as the full
  pipeline (C4), the pilot (D) and the milestone gate (E).

It was defined by the strategy reset of 2026-09-28 from the audit
[docs/ENGINE_AUTHORING_FACTORY_AUDIT.md](../ENGINE_AUTHORING_FACTORY_AUDIT.md), which is its evidence base. Section
numbers such as §9.3 refer to that audit.

## Goal
Make adding a new engine (and a new variant of an existing one) a **data-entry and validation task** with no
simulation, test or CI code, while keeping the generic physics, the verification system and the honesty about where
every number comes from.

Success is measured, not claimed. The pilot engine is authored through the pipeline and its cost is recorded against
the "before" measured in the audit (§13). The ultimate metric is:

> How much unique code did the next real engine need? Target: **none**. Any line needed is a documented factory
> failure with a generic fix.

## Why this is next
- Long-term value over ease (AGENTS.md). Every future engine, variant and part pays the authoring cost.
- The engine architecture is proven generic (nine synthetic architectures, two real engines).
- The cost now sits in boilerplate, unchecked provenance, late validation and per-engine verification code (audit §6).
- It is also the prerequisite for the honest physics work that remains. Multi-family calibration of the K20-fitted
  level constants (ROADMAP, "Multi-family calibration") and Intake Gas Dynamics Phase 2 both need several real reference engines with
  machine-readable provenance, and this milestone produces them.

## Constraints (non-negotiable)
- **No physics change.** No change to model constants, Intake Gas Dynamics 2.0 parameters (κ, K, ζ, v₀, ceiling,
  A-D1), acceptance tolerances or the K20 anchor.
- **K20, M54 and the synthetic matrix stay bit-identical.** The regression fingerprint must not change except for new
  cases. Existing case ids, sections and values are unchanged, and the baseline file changes only by addition.
- **No engine identity in simulation, damage, ECU, calibration or tune-generation code.** The source audit is extended,
  never loosened.
- **One content format.** Authoring features are expanded by the loader into today's definitions. There is no second
  specification format and no checked-in generated copy beside its source.
- **Old content and saves keep loading.** Every new field is optional; mods need no change.
- **No GUI editor, no importers or scrapers, no bulk catalogues.** No more than the pilot(s) below.
- **IP:** facts with citations only. Nothing copied from games or mods (code, assets, data files, text). A third-party
  dataset is used only under an explicit licence recorded in `sources`.

## Phases
Each phase ends green (build, tests, both content validations, fingerprint) and in its own commits.

### B1 — Authoring schema (audit §9.1–9.3)
- `identity` on engine definitions (`manufacturer`, `family`, `variant`, `years`, `market`, `kind`: real / fictional /
  synthetic, `features`, `approximations`). Metadata only; never read by `src/`.
- `sources` document kind; optional `provenance` on parts and engines, with kinds:
  - `measured` / `published` / `secondary` / `converted` / `derived` / `estimated` / `fitted`;
  - `defaulted` computed by the tool;
  - `calibrated` stays in the tune manifest.
- Slot templates as content, and `slot_layout` (template + per-bank categories) as an alternative to explicit `slots`.
- `extends` for engines (with `abstract` family bases) and for parts; resolved after all layers.
- **Proof:**
  - the multi-bank matrix engines are re-authored with layouts, and an equality test shows each expanded definition
    equals its previous explicit one;
  - the fingerprint is identical;
  - a variant test shows an `extends` engine equals its hand-written copy.

### B2 — Authoring check (audit §9.4–9.5)
- `carsim check-engine <id>` and `carsim validate --engines [--strict]`, with issue codes:
  - stock build through `AssemblyValidator`;
  - every `requires` key provided by some loaded part;
  - stock tune ↔ stock build (displacement, injector flow, dead time, density, rev limit, load axis vs MAP and boost,
    tables for controlled hardware, switch speeds inside the axis, recipe present and ordered);
  - declared vs modelled features;
  - provenance coverage and consistency;
  - defaulted fields;
  - plausibility heuristics (warnings, documented thresholds);
  - the physics smoke (start, idle, WOT sweep, sustained full-power hold).
- Real base engines pass `--strict`, which CI runs.
- **Proof:** one negative test per rule, in the style of `AnInconsistentArchitectureIsRejectedWithItsReason`; a
  mutation entry for the tune ↔ build check.

### B3 — Derived values and discovery (audit §9.6, §9.10–9.11)
- `inspect` gains derived values (mean piston speed at the limit, firing interval and bank phasing, theoretical
  airflow, per-cylinder volumes), the defaulted fields, features and a provenance summary.
  - *Done in Phase 3* (`EngineDerivedValues`, `check-engine` DERIVED, `inspect`): every value but the banks' firing
    phasing, which is reported **unavailable** (crank-pin phasing is not in the schema); the mean firing interval is
    derived. Features and provenance were added to `inspect` in Phase 1; the defaulted fields are in `check-engine
    --verbose` (Phase 2).
- `carsim list` (the library) and `carsim schema <category>` (fields, units, required and defaults, from the spec
  types).
- New commands live in their own files; this starts splitting `Program.cs`.

### C1 — Tune generation (audit §9.7)
- `carsim generate-tune <engine> [--fuel] [--write 1]`:
  - a skeleton from the build: axes; beliefs from the parts and fuel; rev limit and idle from reference data or
    arguments;
  - λ from a named policy;
  - boost only from a sourced figure or an argument;
  - a recipe derived from the hardware, added to `tune-manifest.json`;
  - the existing driver runs it.
- **Proof:** regenerating a matrix tune from nothing but its engine reproduces its checked-in calibrated tables
  (hand-authored tables excepted, and listed); determinism across two runs.
- *Phase 3 (the owner's brief supersedes the lines above where they differ):*
  - `carsim generate-tune <engine> --fuel <id> [--policy <tune>] [--id] [--out <file.json>] [--check 1] [--strict 1]`;
    `--out` replaces `--write 1` (a new file plus a `.recipe.jsonc` record; never over a file the generator did not
    write), so no generated recipe is added to `tune-manifest.json` yet.
  - **No skeleton is generated:** axes, rev limit, idle and λ come from a *policy tune* (the stock tune by default),
    because no rule for them exists without inventing values (a named λ policy needs an owner decision). Beliefs are
    derived; the recipe is the manifest's or the manifest's rule applied to the hardware.
  - **Proof as delivered:** determinism across two runs (byte-identical tune and record), regeneration identical (the
    recipe on the generated tune reproduces it), the synthetic V8 end to end. Regenerating a matrix tune does **not**
    reproduce its checked-in tables exactly: the generator sets the ECU's beliefs from the build and the fuel, and the
    checked-in tunes carry hand-entered beliefs that differ slightly (see the gate report, "K20 and M54 stability").

### C2 — Verification from data (audit §9.8)
- Engine verification profiles: fuel, fingerprint profile, smoke scenarios, reference figures with provenance and
  bands stated in advance.
- Data-driven light fingerprint cases for library engines.
- A library conformance suite (the generic half of `M54ReferenceTests`) over every library engine.
- `ReferenceComparisonTests`; `carsim compare-engine`, `carsim verify-engine`, `carsim list-scenarios --smoke`, and CI
  reading that list.
- The matrix's hard-coded fingerprint list becomes derived, **with the same case ids**.
- The K20/M54 deep cases stay hand-written regression anchors.

### C3 — Anti-hack extension (audit §9.9)
- Source-audit tokens derived from content (ids, identity family and variant).
- The audit's scope extended to the calibrators and the tune generator.
- An identity-read audit.
- Renamed-id identity for every library engine.
- A mutation entry for each new invariant.

### C4 — Docs and skills
- ENGINE_AUTHORING_GUIDE.md §6 rewritten as the pipeline; PARTS_DATABASE.md for the new fields.
- `car-sim-add-engine` becomes the pipeline, with model tiers per step. `car-sim-verify` gains `check-engine --strict`
  and `verify-engine`.

### D — Pilot (audit §12)
- **GM LS3-type 6.2 L pushrod V8** (fictional marque in game, real reference in provenance), authored only through the
  pipeline:
  - the first real V engine and the first real multi-bank engine;
  - its own bellhousing pattern plus a gearbox part carrying it, so it swaps into the Isar C30 by interfaces;
  - a published reference with bands stated before the first run.
- **B58 negative test:** identity and declared features only. The check must report direct injection, continuous VVL,
  exhaust cam phasing and the twin-scroll turbine as not modelled, and refuse `--strict` without approximation notes.
- **Stretch** (only if D closes within the milestone): an EJ20-type turbo flat-4, to prove turbo authoring and boost
  targets.
- **Benchmark:** record the protocol of audit §13 (see "Authoring-cost benchmark" below).

### E — Gate
The `car-sim-project-gate` skill:
- re-verify from scratch: build, tests, both content validations, `validate --engines --strict`, fingerprint, the
  mutation harness (all mutants caught), Godot smoke tests including the pilot's scenario, save/load, the swap, dyno,
  CLI, `bench` for the pilot;
- report the measured authoring cost;
- classify the remaining debt;
- define, not start, the next milestone.

## Acceptance criteria
1. The pilot engine needed **0 lines** of simulation, test, tool or CI code. If it needed any, the reason is documented
   and the generic fix is in its own commit, and the criterion is reported as failed for that line.
2. The engine and its parts are data. Its tune is generated (`generate-tune`) and reproduced by `regenerate-tunes`.
3. `check-engine --strict` catches, in tests, a deliberately broken variant of each rule class: schema, geometry,
   interfaces, tune ↔ build, features, provenance.
4. Every physical field of the pilot has provenance. Defaulted fields are listed. Estimated and fitted values appear in
   the report. There are no `fitted` values without a target.
5. Derived values are deterministic (two runs, identical text).
6. The CLI can list, inspect, check, generate the tune for, compare and verify the pilot.
7. Godot loads the pilot's scenario (dyno and drive smoke tests pass, zero `ERROR` lines). The pilot swaps into a car
   by interfaces. Save → load → drive replays exactly.
8. The synthetic matrix stays green, all mutants are caught (existing and new), and the source audit is extended.
9. The K20, M54 and matrix fingerprints are **unchanged**; the baseline grows only by new cases.
10. There is no engine-name branch anywhere in `src/`, the calibrators or the tune generator.
11. ENGINE_AUTHORING_GUIDE.md documents the complete workflow, and `car-sim-add-engine` follows it with model tiers.
12. **Performance:**
    - the step cost of existing engines is unchanged (`bench`, same machine, ±5 %);
    - load time with the matrix is within +20 %;
    - the fingerprint's added CPU time per library engine is reported and ≤ the synthetic engines' per-case cost;
    - the test suite's wall time is reported.
13. The authoring cost is measured and reported against audit §13. Targets: data ≈ 300–400 lines, 0 code lines,
    ≈ 5 commands, 0 hand pastes. A miss is reported as measured, not re-estimated.

## Authoring-cost benchmark
Record for the pilot (and for every engine after it, in its PR):

| Measure | How |
|---|---|
| Files and lines by kind | `git diff --numstat` split into content data, generated (tune tables, manifest), code, tests, docs |
| Commands | The commands run, in order (the skill's checklist) |
| Validation iterations | `check-engine` runs until clean, and what each fixed |
| Hand edits after the first clean check | count and reason |
| Tune work | generated vs hand-authored tables, and any policy override |
| Code needed | every line, and why (target none) |
| Model tiers | the tier each step used, and every escalation (the skill's routing table) |
| Wall time | optional, informative only |

## Test matrix (structural)
- **Expansion equality:** every re-authored matrix engine's slot list; a variant vs its hand-written copy; a mod
  overriding a parent reaches its child.
- **Negative tests:** each `check-engine` rule on a broken variant (tests edit JSON like `ArchitectureInvariantTests`).
- **Tune generation:** a matrix tune regenerated from scratch; recipe derivation for phaser, VVL and 2- and 3-stage
  intakes; determinism.
- **Conformance:** every library engine (K20, M54, matrix, pilot) through the generic suite; renamed-id identity for
  each.
- **Fingerprint:** unchanged baseline for existing cases; new light cases for the pilot.
- **Anti-hack:** derived tokens forbid the pilot's ids in `src/`, the calibrators and the generator, shown by mutants
  that inject them.

## Out of scope
Physics changes of any kind; Intake Gas Dynamics Phase 2; multi-family calibration; new capabilities (DI, exhaust VVT,
superchargers, dry sump, cylinder groups); new part categories; swap interfaces beyond the bellhousing; GUI; asset
pipelines; more than the pilot(s).

## Model routing for this milestone
AGENTS.md, "Model routing":
- Phase B/C design decisions and the gate: strongest tier.
- Loader, check and CLI implementation against this definition: medium tier.
- Re-authoring matrix engines with layouts, provenance entry for the pilot, running the pipeline and preparing reports:
  small tier, escalating on any conflict, missing value or unexplained check failure.
