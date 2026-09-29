# Engine Authoring Factory — audit (2026-09-28)

**Status:** Phase A (audit) of the strategy reset "content scalability / engine authoring factory". This audit read the
repository and measured it. It changed no simulation code, content, tune, tolerance, fingerprint or Intake Gas
Dynamics 2.0 parameter. The milestone it proposes,
[docs/milestones/ENGINE_AUTHORING_FACTORY_1.md](milestones/ENGINE_AUTHORING_FACTORY_1.md), is *proposed — awaiting
authorization*.

**Question:** what would make adding engine #20 nearly as easy as adding engine #2, without weakening the generic
simulation architecture, its verification, or its honesty about where numbers come from?

**Short answer:** the *simulation* side is already largely data-driven. The engine architecture milestone and the
synthetic matrix proved it for every architecture the model supports. The cost now sits around the physics:
- boilerplate the tools could generate (slot lists, tune skeletons, recipes, scenarios);
- provenance that lives in prose, where no tool can check it;
- validation that runs only when an engine is inspected or tested, not when it is loaded;
- per-engine C# in the verification layer (fingerprint cases, reference tests, family lists).

Almost everything needed exists as a component: the loader, validators, calibrators, tune driver, fingerprint and
mutation harness. The factory is mostly wiring these together and moving three kinds of per-engine knowledge from code
and prose into data: provenance, structure (slot lists and variants) and verification (fingerprint cases, reference
bands, smoke scenarios).

Method: the documents listed in AGENTS.md; every file in `src/CarSim.Core/Content`, `Engines`, `Parts`, the CLI, the
verification tools and the relevant tests; the content under `content/`; git history for the cost of past engines
(commands in the appendix). Measured numbers are marked *measured*; estimates are marked *estimated*.

---

## 0. The ten questions, answered

