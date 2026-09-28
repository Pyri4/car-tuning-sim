# Engine Authoring Factory 1.0 — Phase 2 gate report: assembly-aware validation (`carsim check-engine`)

**Status:** Phase 2 is implemented and stops here for the owner's review. Phase 3 (tune generation), data-driven
verification and the LS3 pilot are **not started**. Nothing is merged and no PR is open.

**Branch:** `claude/busy-curie-dx4vz2`. It sits directly on the latest `main` (`af0b289`, PR #7)
with the audit commits, Phase 1 and this phase on top, and has no divergence from `main`.

**Scope held:**
- no simulation, damage, ECU or gameplay change;
- no K20/M54 parameter, tune, tolerance or Intake Gas Dynamics change;
- no fingerprint re-baseline;
- no GUI, no new content format;
- no shipped content change.

**Pre-check (asked for before Phase 2):** the branch is based on the latest `main` (checked with `git fetch` and
`git log main..HEAD` / `HEAD..main`). In the Phase 1 report, the totals (45 new cases, 728 = 683 + 45) matched the
suite. One sub-count was wrong: "4 impossible variants" is 5 theory cases. The wording was corrected; the
implementation was not touched.

## 1. Implementation summary
- **`EngineCheck` (`src/CarSim.Core/Engines/EngineCheck.cs`, new)**
  - Takes a `ContentLoadResult` (errors included) and an engine id, and returns an `EngineCheckReport`: findings
    (section, severity, code, message), facts, feature statuses, provenance coverage and details.
  - It builds the stock assembly slot by slot, reporting every missing stock part and failed install instead of
    throwing as `EngineAssembly.CreateStock` does.
  - **It reuses the rules that exist** rather than restating them: `EngineTopology.CheckFamily`,
    `AssemblyValidator.Validate` (with the stock tune's rev limit and boost as context), `EngineGeometry`,
    `EngineCapabilities`, `FeatureReport`, `VehicleCompatibility`, and the Phase 1 provenance records. Each validator
    code is mapped to a report section, and a test fails if a validator code has no section.
  - **New rules, all authoring-level:**
    - identity (abstract base, real engine without family or reference, no identity);
    - load errors of the engine, its family chain, stock parts and stock tune;
    - stock-part coverage and install order;
    - feature statuses turned into severities;
    - provenance coverage (by type, unrecorded, defaulted, fitted);
    - stock tune ↔ stock build agreement (displacement, injector flow and dead time, the ECU's rev ceiling, cam table
      and switch speeds for controllable hardware, switch speeds within the rpm axis, the load axis against the boost
      target);
    - plausibility heuristics (bore/stroke, rod/stroke, mean piston speed: warnings);
    - which loaded cars take the engine by interfaces.
  - **Placement:** in the core, so the engine-agnostic source audit covers it. It is not in `Simulation/`, `Ecu/`,
    `Damage/` and the other runtime folders that the metadata audit forbids from reading identity or provenance.
- **Loading is unchanged:** an isolated part still loads on its own. Whole-engine rules live only in the check.
- **CLI:** `carsim check-engine <engine-id> [--verbose 1] [--strict 1] [--content/--mods]`.
- **CI:** the core job runs `check-engine kestrel_k20 --strict 1`, `check-engine isar_m54` and
  `check-engine syn_v6_tt --mods content/test`.

## 2. CLI examples
Exit codes:

| Code | Meaning |
|---|---|
| 0 | No errors (warnings allowed) |
| 2 | Errors: not an authorable engine |
| 3 | `--strict 1` with warnings |
| 1 | Usage error (no engine id) |

These are verified by hand: `kestrel_k20 --strict 1` → 0; `isar_m54` → 0; `isar_m54 --strict 1` → 3; `nope` → 2;
no id → 1.

```
$ carsim check-engine isar_m54
check-engine isar_m54 — Isar M54 3.0L DOHC I6

IDENTITY  ok
  real, Isar (BMW) M54B30; family M54
ARCHITECTURE  ok (1 note(s); --verbose)
  inline-6, DOHC, 4 valves/cyl, intake cam phasing, variable intake runner, naturally aspirated
  air paths: 1 intake, 1 exhaust; 0 turbocharger(s)
PARTS  ok
  stock build: 21 of 21 slots filled, 21 distinct parts
TOPOLOGY  ok
  21 slots (21 required), 0 bank-scoped
  banks, slot scopes and assembly order are consistent
INTERFACES  ok
  fits (stock chassis): isar_c30
GEOMETRY  ok (1 note(s); --verbose)
  derived: 2979.3 cc, bore × stroke 84.0 × 89.6 mm, rod ratio 1.51
FEATURES  warning
  intake_cam_phasing: supported
  variable_intake: supported
  [warning] feature_not_modelled: dual_mass_flywheel (...) is not modelled; stands in: One flywheel inertia, no torsional isolation.
  [warning] feature_not_modelled: exhaust_cam_phasing (...) is not modelled; stands in: Exhaust cam held at its park centreline ...
  (+ map_controlled_thermostat, mass_air_flow_metering, returnless_fuel_system)
PROVENANCE  warning
  53 of 98 authored values recorded: published 5, measured 0, secondary 18, converted 4, derived 2, estimated 24, fitted 0; unrecorded 45; defaulted 5
  architecture: cylinders published, layout published
  [warning] provenance_incomplete: 45 authored value(s) of the stock parts have no provenance record (--verbose lists them).
COMPLETENESS  ok (1 note(s); --verbose)
  stock tune: m54.stock

RESULT  PASS with 6 warning(s)
```
(blank lines between sections and two long messages shortened here; the real output is complete). `--verbose 1` adds
the notes (firing order not recorded, compression ratio, default runner diameter) and a DETAILS list: part by part,
the unrecorded fields and the defaulted fields.

## 3. Validation categories

| Section | Rules (codes) | Severity |
|---|---|---|
| CONTENT | `content_error`: load errors of the engine, its family chain, stock parts, stock tune (invalid provenance, unknown parent, cycles, bad identities). Errors in unrelated content: a note | error / info |
| IDENTITY | `unknown_engine`, `abstract_engine` (names the variants), `identity_missing`, `identity_family_missing`, `identity_reference_missing` (real engines), `identity_variant_missing` | error / warning / info |
| ARCHITECTURE | facts (capabilities, banks, air paths, turbos, firing order); `firing_order_missing` | info |
| PARTS | `stock_part_missing`, `stock_install_failed`, and the validator's `missing_part`, `missing_category`, `cylinder_count`, `set_count` | error |
| TOPOLOGY | `unsupported_topology` (duplicate component for a bank, slot on a nonexistent bank, layout ↔ banks, partition), `slot_graph_cycle` | error |
| INTERFACES | `missing_interface`, `valvetrain_mismatch`, `*_uncontrolled` (ECU outputs); vehicle fit fact or `no_vehicle_fits` | error / warning |
| GEOMETRY | journal, pin, bore, gasket fits; clearances; compression (validator); `geometry_incomplete`; `implausible_bore_stroke`, `implausible_rod_ratio`, `implausible_piston_speed` | error / warning |
| LIMITS | `valve_float`, `crank_overspeed`, `flywheel_overspeed`, MAP and boost rules | warning |
| FEATURES | supported (fact), `feature_not_modelled` (warning), `feature_missing_data` (error), `feature_undeclared` (warning for real engines, note otherwise) | error / warning / info |
| PROVENANCE | coverage fact; `provenance_incomplete` (warning for real engines, note otherwise); `provenance_fitted` | warning / info |
| COMPLETENESS | `default_intake_geometry`; `no_stock_tune` (note: tune generation comes after the check); `tune_displacement_mismatch`, `tune_injector_flow_mismatch`, `tune_dead_time_mismatch`, `tune_rev_limit_above_ecu`, `tune_no_cam_table`, `tune_no_switch_speed`, `tune_switch_outside_axis`, `tune_load_axis_below_boost` | warning / info |

Severity policy:
- **Errors** mean the parts cannot form this engine, or the content contradicts itself (a declared modelled feature
  without its hardware).
- **Warnings** mean the engine is buildable but the author must know something: an unmodelled feature, missing
  sources, an implausible value, a tune that disagrees with the build.
- Estimated data is never an error.

## 4. Tests
`tests/CarSim.Core.Tests/Content/EngineCheckTests.cs`, 38 cases. Fixtures are test layers over the base content and
the synthetic matrix; nothing ships. Each negative case asserts the code **and** section, not just failure.

| Brief item | Test |
|---|---|
| valid stock engine | every shipped and synthetic engine passes (11 cases); the K20 passes even `--strict` |
| valid engine with estimated data accepted | the M54: PASS, estimated values counted, `--strict` → 3 |
| missing required part | `stock_part_missing` (Parts) + `missing_part` |
| wrong part interface | K20 head on the six: `missing_interface` (Interfaces), naming `k20.deck` |
| duplicate part | a second head slot on one bank: `unsupported_topology` (Topology) |
| invalid bank assignment | a slot naming bank 'left' on a one-bank engine (Topology) |
| invalid topology | layout `v` without banks; an `install_after` cycle (reported, not thrown) |
| invalid geometry | 86 mm pistons in 84 mm bores `piston_bore_mismatch`; tall pistons `piston_head_contact`; a short stroke is only `implausible_bore_stroke` (warning) |
| incompatible part variant | the 86 mm piston variant above |
| missing feature declaration | a real engine with no features: `feature_undeclared` warnings for its phaser and variable intake |
| unsupported feature declaration | the B58 fixture; without an approximation → blocked |
| modelled feature without data | a turbocharger declared on the NA six → `feature_missing_data` (error) |
| missing provenance | the M54's `provenance_incomplete` warning with its count and details; the K20's is a note |
| invalid provenance | a stock-part variant with type "guessed" → `content_error` naming the part |
| unknown parent / inheritance failure | `content_error` "unknown engine" + `unknown_engine`; a two-engine cycle |
| abstract engine rejected | `abstract_engine`, naming its variant; the variant passes |
| identity gaps | real engine without family or reference; an engine without identity |
| stock tune vs build | the M54 with the K20's tune: displacement and injector mismatches; an untuned engine passes with a note |
| LS3 readiness | §8 |
| deterministic output | the same report text twice (M54, B58 fixture) |
| exit codes | K20 (0, 0), M54 (0, 3), unknown (2, 2), verdict texts |
| rule coverage | every `AssemblyValidator` code has a report section |

## 5. Mutation results
8 new entries, each one a rule this phase relies on; each caught.

| Mutant | Tests failed |
|---|---|
| `check-required-stock-part-ignored` | 1 |
| `check-interfaces-bypassed` | 1 |
| `check-provenance-ignored` | 3 |
| `check-abstract-accepted` | 1 |
| `check-missing-feature-data-not-blocking` | 1 |
| `check-tune-displacement-ignored` | 1 |
| `unknown-feature-accepted` | 1 |
| `slot-unknown-bank-accepted` | 1 |

Full harness on the final tree: **43 of 43 caught** (31 from before the factory, 4 from Phase 1, 8 from Phase 2).

## 6. Regression result (final tree)

| Check | Result |
|---|---|
| Clean Release build (`--no-incremental`, includes the Godot C# project) | 0 warnings, 0 errors |
| `dotnet test CarTuningSim.sln -c Release` | **766 passed, 0 failed, 0 skipped** (728 before Phase 2; +38) |
| Regression fingerprint (`carsim fingerprint`, 35 cases, 100 sections) | **IDENTICAL**, also re-run after the mutation harness restored the sources; no re-baseline |
| `carsim validate` / `--mods content/test` | OK / OK |
| CI's CLI steps (validate, sweeps, inspect, check-engine ×3, fingerprint subset, `regenerate-tunes --tune k20.stock,syn.r6.stock`) | all OK; both tunes reproduced exactly |
| Skills consistency (CI check) | OK (`car-sim-add-engine`, `car-sim-verify` updated and reinstalled) |
| Godot headless smoke tests | Not run locally (Godot is not installed in this container). **GitHub CI ran them** on the official 4.7.2 .NET build: K20, M54, and the pushrod V8 and twin-turbo V6 swapped into the Isar; dyno and drive, all passed |
| CI on GitHub ([run 76](https://github.com/Pyri4/car-tuning-sim/actions/runs/36441617002), head `b9f4d12`) | **success**: core build and tests, CLI smoke, skills check, the new check-engine step, verification tools, Godot smoke; the mutation job is manual-only (skipped) |

## 7. B58 negative test
The fixture (test-only) declares, on the synthetic turbo four as stand-in hardware, a B58-style identity: turbocharger,
intake VANOS, exhaust VANOS, direct injection, Valvetronic and a twin-scroll turbine. `check-engine` reports:

```
FEATURES  warning
  turbocharger: supported
  intake_cam_phasing: supported
  [warning] feature_not_modelled: direct_injection (Direct fuel injection) is not modelled; stands in: port injection
  [warning] feature_not_modelled: exhaust_cam_phasing (Exhaust cam phaser) is not modelled; stands in: omitted
  [warning] feature_not_modelled: twin_scroll_turbine (Twin-scroll turbine housing) is not modelled; stands in: single-scroll turbine
  [warning] feature_not_modelled: variable_valve_lift_continuous (Continuously variable valve lift (Valvetronic-type)) is not modelled; stands in: omitted
  [warning] feature_undeclared: The stock parts provide intercooler (Charge-air cooling), which identity.features does not declare.
RESULT  PASS with 7 warning(s)          (--strict: exit 3)
```

- The four gaps are named, each with its stand-in. Nothing is simulated that the model lacks; the declaration
  changes no physics (a Phase 1 test).
- Without an approximation, the same declaration is a load error and blocks the check (exit 2).
- The check also noticed the fixture forgot to declare the intercooler its stand-in hardware provides.
- No B58 content or capability was added.

## 8. LS3 readiness
An LS3-style fixture passes with only its missing provenance flagged: the synthetic pushrod V8 under a real-engine
identity with the cross-plane firing order 1-8-7-2-6-5-4-3. The report reads "V8 (90°), 2 banks, OHV (pushrod),
2 valves/cyl …". **The architecture an LS3 needs is checkable today.** What an LS3 author will still meet:

| Finding | Classification | Action |
|---|---|---|
| An LS3 block would provide a GM LS bellhousing that no loaded gearbox takes, so the check reports `no_vehicle_fits` | content | The pilot adds a gearbox part with that pattern (data) |
| An L99-style variant's cylinder deactivation (AFM) | missing physics | Declarable today as `cylinder_deactivation`, reported not modelled (tested) |
| An L99 / Gen V cam-in-block phaser moves intake *and* exhaust lobes together; the model's `intake_phaser_range_deg` moves the intake only | generic architecture limitation (a phaser type) | Documented; not needed for the LS3 (no VVT). A capability if a VVT pushrod engine is authored |
| V8 slot lists are still written by hand (≈ 104 lines, `syn_v8_ohv`) | authoring cost (schema) | Slot layouts, a later phase |
| Fuel-system capacity (pump and injectors against demand) is not checked | validator limitation | Needs a sweep: data-driven verification (Phase 3+), not a static check |
| Cylinder groups: not applicable (a V8 has banks); inline parallel twins still need cylinder groups | generic architecture limitation | ROADMAP, "Cylinder groups on inline engines" |

## 9. Documentation changes
- ENGINE_AUTHORING_GUIDE.md: §6 step 5 is now `check-engine`; new §6a (sections, exit codes, severity policy, what
  stays manual).
- ARCHITECTURE.md: the tools section; a decision-log entry (why the check is separate from loading, reuses the
  validator and lives in the core).
- ROADMAP.md: the Phase 1 gate passed; the Phase 2 entry; next task 1 status; test count.
- The milestone status; docs/VERIFICATION.md (43 mutants); README.md (the command).
- Skills:
  - `car-sim-add-engine`: the workflow now starts with create content → `check-engine` → fix → tune, with tiers per
    step;
  - `car-sim-verify`: `check-engine` in the CLI checks.

## 10. Limitations and remaining generic work
1. **Static only.** The check builds the assembly but runs no simulation. Starting, idling, fuel capacity and
   reference comparisons belong to data-driven verification (a later phase).
2. **Plausibility thresholds are heuristics** (class D authoring aids), warnings only. They can miss unusual but real
   engines, or flag them; the message says what to double-check.
3. **Stock tune agreement is checked only on the stock build**, not for other builds or tunes.
4. **Provenance counts cover the stock parts' authored spec fields.** Engine architecture records are listed, not
   counted. Tunes and reference figures have no provenance home yet.
5. **`--strict` is all-or-nothing on warnings.** Per-rule policies (e.g. real base content must be 100 % sourced) are
   not configured; the milestone's acceptance criterion 4 will need one.
6. **Slot layouts are still manual** (the largest remaining authoring cost); the check validates slots, it does not
   generate them.
7. **Generic architecture work surfaced:** a cam-in-block (pushrod) phaser type; cylinder groups on inline engines
   (known); cylinder deactivation (physics). None is needed for the LS3 itself.
8. **Godot** was not run in this container; CI ran the smoke tests and they passed.

## Model routing (as practised)
This session ran on one model and spawned no subagents.
- **T3 work:** the validation architecture, where each rule belongs (the loader vs the check; the reuse of the
  validator), severity policy, classifying the LS3 findings, and the report.
- **T1 work (mechanical, done inline because delegating would have cost more than the work):** the section map for
  the validator codes, repetitive test fixtures, mutation entries, CI lines and doc edits.
