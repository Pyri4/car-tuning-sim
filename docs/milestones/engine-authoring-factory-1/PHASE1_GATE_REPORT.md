# Engine Authoring Factory 1.0 — Phase 1 gate report: content schema and provenance

**Status:** Phase 1 is implemented and stops here for the owner's review. Phase 2 (`check-engine`), tune generation,
data-driven verification and the LS3 pilot are **not started**. Nothing is merged and no PR is open.

**Branch:** `claude/busy-curie-dx4vz2`. It is stacked on the unmerged audit commits `3e4e994` and `11a1198` (the
strategy reset, the audit and the milestone definition), which sit on `main` at `af0b289`. Merge those first, or merge
the branch as a whole.

**Scope held:**
- no simulation, damage, ECU or gameplay code changed;
- no K20 or M54 parameter, tune, tolerance or Intake Gas Dynamics value changed;
- no fingerprint re-baseline;
- no second content format, no GUI.

## 1. Schema changes
All new fields are optional. The loader still rejects unknown fields, so a typo in a new field is an error like any
other.

| Where | Field | Meaning |
|---|---|---|
| file | `sources` (new document kind) | `{id, title, type, publisher?, url?, accessed?, license_note?, notes?}`. Types: manufacturer, service_documentation, reference_work, press, enthusiast_measurement, retailer, project_document |
| part | `provenance` | Map from a spec field name (or `mass_kg`, or `*` for every authored field without its own record) to a record `{type, source?, method?, target?, confidence?, notes?}` |
| part | `extends` | The part this one is a variant of |
| engine | `identity` | `{kind: real/fictional/synthetic, manufacturer?, family?, variant?, years?, market?, reference?, features[]}`; a feature is `{feature, approximation?, notes?}` |
| engine | `provenance` | Records for the architecture fields (`cylinders`, `layout`, `banks`, `bank_angle_deg`, `firing_order`) |
| engine | `extends`, `abstract` | Variants of a family; abstract family bases |

Code:
- **new** `src/CarSim.Core/Content/Provenance.cs`: `ProvenanceTypes`, `ValueProvenance`, `SourceDefinition`,
  `ProvenanceFields`;
- **new** `src/CarSim.Core/Engines/EngineFeatures.cs`: the feature vocabulary, `EngineIdentity`, `DeclaredFeature`,
  `FeatureReport`;
- `ContentLoader`: sources, provenance parsing and validation, part and engine variants;
- `ContentDatabase`: `Sources`, `AbstractEngines`;
- `EngineDefinition`: `Identity`, `Extends`, `Provenance`;
- `PartDefinition`: `Extends`, `AuthoredSpecFields`, `Provenance`;
- CLI `inspect`: identity, feature statuses, provenance coverage.