| # | Question | Answer (details in the section named) |
|---|---|---|
| 1 | What exists for engine authoring? | A strict JSON content system, typed specs, interfaces, topology rules, an assembly validator, derived geometry and a capability summary, `inspect`, four calibrators, a recipe-driven tune driver, the regression fingerprint, the mutation harness, a nine-engine synthetic matrix with data-driven pipeline tests, and three project skills (§1) |
| 2 | What is hard-coded? | No physics per engine. Hard-coded per engine: the fingerprint cases (C#), the reference tests (C#), the real-family lists in tests, the source audit's family tokens, and the CI smoke-test scenario lists. Boilerplate authored by hand: slot lists, tune skeletons, recipes and scenarios. Level constants fitted on the K20 are shared, not per engine (§4) |
| 3 | What requires C# today? | Per engine: the fingerprint matrix, reference tests and test family lists (unnecessary). Per missing physical feature: a capability (necessary, by design, guide §7). Per new part category: code (necessary) (§3) |
| 4 | What is data-driven? | Every item of the brief's §6 list that the model represents: geometry, banks, bank angle, firing order (validated, no physics), valvetrain type, valve count, cam profiles, intake VVT, two-stage VVL, N-stage runners, turbos (any count, per bank or shared), intercooler, exhaust, cooling, lubrication, ECU hardware and calibration. Not representable yet: DI, exhaust VVT, continuous VVL, superchargers, dry sump, inline cylinder groups, plenum geometry (§2) |
| 5 | How long does a realistic new engine take? | *Measured:* the M54 milestone added 2,568 lines in 50 files, of which the engine content was 380 lines in 9 files; synthetic engines are 280–467 lines each. *Estimated* for a new NA engine with no capability gap: ≈ 1,000–1,150 authored lines in 10–12 files, ≈ 450 of them C# or YAML, 12–16 commands and 3–4 hand pastes (§5) |
| 6 | What could be automated? | Slot lists from the bank topology; variants by inheritance; tune skeletons, recipes and tables; tune ↔ build consistency and plausibility checks; derived values; provenance coverage reports; fingerprint cases, conformance tests and reference comparisons from per-engine verification data; CI smoke lists (§6, §9) |
| 7 | What validation is automatic? | Schema, ranges, topology, cross-references and scenario fit at load. The physical assembly rules run only in `inspect` and in tests. Tunes are checked for shape, not for agreement with the engine. Provenance is not checked at all (§7) |
| 8 | What must an author construct by hand? | Every slot and its assembly order; every part; a tune skeleton, with values copied from the parts by hand; a manifest recipe; pasted calibrator output; scenarios; provenance prose; reference tests; fingerprint cases; CI lines (§8) |
| 9 | What CLI tooling can be extended? | `validate`, `inspect`, `sweep`, `calibrate-*`, `derive-stage`, `regenerate-tunes` (the tune generator's engine), `fingerprint`/`fingerprint-diff`, `bench`, `drive`. The mutation harness and `TuneManifest` are reusable as they are (§1, §9) |
| 10 | What verification can be reused? | Everything. The fingerprint needs data-driven case lists. The generic parts of `M54ReferenceTests` (≈ 75 % of it) become a conformance suite over every library engine. `EngineMatrixTests` already runs over whatever the matrix contains (§1, §9.8) |

Also asked for: what the minimum factory needs (§10), what not to build yet (§11), the pilot engine (§12), effort
before and after (§13), and the milestone breakdown (§14).

---

## 1. What already exists (reusable infrastructure)

| Component | What it does today | Role in the factory |
|---|---|---|
| Content format (`ContentLoader`, `ContentJson`) | JSON with comments, snake_case, unit-suffixed fields. **Unknown fields are errors**, number handling is strict, and every error is collected with its file and id | The one source of truth. The factory extends it rather than adding a second format: no YAML, no compiled "engine spec" layer (§9.0) |
| Content layers and mods (`LoadWithMods`) | Base, then mods in order; override by id (reported); cross-references checked after every layer has loaded | Engines, variants and reference data are layers. `content/test` already works this way |
| Typed part specs (`PartSpecRegistry`, 29 categories, `*Specs.cs`) | Per-category fields with units, ranges and defaults; SI conversion at load | Parts stay typed. A new part is data; a new category stays code (it needs physics) |
| Interfaces (`provides`/`requires`) | Mounting keys, checked bank by bank (`AssemblyValidator`) and across engine and car (`VehicleCompatibility`) | Compatibility stays interface-based. The factory adds a load-time check that every required key is provided by at least one part (typo catcher) |
| Topology (`EngineTopology.CheckFamily`) | Bank partition, layout ↔ bank count, bank angle, firing-order permutation, engine-wide vs bank-scoped categories, slot banks | Unchanged; generated slot lists go through it |
| Assembly validator | About 30 physical rules with codes: journals, pins, bores, gasket, valvetrain, coil bind, valve float, quench, CR, phaser/VVL/runner control, crank/flywheel speed, MAP range, boost | Reused as the core of the authoring check. Today `carsim validate` does not run it (§7) |
| Derived geometry (`EngineGeometry`), capability summary (`EngineCapabilities`) | Displacement, clearance volume, CR, quench per bank; layout, banks, valvetrains, phasers, VVL, runner stages, turbos, intake and exhaust paths | The derived-value report builds on them (§9.6) |
| `carsim inspect` | Architecture line, stock build, per-bank geometry, runner stages (β, fundamental, tuned speed), compatibility report | Extended with derived values, defaults and provenance (§9.6) |
| Calibrators (`calibrate-cams`, `calibrate-ve`, `calibrate-spark`; `runner_switch`/`lift_switch` in the driver; `derive-stage`) | Measure base maps on the model; deterministic | The tune generator's steps. No new calibration logic is needed |
| Tune driver (`regenerate-tunes`, `tune-manifest.json`) | A recipe per tune (build, fuel, ordered steps). Hand-authored tables are listed and audited. It writes only settled values, keeps the file's format, and records a `provenance` string per tune | **Already the core of tune generation.** Missing: the skeleton, the recipe derived from the hardware, and a one-command path (§9.7) |
| Regression fingerprint (`FingerprintMatrix`, 35 cases, baseline, `--dump`/`fingerprint-diff`, knife-edge sections) | Every K20, M54 and synthetic output at full precision | Kept. Library engines get a light, data-driven profile (§9.8) |
| Mutation harness (31 mutants, all caught) | Proves each guarding test fails on its bug | New invariants get entries; nothing to rebuild |
| Synthetic matrix + `EngineMatrixTests` + `ArchitectureInvariantTests` | Nine architectures through the whole pipeline. The matrix tests iterate `TestContent.MatrixFamilies` (no family named) | The model for library-wide conformance tests: tests that run over whatever content is loaded |
| Anti-hack tests (`EngineAgnosticTests`) | Source audit (family tokens, every content id in string literals, size branches); renamed-id identity; content-only variant | Extended to derive tokens from content and to cover tune-generation code (§9.9) |
| Save/load (`SaveSystem` v4) | Stores ids and slot ids; migrates; reports missing content | Unaffected if expansion happens at load (§9.1) |
| Godot (`GameState`) | Loads base content plus `CARSIM_MODS_DIR`; scenario picker; nothing engine-specific | Unaffected; new engines appear through scenarios |
| Skills (`car-sim-add-engine`, `car-sim-verify`, `car-sim-project-gate`) | The procedures of AGENTS.md as checklists | `car-sim-add-engine` becomes the pipeline, with model tiers per step (updated with this audit) |

---

## 2. What is data-driven today (the brief's §6 list)

| Item | Status | Where |
|---|---|---|
| Cylinder count, bank count, bank angle, bore, stroke, rod length | Data | family `banks`, `bank_angle_deg`; block, crank, rods |
| Compression ratio | **Derived** from volumes (never authored) | `EngineGeometry` |
| Firing order | Data, validated as a permutation; **no physics** (mean-value model) | family `firing_order` |
| Crank geometry | Stroke and journals are data. Crank phasing (flat vs cross plane) is not in the schema, since nothing would read it | crankshaft spec |
| Head architecture, valves per cylinder | Data (`valvetrain`, `valves_per_cylinder`), checked bank by bank | head and cam specs |
| Cam profiles, valve timing | Data: 1 mm durations, lifts, LSA, installed centrelines | cam spec |
| Intake VVT | Data (`intake_phaser_range_deg`) + ECU flag + tune table | — |
| Exhaust VVT | **Not modelled** (no exhaust-opening term) | capability (ROADMAP, "Engine capabilities still missing") |
| VVL | Two-stage data (`high_lift_profile`). Continuous VVL (Valvetronic-type) is **not modelled** | — |
| Runner geometry, DISA-style switched runners | Data: length, diameter, N stages, switch speeds in the tune | intake spec |
| Plenum geometry | **Not in the schema** (deferred with the plenum/group mode, Intake Gas Dynamics 2.0 Phase 2) | — |
| Throttle geometry | Data (bore, flow), at most one throttle per bank. Individual throttles are approximated as one | throttle spec |
| Injector configuration | Data (count, flow, rated pressure, dead time, max duty). **Port injection is implied** | injector spec |
| Fuel system | Data (pump flow, regulator). **Direct injection is not modelled** | fuel pump spec |
| Turbo configuration, multiple turbos, turbo per bank | Data (parametric compressor and turbine) | turbo spec, bank slots |
| Inline parallel twins (RB26, N54, 2JZ-GTE) | **Blocked**: an inline engine has one bank (ROADMAP, "Cylinder groups on inline engines") | `EngineTopology` |
| Twin-scroll, sequential turbos, superchargers | **Not modelled** | — |
| Intercooler, exhaust geometry | Data | specs |
| Cooling, oil capacity, lubrication | Data (radiator UA, coolant capacity, thermostat; sump capacity, surge g, baffle, pump). **Dry sump not modelled** | specs |
| Engine mass | Data per part (`mass_kg`); the car's mass follows the installed parts | `VehicleConfiguration` |
| ECU calibration, rev limit, idle, λ, spark, boost targets | Tune data. VE, spark and cam tables come from the calibrators; λ and boost targets are hand-authored | tunes, manifest |
| Engine-specific parts | Data | parts |

So "what cannot be represented generically" is a short, already documented list (guide §9). The factory must make that
list visible for each engine rather than only in the docs (§9.4).

---

## 3. What requires C# today

Per **new engine** (should not require code):

| Touch point | Why it is code today | Size for the M54 | Verdict |
|---|---|---|---|
| `tools/CarSim.Verification/Fingerprint/FingerprintMatrix.cs` | Real-engine cases (builds, failure holds, scenario, lap) and the synthetic list `(family, fuel)` are C# literals | ≈ 15 references; 9 tuples for the matrix | **Unnecessary.** A light library profile can be derived from data. The K20/M54 deep cases stay C# as regression anchors (§9.8) |
| `tests/.../SecondFamily/M54ReferenceTests.cs` | Reference bands and the generic conformance checks are written per engine | 437 lines, 21 tests. About 16 are generic ("starts and idles", "respects its rev limit", "fuel map on target", "timing changes torque and knock", "survives sustained full power", over-advance and over-rev failures, damage persists through a save, replay, determinism); about 5 compare with the reference | **Unnecessary.** Split into a library conformance suite plus reference data with bands stated in advance |
| `TestContent.Families`, `EngineAgnosticTests.Families` | Real families and their scenarios listed by hand | 2–4 lines | **Unnecessary.** Derive from content |
| Source-audit token regex (`k20\|m54\|kestrel_k20\|isar_m54\|syn_…`) | Family tokens hard-coded (content ids are already derived) | 1 line | **Unnecessary.** Derive the tokens from engine ids and identity metadata |
| `.github/workflows/ci.yml` smoke steps | Scenario lists in YAML | 4–10 lines | **Unnecessary.** List the smoke scenarios from data with a CLI command |

Per **new physical feature**: a capability (guide §7). This is **necessary**, and it is the point of the architecture.
Per **new part category**: code, **necessary** (the simulation must know what the fields mean).

No engine has ever needed a simulation branch. The only generic model change the M54 needed (cam timing) was a
capability, which is the intended route.

---

## 4. What is hard-coded (scale risks)

1. **Slot lists** are data, but pure boilerplate. They are fully determined by the bank topology, the per-bank vs shared
   choice and a naming convention. *Measured:* 23–32 lines for inline engines and 104–138 lines for V and flat engines,
   i.e. 26–30 % of every multi-bank synthetic file.
2. **Real-engine verification in C#** (§3).
3. **Level-setting constants fitted on the K20:** Otto realisation 0.80, the FMEP coefficients, v₀ and the ceiling, the
   crown heat flux and the header constant. They are shared (class C), so there is no per-engine hack. But **every new
   real engine inherits the K20's level calibration** (the M54 sits −10 % in power). This is the largest *physics*
   risk to a content library. It must not be "fixed" per engine. The honest fix is multi-family
   calibration (ROADMAP, "Multi-family calibration"), and it needs several reference engines authored with provenance. The factory produces exactly those,
   so **the factory is the prerequisite for honest multi-family calibration**, not a detour from physics.
4. **Silent defaults.** A field the author did not write becomes a model input with no record:
   - runner diameter 0.40 × bore (the only one flagged, as `default_intake_geometry`);
   - injector dead time 0, rated pressure 300 kPa, max duty 0.85;
   - block coolant 3.0 L;
   - valvetrain `dohc` and 4 valves;
   - radiator reference air speed;
   - fuel MON = RON − 10.

   A provenance system that ignores defaults would miss exactly the values nobody chose.
5. **Tune values copied from parts by hand:** `displacement_cc`, `injector_flow_cc_min`, `injector_dead_time_ms` and
   `fuel_density_kg_l` are ECU beliefs, deliberately separate from the hardware. In the *stock* tune on the *stock*
   build they should agree, and nothing checks that they do.
6. **Scenario boilerplate:** every matrix engine needs `<family>_bench` (the matrix tests read its fuel) and
   `<family>_swap` scenarios, 8 lines each.

---

## 5. How long a new engine takes today (measured history)

**Second engine family (PR #4, the M54), measured with `git diff --numstat 0970bbe^1 0970bbe`:** 50 files, +2,568 / −122.

| Kind | + lines |
|---|---|
| Content JSON (engine, 3 part files, tune, car, chassis parts, scenario) | 392 (the content commit alone: 9 files, 380 lines) |
| `src/` (the cam-timing capability, justified) | 409 |
| Tests | 1,227 |
| Docs | 373 |
| Tools (CLI calibrators) | 115 |
| Game | 44 |

**Synthetic matrix (engine-architecture milestone), measured:**

| Family | File | Engine block (of which slots) | Parts | Tune | Tune table cells |
|---|---|---|---|---|---|
| `syn_i4_sohc` | 280 | 57 (23) | 173 | 48 | 360 |
| `syn_i6_vis` | 282 | 57 (23) | 174 | 49 | 384 |
| `syn_i3_turbo_vvl` | 326 | 68 (32) | 191 | 65 | 585 |
| `syn_i4_turbo` | 341 | 68 (32) | 192 | 79 | 832 |
| `syn_v8_ohv` | 379 | 145 (104) | 184 | 48 | 312 |
| `syn_v6_na` | 384 | 151 (110) | 183 | 48 | 360 |
| `syn_h4_turbo` | 415 | 156 (113) | 193 | 64 | 624 |
| `syn_v8_dohc_vvt` | 422 | 151 (110) | 211 | 58 | 544 |
| `syn_v6_tt` | 467 | 183 (138) | 203 | 79 | 832 |

Plus 105 lines of scenarios and 107 of shared parts for the nine. Every table except λ and boost came from the
calibrators, pasted or written by the driver.

**The procedure today (guide §6) for a real engine with no capability gap**, *estimated* from the measured analogues
(a V8 like `syn_v8_ohv`, a real engine like the M54):

| Step | Manual work | Lines (est.) | Kind |
|---|---|---|---|
| Source the data; write provenance prose in PARTS_DATABASE.md | research + prose | ≈ 70 | docs |
| Engine definition: slots by hand, stock parts | ≈ 110 slot lines for a V | ≈ 150 | data |
| 15–25 parts | typing specs | ≈ 180–210 | data |
| Tune skeleton: axes, λ, limits; displacement, injector flow, dead time and density copied from parts; placeholder tables | copying | ≈ 50 | data |
| Recipe in `tune-manifest.json` | by hand | ≈ 5 | data |
| Calibrators: 3–5 runs, output pasted (or `regenerate-tunes --write` once skeleton and recipe exist) | 3–4 pastes | — | commands |
| Scenario(s), a gearbox or car for the bellhousing | by hand | ≈ 30–40 | data |
| Reference tests (as `M54ReferenceTests`) | C# | ≈ 300–440 | **code** |
| Fingerprint cases | C# | ≈ 5–15 | **code** |
| Test family lists, CI smoke steps | C#, YAML | ≈ 5–10 | **code/config** |
| Results in SIMULATION_SPEC.md, counts in README/ROADMAP | prose | ≈ 50–100 | docs |

**Total (estimated): ≈ 1,000–1,150 lines in 10–12 files, ≈ 450 of them C# or YAML; 12–16 commands; 3–4 hand
pastes.** Validation problems (a wrong journal, a mismatched displacement) surface at `inspect` or `dotnet test` time,
not at load.

---

## 6. Pain points (what is unnecessarily manual)

| # | Pain point | Evidence |
|---|---|---|
| P1 | Slot lists written by hand although the bank topology determines them | 104–138 lines per V/flat engine (§4.1) |
| P2 | Provenance is not machine-readable. It lives in PARTS_DATABASE.md prose and in JSON comments, which the parser strips, so no tool can tell a published value from an estimated or fitted one | M54: "estimated" port flows turned out to be worth +6.6 % at 6,000 rpm (SIMULATION_SPEC, M54 investigation). The Phase 1 review found κ and the flows confounded, so the next M54 decision is data quality, which is invisible to the tools |
| P3 | Tune skeletons are hand-copied from parts. Recipes are written by hand although the hardware determines their steps (phaser → `cams`, VVL → `lift_switch`, switched intake → `runner_switch`, then `ve → spark → ve`). Calibrator output is pasted | §4.5; guide §6 step 6 |
| P4 | `carsim validate` does not assemble the stock build or check the stock tune against it; physical errors appear only in `inspect` or tests | `Program.Validate` only loads |
| P5 | No plausibility ("suspicious value") warnings: bore/stroke, rod ratio, mean piston speed, specific output, injector duty at peak, oil per litre | — |
| P6 | Real-engine verification is C# per engine | §3 |
| P7 | No family/variant distinction. A second variant duplicates the whole definition; a part variant duplicates the whole part | The M54 forged pistons re-type all 9 spec fields to change 5; a second M54 variant would copy the 21 slots |
| P8 | Real hardware features the model lacks cannot be declared. The M54's exhaust VANOS and hot-film MAF are prose; a B58's DI could only be omitted silently | PARTS_DATABASE, "Simplifications" |
| P9 | Silent defaults (§4.4) | — |
| P10 | Scenario and CI boilerplate per engine | §4.6, §3 |
| P11 | No list or summary of the library (which engines, which capabilities, which provenance quality, which verification status) | — |
| P12 | Derived values are incomplete. `inspect` shows displacement, bore × stroke, rod ratio, CR, clearances, reciprocating mass, runner tuned speeds and total mass. It does not show mean piston speed, firing interval, theoretical airflow or which fields were defaulted | `carsim inspect isar_m54` |

---

## 7. What validation is automatic today

| Layer | When | What |
|---|---|---|
| Schema | load | Unknown fields, types, required fields, JSON syntax (all errors, with file and id) |
| Spec ranges | load | Per-field ranges (`SpecChecker`), curves strictly increasing, adjustable ranges validated at both ends |
| Topology | load | `EngineTopology.CheckFamily`, acyclic `install_after`, unique slots, known categories |
| Cross-references | load (after every layer) | Stock parts exist and match slot categories; stock tune exists; scenario engine/vehicle/fuel/tune/slots; the scenario's car takes its engine by interfaces |
| Tune shape | load | Axes strictly increasing; tables sized to the axes and in range; required fields |
| Physical assembly | `inspect`, `EngineConfiguration.Build`, tests | `AssemblyValidator` (≈ 30 coded rules) |
| Stock build completeness | tests (`EveryBaseEngineHasACompleteStockBuild`, matrix pipeline) | Every required slot filled; builds, starts, runs |
| Tune reproducibility | tests (a recipe per tune; recipe order) and `regenerate-tunes` | Tables are what the recipe gives |
| Behaviour | tests, fingerprint | Pinned outputs, invariants, architecture properties |
| Identity | tests | Source audit, renamed-id identity, content-only variant |
| Provenance | — | **Nothing** |
| Tune ↔ build agreement | — | **Nothing** (§4.5) |
| Declared vs modelled features | — | **Nothing** (P8) |
| Interface keys | assembly time | A mistyped `requires` shows up only as a "missing interface" when assembled (no check that some part provides the key) |

---

## 8. What an author must construct by hand

Every slot (id, category, display name, banks, `install_after`, accessibility); every part and its interfaces; the
stock-part map; a tune skeleton with hand-copied hardware beliefs; the recipe; the pasted tables; scenarios (and for
the matrix `_bench`/`_swap`); provenance prose; reference tests; fingerprint cases; family lists; CI steps; the results
write-up.

Of these, **parts, interfaces, the topology choice, sourced figures and the write-up of misses are irreducible**:
they are the engine. Everything else can be generated or derived.

---

## 9. Proposed architecture

### 9.0 Principles
1. **One source of truth: the existing JSON content.** No second specification format, no YAML, no generator whose
   output is checked in beside its input. Authoring conveniences are **expanded by the loader into the same
   `EngineDefinition`/`PartDefinition` the simulation uses today**. The physics cannot see them, and existing content
   is bit-identical by construction (checked by the fingerprint).
2. **Physics vs content** (to be stated in ENGINE_AUTHORING_GUIDE.md):

   | Physics (code, shared, tested) | Content (data, per engine, sourced) |
   |---|---|
   | What a runner does (acoustic mode, wave gain) | How long this engine's runner is |
   | How compression ratio affects combustion and knock | This engine's chamber, gasket, dish and deck volumes (the CR is derived) |
   | How a compressor behaves (speed lines, surge, choke) | This engine's turbo and its map parameters |
   | How cam timing affects filling | This engine's cam profiles and phaser range |
   | How the ECU meters fuel (speed density) | This engine's calibration (from the calibrators) |
   | Which features exist (capabilities) | Which features this engine has (its parts, and declared features) |

3. **Tools, not magic.** Every generated value is deterministic, reproducible, inspectable and overridable, and it
   is labelled as generated.
4. **Warnings grow into gates, not into load errors.** New authoring checks are warnings with codes. A `--strict`
   mode (CI, base content) turns selected ones into failures. Mods keep loading.

### 9.1 Slot layouts (P1)
An engine may give a `slot_layout` instead of `slots`. A **slot template** is itself content (a `slot_templates`
document kind, so a mod can add one). It lists the standard piston-engine slots with categories, display names,
`install_after` and accessibility. The engine names which bank-scoped categories are **per bank**; every other
template slot serves all banks:

```jsonc
"banks": [{"id": "left", "cylinders": [1, 3, 5, 7]}, {"id": "right", "cylinders": [2, 4, 6, 8]}],
"slot_layout": { "template": "piston_engine", "per_bank": ["head_gasket", "cylinder_head", "valve_springs",
                 "exhaust_manifold", "exhaust"] }   // camshafts, intake, throttle: one for the engine (a pushrod V8)
```

The expansion follows the matrix's existing conventions:
- per-bank slots are `<slot>_<bank>`, named "(left)";
- a per-bank dependency stays on its bank;
- a shared slot that depends on a per-bank slot waits for every bank.

Single-bank engines keep plain slot ids, so saves and scenarios are unaffected. Explicit `slots` stay valid, and the
K20 and M54 need not change.

**Proof:** re-author the multi-bank matrix engines with layouts. Their expanded definitions must equal the checked-in
explicit ones (a unit test) and the fingerprint must stay identical. The per-bank choice *is* the air-path topology
(guide §6 step 2), so this compresses exactly the decision an author must make, and nothing more.

### 9.2 Families and variants (P7)
- An engine definition may declare `"extends": "<engine id>"`: it inherits everything and replaces what it names.
  `stock_parts` merges entry by entry; `slots`, `slot_layout` and `banks` are replaced whole.
- `"abstract": true` marks a family base that is not buildable on its own. It needs no complete stock build and is
  hidden from scenarios.
- Parts may `extend` a part: spec fields merge field by field, and interfaces are inherited unless given.
- Resolution happens after all layers have loaded, like the existing cross-reference checks, so a mod overriding a
  parent reaches its children. Cycles and missing parents are errors. Chains are allowed; there is no multiple
  inheritance.
- An `identity` block (`manufacturer`, `family`, `variant`, `years`, `market`, `kind`: `real` | `fictional` |
  `synthetic`) is display and provenance metadata. **Nothing in `src/` may read it**, and the source audit enforces
  that (§9.9).

Vocabulary: the repository's word "engine family" today means one buildable definition. From now on, a *family* is a
base (possibly abstract) and a *variant* is a definition that extends it. A definition without `extends` is both, as
the K20 and M54 are.

### 9.3 Provenance (P2, P9)
The repository already classifies *values* in words: PARTS_DATABASE's M54 tables use published, measured
(enthusiast), derived, converted, estimated and calibrated. It already uses the letters A–D twice, for debt classes in
gates and for model-constant classes in SIMULATION_SPEC. A third letter scale would collide, so provenance **kinds
are words**, ranked and mapped to the owner's A–E:

| Kind | Owner's class | Meaning | Required extra |
|---|---|---|---|
| `measured` | A | Measured on the real part, or primary documentation (factory repair or service data, a drawing) | source |
| `published` | B | Manufacturer or official specification (spec sheet, press kit, crate-engine sheet) | source |
| `secondary` | C | Reputable secondary source (trade press, enthusiast measurements, parts listings) | source; `corroborated` when ≥ 2 independent sources agree |
| `converted` | as its input | A definitional conversion of a sourced value (advertised → 1 mm duration, lb/h → cc/min) | the input's source and the conversion |
| `derived` | as its weakest input | Computed from other authored values by a named formula or tool (A-D1 stage; a dish volume solved from a published CR) | `method` |
| `estimated` | D | Engineering estimate with no source | `band` or note |
| `fitted` | E | Chosen so that the model reproduces a reference *output* (a curve, a power figure). The guide forbids this for content, so it may only appear with the target named and a written justification | `target`, note |
| `calibrated` | — | Tune tables produced by the dev calibrators (already recorded by the tune manifest; not duplicated) | recipe |
| `synthetic` | — | Fictional test content (the matrix) | — |
| `defaulted` | — | **Computed by the tool, never authored**: the field was absent and the schema default applied | — |

A geometric closure (the M54's 12.1 cc bowl solved so that the published CR comes out) is `derived`, not `fitted`:
it is fitted to a published *specification*, not to model output. `fitted` is reserved for the thing this project
forbids, so that the rare justified case is visible.

Format sketch (optional fields; old content stays valid):

```jsonc
"sources": [{ "id": "gm_ls3_spec", "title": "…", "publisher": "…", "type": "manufacturer_spec",
              "url": "…", "accessed": "2026-…", "license_note": "facts cited; no text or images copied" }],
"parts": [{ "id": "…", "category": "pistons", "spec": { … },
            "provenance": { "*": { "kind": "published", "source": "gm_ls3_spec" },
                            "mass_each_g": { "kind": "estimated", "note": "no source; band 480–520 g" } } }]
```

Checks:
- field names must exist on the spec type (error);
- sources must exist (error);
- `fitted` without a `target` (error);
- coverage per engine (warning; required at 100 % of physical fields for `identity.kind: real` in base content under
  `--strict`);
- `defaulted` fields listed automatically.

The report prints counts per kind and every estimated, defaulted and fitted value, so a reviewer sees at once what an
engine's numbers rest on.

### 9.4 Declared features (P8)
`identity.features` lists the real engine's hardware features from a fixed vocabulary kept in code next to
`EngineCapabilities` (the single statement of what the physics can do): `intake_cam_phasing`, `exhaust_cam_phasing`,
`variable_valve_lift_two_stage`, `variable_valve_lift_continuous`, `variable_intake`, `turbocharger`,
`twin_scroll_turbine`, `sequential_turbos`, `supercharger`, `direct_injection`, `dry_sump`, `individual_throttles`, …

The check compares the declared features with the resolved capabilities:
- declared and modelled but absent from the parts → warning;
- declared and not modelled → `feature_not_modelled` warning, plus a required `approximations` note saying what stands
  in (a real engine without that note fails `--strict`);
- modelled but undeclared → info.

This is the brief's §23 made executable:
- the M54 would say "exhaust VANOS not modelled: exhaust cam held at park";
- a B58 authored today would say "direct injection, continuous VVL, twin-scroll turbine not modelled".

Nothing is faked and nothing is hidden.

### 9.5 Authoring check (P4, P5): `carsim check-engine <id>` and `carsim validate --engines`
One report per engine, codes and severities like `CompatibilityIssue`:

- **Stock build:** assemble and run `AssemblyValidator` (today only in `inspect`); check that every `requires` key is
  provided by some loaded part (typo catcher).
- **Tune ↔ build:** stock tune `displacement_cc` vs derived displacement (±1 %), injector flow, dead time and rated
  pressure vs the stock injectors, fuel density vs the calibration fuel, rev limit vs the ECU's ceiling and the valve
  float speed, load axis vs MAP sensor range and boost target, tables present for controlled hardware (cam table,
  switch speeds inside the rpm axis), the recipe exists and its order is legal.
- **Plausibility (heuristic, class D, warnings only, thresholds documented):**
  - bore/stroke ratio outside 0.6–1.5;
  - rod/stroke ratio outside 1.4–2.3;
  - mean piston speed above 25 m/s at the rev limit;
  - after a sweep: BMEP outside the NA or boosted envelope, injector duty above `max_duty` at peak power (measured on
    the model, not from an assumed BSFC), knock retard at WOT on the calibration fuel, oil capacity per litre.
- **Physics smoke** (the generic half of `M54ReferenceTests`): starts, idles at its target, a finite WOT sweep to the
  limiter, no failure in a sustained full-power hold on the stock build.
- **Features and provenance:** §9.3–9.4.

### 9.6 Derived values (P12)
`inspect` (or `check-engine`) prints, labelled *derived*:
- displacement, CR, clearances, rod ratio and reciprocating mass (these exist), plus swept and clearance volume per
  cylinder and CR per bank;
- bore/stroke;
- mean piston speed at the rev limit;
- firing interval and the bank's firing phasing (from order and angle; data only, no physics claim);
- runner tuned speed per stage (exists);
- theoretical airflow at the limiter at VE 1;
- valve float speed (exists as a warning);
- total oil and coolant capacity;
- the list of defaulted fields.

Nothing here invents a physical constant. Values that need one (a BSFC for injector sizing) are measured on the model
instead.

### 9.7 Tune generation (P3): `carsim generate-tune <engine> [--fuel] [--write 1]`
1. **Skeleton from the build:**
   - axes: idle to limit + 1,000 rpm; loads 10 kPa to the MAP sensor's range or boost target + margin;
   - hardware beliefs taken from the stock parts and the calibration fuel (displacement, injector flow and dead time,
     density, stoich);
   - rev limit and idle from the reference data or stated on the command line — never guessed;
   - λ targets from a **named policy** (e.g. `na_pump`, `boost_pump`, recorded as `hand_authored` by policy in the
     manifest);
   - boost target only from a sourced figure or an explicit argument.
2. **Recipe from the hardware:** `cams` if a controlled phaser, `lift_switch` for two-stage cams, `runner_switch` for
   switched intakes (N − 1 switch speeds), then `ve → spark → ve`. The recipe goes into `tune-manifest.json`.
3. **Run it through the existing driver** (`TuneRegenerator`: settled values only, the file's format kept).

The generated tune is deterministic and reproducible (`regenerate-tunes` checks it like any other). It is inspectable
(manifest and description say how it was made) and overridable (a table moved to `hand_authored` is never rewritten).
It is distinguishable from a hand-authored calibration, because the manifest lists which tables are which.

### 9.8 Verification from data (P6, P10)
An **engine verification profile** per library engine (e.g. `verification/engines/<id>.json`, loaded by the
verification tools, never by the game):
- calibration fuel;
- a fingerprint profile (`light`: geometry, WOT, part load, dyno pull — the synthetic cases' sections);
- smoke scenarios;
- optional reference figures, each with provenance and **an acceptance band stated before the first run** (recorded in
  git before the engine's output is committed, as the A-D1 derivation was).

From it:
- `FingerprintMatrix` adds the light cases. Existing case ids and sections are unchanged, so the baseline file does not
  move.
- A **library conformance suite** runs the generic checks of `M54ReferenceTests` over every library engine, like
  `EngineMatrixTests`.
- `ReferenceComparisonTests` assert the bands and print the curves as held-out evidence (never as targets).
- `carsim compare-engine <id>` prints model vs reference.
- `carsim list-scenarios --smoke` feeds CI.

The K20/M54 deep cases (turbo builds, failure holds, laps) stay hand-written: they are the model's regression
anchors, not library entries.

### 9.9 Anti-hack extension (brief §20)
- Source-audit tokens derived from content (engine ids, and `identity.family` and `variant` tokens) instead of a fixed
  regex, so a new engine's names are forbidden in `src/` the moment it loads.
- The audit's scope extended to the tune-generation and calibration code (`tools/CarSim.Verification/Calibration`,
  `src/CarSim.Core/Ecu/*Calibrator.cs`), where "special-case tune corrections" would hide.
- An `identity` read audit: nothing in `src/` references the identity block.
- Renamed-id identity for every library engine, as part of the conformance suite (one short sweep each).
- Mutation entries for each new check, for example:
  - `check-engine` ignoring a displacement mismatch;
  - a generated recipe skipping `runner_switch`;
  - the fingerprint's derived list dropping an engine;
  - an expansion that reuses the first bank's slot.

Considered and rejected: a numeric-literal audit for "engine constants" in `src/` (hopelessly noisy). The fingerprint,
the multi-engine references and review cover it.

### 9.10 Commands (the brief's §10 wishes, mapped to what exists)

| Wished for | Exists | Proposed |
|---|---|---|
| `validate-engine` | `validate` (load only) | `check-engine <id>`; `validate --engines [--strict]` |
| `inspect-engine` | `inspect` | + derived values, defaults, features, provenance summary |
| `list-engines` | — | `list` (engines, identity, layer, capabilities, provenance quality, verification status) |
| `compare-engine` | — (per-engine C# tests) | `compare-engine <id>` from the verification profile |
| `dyno` | `sweep`; the game's dyno | unchanged |
| `generate-tune` | `calibrate-*`, `regenerate-tunes` | `generate-tune` (§9.7) |
| `verify-engine` | pieces | `verify-engine <id>`: check + tune reproduces + conformance + references + its fingerprint cases |
| `fingerprint <engine>` | `fingerprint --case <prefix>` | unchanged; library case ids start with the engine id |

The CLI's `Program.cs` (684 lines) is already listed as debt ("split commands into classes"). The new commands should
land in their own files.

### 9.11 Parts (brief §8, §9)
Parts are already data per category. The factory adds:
- part variants (`extends`);
- provenance;
- a `carsim schema <category>` printout (fields, units, required/default) generated from the spec types, so an author,
  or a small model, does not need to read PARTS_DATABASE.md to fill a part;
- the provided-key check.

It does **not** split categories (valves, retainers, water pump, dampers, ARBs as separate parts). A new category is
justified only by a simulation consequence (ARCHITECTURE §5), and that is a separate decision. Swap interfaces beyond
the bellhousing (mounts, wiring, fluids, driveshaft) are ROADMAP's "Swap interfaces beyond the bellhousing", not this milestone.

### 9.12 Licensing and IP (brief §3)
State today:
- marques are fictional (Kestrel ↔ Honda K20, Isar ↔ BMW M54);
- real references are cited in PARTS_DATABASE.md;
- there are no third-party assets (primitive visuals, no audio).

Proposal:
- `sources` carry a type and a licence note;
- engine data is facts with citations;
- nothing is copied from any game or mod (Car Mechanic Simulator included): no code, meshes, textures, audio, data
  files or text;
- such games are used only as UX and architecture inspiration (reusable assembly groups ≈ slot layouts; tuned part
  versions ≈ part variants; swaps ≈ interfaces);
- a third-party dataset or mod is used only when its licence explicitly permits it, recorded in its `sources` entry;
- real names stay in `identity` and provenance, and the game keeps the fictional-marque convention.

This is project policy, not legal advice. The owner decides edge cases.

---

## 10. The minimum factory

The smallest set that makes a new real engine "data + validation", in dependency order:
1. `identity`, provenance and sources in the schema, with checks and a report (§9.3). Without this, scale multiplies
   unknown-quality numbers.
2. `check-engine` with stock-build, tune ↔ build, feature and plausibility checks (§9.4–9.5). This is what catches
   mistakes before the engine enters the game.
3. `generate-tune` (§9.7): removes the largest manual, error-prone step.
4. Verification profiles plus data-driven conformance, reference and fingerprint cases (§9.8): removes all per-engine
   C#.
5. Slot layouts and variants (§9.1–9.2): the largest line-count saving, and the enabler for variant libraries.
6. Derived values in `inspect` (§9.6), `list`, `schema`, anti-hack extension (§9.9), skills.

---

## 11. What should NOT be built yet

- A GUI editor or web authoring tool.
- A second specification format (YAML, TOML, a compiled "engine spec") beside the JSON.
- Importers or scrapers of third-party data, and bulk part catalogues.
- More than one or two pilot engines before the pipeline is measured.
- B58, N54, 2JZ-GTE and RB26 as pilots: each needs a missing capability (DI, exhaust VVT, continuous VVL, twin-scroll)
  or cylinder groups first. The B58 is still useful as a **negative test**: its declared features must be reported as
  not modelled (§12).
- A parametric "part generator" that invents parts (a "stage 2 cam") from rules. It would invent values; variants
  copy sourced values instead.
- Asset or sound reference fields. There are no assets yet; when there are, each reference carries its licence in
  `sources`.
- Multi-family re-fitting of the level constants, Intake Gas Dynamics Phase 2, per-cylinder state. These are physics
  milestones. The factory makes them better informed, not unnecessary.
- New part categories, or swap interfaces beyond the bellhousing (ROADMAP, "Swap interfaces beyond the bellhousing").

---

## 12. Pilot engine

Criteria:
1. data-only today (no missing capability, no cylinder groups);
2. new architecture coverage among *real* engines (today only an I4 and an NA I6 exist);
3. reliable published data with citable sources;
4. a published output figure or curve for held-out comparison;
5. low risk of turning the pilot into physics work.

| Candidate | Architecture | Data-only today? | Data availability | Verdict |
|---|---|---|---|---|
| **GM LS3 (6.2 L)** | 90° V8, pushrod, 2 valves, shared cam, plenum and dual exhausts, NA, port injection | **Yes** (as `syn_v8_ohv`) | High: the maker's crate-engine spec sheet gives bore, stroke, CR, cam lift, duration and LSA, chamber, injectors and a rated curve; heads and intake are widely documented | **Recommended pilot 1** |
| Subaru EJ20 turbo (EJ205/EJ207) | Flat-4, one turbo fed by both banks, intake AVCS, intercooler | Yes (as `syn_h4_turbo`) | Medium: turbo map parameters and AVCS range mostly secondary or estimated | **Pilot 2 (stretch)**: exercises turbo authoring and boost targets |
| Honda F20C | NA I4, VTEC on intake and exhaust, 9,000 rpm | Yes | Medium–high | Good; less new architecture (close to the K20) |
| Mitsubishi 4G63T | Turbo I4 | Yes | Medium | Less coverage than the EJ20 |
| Toyota 2JZ-GE (NA) | I6 | Yes | Medium | Too close to the M54 |
| Toyota 2JZ-GTE / Nissan RB26 | Inline parallel twin turbo | **No**: cylinder groups (ROADMAP, "Cylinder groups on inline engines") | Medium | After cylinder groups |
| BMW B58 | I6 turbo, DI, Valvetronic, double VANOS, twin scroll | **No**: 3–4 capabilities missing | High | **Negative test only**: author the identity and declared features; the check must refuse to present it as modelled |

The LS3 exercises:
- the first real V engine, and multi-bank authoring through slot layouts (per-bank heads, gaskets, springs,
  manifolds, exhausts; shared cam, intake and throttle);
- a real firing order (1-8-7-2-6-5-4-3);
- a new bellhousing pattern, with a gearbox part that carries it, so the swap into the Isar C30 is decided by
  interfaces, not by borrowing the M54's pattern as the matrix engines do;
- reference comparison against a maker's rated figures;
- provenance with a high share of `published` values.

It stresses the **pipeline** and not the physics, which is what a pilot must measure. Intake Gas Dynamics 2.0 wants a
second real reference with published intake geometry (U8). The LS3 could serve that later only if its runner geometry
turns out to be sourced, and this milestone fits nothing to it. Its expected misses (the
K20-fitted level constants; the pushrod valvetrain's friction and float against a 2-valve head) become documented,
classified evidence for the multi-family calibration milestone. They are not a reason to touch constants. In the game
it follows the fictional-marque convention; the real reference is recorded in provenance.

---

## 13. Authoring effort, before and after

| Measure | Before (V8 NA real engine, estimated from measured analogues) | After (target, to be measured by the pilot) |
|---|---|---|
| Files touched | 10–12 | 3–5 (engine + parts, verification profile, scenario; generated tune file and manifest entry) |
| Lines of data | ≈ 450–500 (≈ 110 of them slots) | ≈ 300–400 (layout ≈ 5 lines; + provenance ≈ 50–80, replacing ≈ 70 lines of prose) |
| Lines of C# / YAML | ≈ 330–460 | **0** (a non-zero value is a documented factory failure) |
| Tune work | skeleton by hand, recipe by hand, 3–5 calibrator runs, 3–4 pastes | 1 command (`generate-tune --write 1`); λ/boost from a named policy or a sourced figure |
| Commands | 12–16 | ≈ 5: `check-engine` → `generate-tune` → `verify-engine` → `dotnet test` (fingerprint write) → Godot smoke |
| Errors found at | `inspect` or test time | load and `check-engine` time |
| Provenance | prose, unchecked | machine-readable, coverage reported, fitted/estimated listed |
| A **variant** of an existing family | a full copy (≈ 300+ lines) | ≈ 20–60 lines (`extends` + differing parts; generated tune) |

The irreducible cost after the factory is the parts: typing sourced physical facts. That is the brief's "engine #100 is
mostly data entry + validation".

**Benchmark protocol** (the milestone records it for the pilot):
- `git diff --numstat` by kind (data, code, tests, docs, generated);
- commands run;
- validation iterations until clean;
- hand edits after the first `check-engine`;
- escalations, with the model tier each step used;
- every C# line it needed, with the reason.

---

## 14. Proposed milestone breakdown

Defined in [docs/milestones/ENGINE_AUTHORING_FACTORY_1.md](milestones/ENGINE_AUTHORING_FACTORY_1.md) (proposed —
awaiting authorization):

| Phase | Content | Proof that nothing moved |
|---|---|---|
| B1 Schema | `identity`, `sources`, provenance, slot templates and layouts, `extends` and `abstract` for engines and parts; loader expansion after all layers | Fingerprint identical; matrix engines re-authored with layouts expand to their old slot lists |
| B2 Check | `check-engine`, `validate --engines [--strict]`: stock build, provided keys, tune ↔ build, features, plausibility, provenance, defaults | One negative test per rule (as `AnInconsistentArchitectureIsRejectedWithItsReason`); mutants |
| B3 Derived | derived values, defaults and features in `inspect`; `list`; `schema` | Golden output tests on the matrix |
| C1 Tunes | `generate-tune` (skeleton, policy, recipe, driver) | Regenerating a matrix tune from scratch reproduces its checked-in calibrated tables |
| C2 Verification data | profiles; data-driven light fingerprint cases; conformance suite; reference comparison; `compare-engine`, `verify-engine`; CI smoke list | Baseline file unchanged; the suite passes on K20, M54 and matrix |
| C3 Anti-hack | derived tokens, calibration code in the audit, identity-read audit, renamed-id identity for all, mutation entries | Every new mutant caught |
| C4 Docs and skills | guide §6 rewritten as the pipeline; skills with model tiers | CI skill check |
| D Pilot | LS3 through the pipeline; B58 as the negative test; the benchmark | Zero C# for the LS3, or a documented failure |
| E Gate | full verification, measured authoring cost, debt classes, next milestone | `car-sim-project-gate` |

Risks:
- **Loader expansion complexity.** Mitigated by expanding into plain definitions, equality tests and fingerprint
  identity.
- **Provenance burden.** Coverage is a warning, and a gate only for real base engines.
- **Fingerprint runtime growth.** The light profile is budgeted (measure CPU-s per engine; shard if needed).
- **New real engines will miss their references systematically** (the K20-fitted constants). This is expected,
  classified and documented; never corrected per engine.
- **Small-model authoring errors.** Caught by the check; escalation rules are in the skill.

---

## 15. Findings outside the factory's scope (recorded, not acted on)

| # | Finding | Class | Note |
|---|---|---|---|
| F1 | Intake Gas Dynamics 2.0 Phase 1 is merged to `main` (PR #7, `af0b289`), but PHASE1_REVIEW.md says "not merged" and ROADMAP.md said "at its gate, awaiting review" | C (docs) | ROADMAP's status line is corrected with this audit. The review's owner decisions §5.1–5.3 (anchor margin, T35, M54 flow data) are not recorded as taken anywhere in the repository. They stay open and are not touched |
| F2 | Several real hardware features are prose-only simplifications (M54 exhaust VANOS, hot-film MAF, returnless fuel system, map thermostat, dual-mass flywheel) | B | Becomes machine-readable with declared features (§9.4) |
| F3 | `carsim validate` does not run the assembly validator on stock builds | C for the factory | B2 |
| F4 | The K20's intake runner diameter is a model default (A5), and the anchor is sensitive to it (Phase 1 review §1) | B | A `defaulted` provenance entry makes this visible. Not changed here |

---

## Appendix: evidence

```
git diff --numstat 0970bbe^1 0970bbe            # PR #4 (M54) by kind: json +392, src +409, tests +1227, md +373, tools +115, game +44
git show --stat 6199b8c                          # the M54 content commit: 9 files, +380
git show --stat 57f5b07                          # the synthetic matrix commit: 26 files, +4287
python3 (section and slot-line counts of content/test/engine-matrix/*.json, table cells per tune)
wc -l tests/CarSim.Core.Tests/SecondFamily/M54ReferenceTests.cs   # 437
grep -n "syn_\|isar_m54\|kestrel_k20" tools/CarSim.Verification/Fingerprint/FingerprintMatrix.cs
sed -n '/private static int Validate/,/^    }/p' tools/CarSim.Cli/Program.cs    # load only
```