Design choice: **provenance sits beside the value, not around it.** The illustrative `{"bore_mm": {"value": …,
"provenance": …}}` would have changed the schema of every spec and every reader, for no simulation benefit. With a
map keyed by field name:
- the value keeps its unit-suffixed field (the repository's unit convention);
- specs, saves and the physics are untouched;
- `*` keeps sourcing a whole spec sheet to one line.

## 2. Migration and backwards compatibility
- **No migration is needed.** Old content has none of the new fields and loads exactly as before (tested). Saves
  store ids and slot ids, so they are unaffected (the existing save/load tests pass unchanged).
- **Plain parts and engines take the same parse path as before.** Variants are resolved after every layer, from the
  parent's document with the variant's fields over it, into the same `PartDefinition`/`EngineDefinition` types. The
  simulation cannot tell a variant from a hand-written copy, and a test shows the M54 and an `extends` copy give the
  same peak torque.
- **Engine load order is preserved**, including when a mod overrides an engine (tested); the CLI and game depend on it.
- **Mods:**
  - a mod that redefines a parent reaches its variants (tested);
  - a mod may replace a plain definition with a variant, or the reverse;
  - duplicates within a layer are still errors.
- One behaviour change, in content only: `syn.v6tt.exhaust_manifold.right` now `extends` the left manifold instead of
  repeating it. The dictionary position of that part moved; nothing depends on it (fingerprint identical).

## 3. Provenance
- Types: `published`, `measured`, `secondary`, `converted`, `derived`, `estimated`, `fitted`. They are words, not
  letters, because A–D already name debt and constant classes.
- Tune tables are "calibrated" and stay recorded by the tune manifest; they are not a content provenance type.
- **Validation** (load errors):
  - unknown type;
  - missing `source` for published, measured, secondary and converted;
  - missing `method` for derived and converted;
  - missing `target` for fitted, or a `target` on anything else;
  - bad `confidence`;
  - unknown source id (checked after every layer);
  - a key that is not a spec field of that category (or `mass_kg` / `*`);
  - malformed or null records, and unknown fields inside a record.
- **Authored vs defaulted:**
  - `*` expands only over fields the content states; a field left to its schema default gets no record;
  - `PartDefinition.AuthoredSpecFields` lists the stated fields, so later tooling can report defaults (the defaulted
    list itself is Phase 2).
- **Authored vs derived:**
  - derived quantities (displacement, compression ratio, clearance volume, runner tuned speeds) are not fields: the
    model computes them;
  - a `derived` record is for an authored input the author computed by a named method.

  This boundary is documented in PARTS_DATABASE.md and the guide; regenerating such inputs is later work.
- **Existing content: nothing was invented.**
  - **M54:** records only where PARTS_DATABASE.md states an origin. Six sources in
    `content/base/sources/bmw_references.json`. On the stock build, 53 of 98 authored spec values have a record:
    published 5, secondary 18, converted 4, derived 2, estimated 26 (these counts include 2 mass records outside the
    53).
  - Values with no recorded origin have no record: the lobe separation, the gasket's fire-ring bore, the compression
    height, the sump's surge limit, and some ratings.
  - **Discrepancy found:** PARTS_DATABASE's "published, used exactly" row includes values that its own text attributes
    to enthusiast sources (rods, pins, journals, deck height, cam lifts). The records mark those `secondary`, and the doc
    now says so.
  - Two masses (rod 575 g, piston 390 g) differ from the measured figures the doc cites (602 g with bearing, 313 g
    with rings), and the repository does not say why. They are `estimated`, with that note.
  - K20: fictional, no records (design values). The aftermarket (Featherline) parts and the synthetic matrix: none.
    **No `fitted` value exists in the content.**

## 4. Families and variants
- An engine definition may `extends` another:
  - `stock_parts` and `identity` merge entry by entry;
  - `features` replaces the whole list;
  - every other field is replaced (`slots` and `banks` whole);
  - `abstract` is never inherited.
- A definition without `extends` is its own family (K20, M54).
- An `abstract` base:
  - needs no slots or stock build;
  - is checked for topology when it has slots;
  - lands in `ContentDatabase.AbstractEngines`, not `Engines`, so the game, the CLI and the matrix tests never offer
    it.
- Provenance: a variant inherits its parent's record for an architecture field it does not restate. A restated field
  has no record unless the variant gives one.
- Errors: unknown parent, cycles, a parent that failed to load, plus every existing check on the merged result
  (e.g. an unknown stock part, a bank partition).
- Deliberately not built: multiple inheritance, partial slot or bank merges, slot layouts (`slot_layout`, still
  proposed; this phase did not need it).

## 5. Unsupported capabilities
- `EngineFeatures.All` is a fixed vocabulary in code, next to `EngineCapabilities`: the single statement of what the
  physics models.
  - Modelled: `intake_cam_phasing`, `variable_valve_lift_two_stage`, `variable_intake`, `turbocharger`,
    `twin_turbo_parallel`, `intercooler`.
  - Not modelled: `exhaust_cam_phasing`, `variable_valve_lift_continuous`, `direct_injection`,
    `port_and_direct_injection`, `twin_scroll_turbine`, `sequential_turbos`, `variable_geometry_turbine`,
    `supercharger`, `dry_sump`, `individual_throttles`, `mass_air_flow_metering`, `returnless_fuel_system`,
    `map_controlled_thermostat`, `dual_mass_flywheel`, `cylinder_deactivation`, `variable_oil_pump`.
- Declaring an unmodelled feature **requires** an `approximation` (what stands in, or `"omitted"`). An unknown or
  repeated feature is an error.
- `FeatureReport.For(identity, capabilities)` gives each feature one of four statuses (the brief's
  SUPPORTED / NOT MODELED / MISSING DATA / INVALID; "invalid" is a load error):

  | Status | Meaning |
  |---|---|
  | supported | modelled, and the parts provide it |
  | not modelled | not simulated; the approximation stands in |
  | missing data | modelled, but no installed part provides it |
  | undeclared | the parts provide it but the identity does not list it (information) |
- The M54 declares seven features. Two are supported (intake VANOS, DISA). Five are not modelled: exhaust VANOS,
  hot-film MAF, returnless fuel, map thermostat, dual-mass flywheel, each with the stand-in its PARTS_DATABASE
  simplification already described.
- A B58-like declaration (DI, Valvetronic, twin scroll) on an M54 copy is accepted, reported as not modelled, and
  **simulates identically** to the M54 (tested). Declaring hardware changes nothing the physics reads.

## 6. Part-model changes
- Parts already exist independently of engines: they fit through interfaces, and universal parts were already in use.
  The only addition is **part variants** (`extends`):
  - `spec` merges field by field;
  - other fields are replaced;
  - the variant keeps the parent's category and must name itself;
  - provenance is inherited only for unchanged values (a changed value never carries its parent's source).
- A load-time rule "every required interface key is provided by some part" was prototyped and **withdrawn**: two
  existing tests load parts in isolation on purpose, and the rule belongs to Phase 2's `check-engine` (as a warning),
  not to schema validation.

## 7. Tests added
`tests/CarSim.Core.Tests/Content/AuthoringSchemaTests.cs`, 45 cases:
- **provenance:**
  - expansion over authored fields only;
  - defaulted fields get no record;
  - 11 malformed-record cases and 2 malformed-source cases;
  - deterministic JSON round trip of records and sources, and identical output from two loads;
  - the M54's records claim no more than the repository knows (no fitted values; unknown origins have no record);
- **identity and features:**
  - the M54's statuses;
  - missing data and undeclared;
  - 6 invalid identities;
  - an unmodelled declaration simulates identically;
- **engine variants:**
  - override of one stock part (and the physics follows it);
  - an empty variant is the same engine;
  - abstract families;
  - 5 impossible variants;
  - a mod redefining a family reaches its variants, and load order is kept;
- **part variants:**
  - the matrix variant equals its parent under its own id and name;
  - selective provenance inheritance;
  - 6 impossible variants;
- **backwards compatibility:** content without the new fields;
- **physics boundary:** a source audit that the simulation, ECU, damage, dyno, vehicle and gameplay code never read
  identity, provenance, `Extends`, `AuthoredSpecFields`, `FeatureReport`, `EngineFeatures` or `SourceDefinition`.

Mutation harness, 4 new entries, each caught:
- `provenance-any-type`;
- `part-variant-inherits-changed-provenance`;
- `engine-variant-replaces-stock-parts`;
- `unmodelled-feature-without-approximation`.

## 8. Regression results (final tree)

| Check | Result |
|---|---|
| `dotnet build CarTuningSim.sln -c Release` (includes the Godot C# project) | 0 warnings, 0 errors |
| `dotnet test CarTuningSim.sln -c Release` | **728 passed, 0 failed, 0 skipped** (683 before; +45) |
| Regression fingerprint (`carsim fingerprint`, all 35 cases, 100 sections) | **IDENTICAL**: every digested value bit-for-bit the baseline's; no re-baseline |
| `carsim validate` (base) / `--mods content/test` / `--mods docs/example-mod` | OK / OK / OK |
| Mutation harness, all 35 entries | see §8.1 |
| Skills consistency (the CI check: sources ↔ `.agents/skills`, links, lock) | OK (`car-sim-add-engine` updated and reinstalled) |
| Godot headless smoke tests | **not run**: Godot is not installed in this container. No game, UI or simulation code changed, and the game project compiles |

The first full test run failed 4 existing tests. Each was root-caused, and none was changed to pass:
- two failed on the withdrawn interface rule (§6);
- two failed because the M54-cloning identity tests copy every base file whose path contains "m54", and the new
  sources file was named `m54_references.json`. It became `bmw_references.json`: a citation registry is shared, not
  engine content, so a clone must not duplicate it.

### 8.1 Mutation harness
Full run on the final tree: **35 of 35 mutants caught**. That is the 31 existing ones (unchanged verdicts) and the 4
new ones:

| Mutant | Guarding tests failed |
|---|---|
| provenance-any-type | 1 |
| part-variant-inherits-changed-provenance | 1 |
| engine-variant-replaces-stock-parts | 2 |
| unmodelled-feature-without-approximation | 1 |

After the harness restored and rebuilt the sources, `carsim fingerprint` was IDENTICAL again (35 cases, 100 sections).

## 9. Documentation changes
- PARTS_DATABASE.md:
  - part variants; provenance and sources (the table of types and rules); authored vs derived;
  - engine `identity`, `extends` and `abstract`; declared features and their statuses;
  - the M54 provenance mapping note; content strategy.
- ENGINE_AUTHORING_GUIDE.md: vocabulary (family, variant, identity, provenance); §6 step 1 (machine-readable
  provenance, no invented sources, authored vs derived, declared features); step 4 (variants).
- ARCHITECTURE.md: §5 authoring schema; a decision-log entry (why beside-the-value provenance and loader-resolved
  variants).
- AGENTS.md: the provenance rule now points at the `provenance` maps and declared features; the milestone is
  authorized phase by phase.
- GAME_VISION.md, README.md, ROADMAP.md, docs/VERIFICATION.md (35 mutants), the milestone's status, and the
  `car-sim-add-engine` skill (available vs not-yet fields, and model tiers for the new decisions).

## 10. Remaining limitations (for Phase 2 and later)
1. **Coverage is reported, not enforced.** `inspect` prints provenance coverage, but nothing yet requires records
   for a real engine. `check-engine --strict` is Phase 2.
2. **Tunes and scenarios carry no provenance.** The tune manifest records calibration; published reference figures
   (rated power, rev limit) have no machine-readable home yet (Phase 2 verification profiles).
3. **The defaulted list is not reported yet,** though `AuthoredSpecFields` makes it computable.
4. **No slot layouts:** V/flat slot boilerplate remains.
5. **Structural limits of the merge rules:** variants replace slots and banks whole, identity features replace as a
   list, and provenance keys name top-level spec fields only (`high_lift_profile` or `switched_stages` are covered
   as a whole, not per sub-field).
6. **Some M54 values stay unspecified.** Their origin is not in the repository, and resolving them needs the
   sources re-read (T3 decision, or owner-provided data).
7. **The feature vocabulary is code.** A new feature name needs a code line, deliberately: whether the physics models
   it is a fact about the code.
8. **Godot smoke tests** were not run here (CI runs them).

## Model routing (as practised)
This session ran on a single model; no subagents were spawned. Classified by the AGENTS.md tiers:
- **T3 (strongest):** the schema architecture, the variant semantics, the provenance-inheritance rule, withdrawing
  the interface rule, and the M54 provenance classification (interpreting source evidence).
- **T1 (small):** the repetitive edits (nine matrix identities, provenance entry, docs), which were mechanical
  scripts under that classification.

Model efficiency: the T1 work was small enough that delegating it would have cost more than it saved. In Phase 2 the
pilot's data entry is the natural first delegation.
