---
name: car-sim-add-engine
description: Add a new engine family or variant (real or fictional) to car-tuning-sim as content — architecture, parts with interfaces and provenance, a calibrated tune, a car or scenario — measure what it cost, and decide when a missing physical feature needs a generic capability instead. Use when asked to add, author, model or port an engine (e.g. "add an LS3", "make a V12", "author an engine from this spec sheet"), to add engine parts, or when an engine cannot be expressed with the existing data.
---

# Add an engine (car-tuning-sim)

The rule: **adding an engine is a content/data task.** Simulation code never learns an engine's name. The canonical
reference is `ENGINE_AUTHORING_GUIDE.md` — read §1–§6 before authoring and §8 before finishing; this skill is the
checklist, not a replacement. Content scale is a first-class goal (GAME_VISION.md): the measure of success is how
little unique work the engine needed — ideally zero code. The proposed Engine Authoring Factory
(docs/milestones/ENGINE_AUTHORING_FACTORY_1.md) will turn these steps into commands; until it lands, follow them as
written and do **not** use its proposed fields (`provenance`, `identity`, `extends`, `slot_layout`) — the loader rejects
unknown fields.

## Model tiers (AGENTS.md, "Model routing")
Each step names the lowest tier that can do it reliably: **T1** small, **T2** medium, **T3** strongest. A small model
gathers evidence and enters data; it never decides. **Escalate instead of guessing** — stop and hand the step to a
higher tier when you meet: missing or conflicting source data, an unknown interface or compatibility question, an
architecture or feature the model may not represent, ambiguous terminology, a check failure you cannot explain, or any
wish to change a constant, tolerance, test or regression baseline. Never invent a value.

## Before starting — T3 decides, T1 may gather
- Check ROADMAP.md: engines are not added ahead of the capabilities they need (AGENTS.md, "Choosing and running
  milestones"). If the owner has not asked for this engine, say so instead of adding it.
- Check the architecture fits (guide §2, §5, §9). Examples: an inline engine has one bank today (no inline parallel
  twin turbos yet); superchargers, direct injection, dry sumps and exhaust cam phasing are not modelled. If the engine
  needs one of these, stop and propose a capability (guide §7) — do not approximate it with fake specs. An engine may be
  authored with such hardware **listed as a simplification** (what stands in for it) only when the owner accepts the
  approximation.

## Steps (guide §6)
1. **Reference data** (T1 enters sourced values; T3 resolves conflicts between sources) with provenance for every value:
   measured / published / secondary / converted / derived / estimated / fitted / calibrated (guide §6 step 1), recorded
   as PARTS_DATABASE.md does for the M54. Never present an estimate or a fit as published. Author volumes; never author
   compression ratio. Real names stay in provenance; the game uses a fictional marque.
2. **Architecture** (T2; T3 if no synthetic matrix engine has this topology): layout, banks (ids + cylinder numbers),
   bank angle, firing order; which bank-scoped parts are per bank and which are shared (plenum, turbo, exhaust) — that
   choice *is* the air-path topology.
3. **Parts** (T1 fills specs following an existing part of the same category; T2 chooses interfaces) with specs and
   **interfaces** (`provides`/`requires`: deck, flanges, bellhousing, cam tunnel…). Reuse universal parts (injectors,
   ECUs, fuel pumps, turbos) where they physically fit.
4. **Family** (T1, copying the matrix's slot pattern): slots (`banks`, `install_after`), `stock_parts`, `stock_tune`.
5. **Validate** (T1 runs and fixes typos and structural errors; T2 interprets warnings; T3 for physical warnings you
   cannot explain): `dotnet run --project tools/CarSim.Cli -c Release -- validate [--mods <dir>]` and
   `inspect <engine-id>` — `validate` only loads; `inspect` runs the physical assembly rules. Fix every error, read every
   warning (valve float, coil bind, quench, compression, interfaces), and check by hand that the stock tune's
   `displacement_cc`, injector flow and dead time match the stock parts (not checked automatically yet).
6. **Calibrate the base tune with the dev calibrators** (T1 runs them in the guide's order) — never hand-fit a curve:
   hardware schedule first (`calibrate-cams` with a phaser; switch speeds for switched hardware), then `calibrate-ve` →
   `calibrate-spark` → `calibrate-ve`, each with `--fuel <fuel>`; paste the printed tables; say in the tune's
   `description` how it was made, and add the recipe to `tools/CarSim.Verification/tune-manifest.json` (a test requires
   one per tune; `carsim regenerate-tunes --tune <id>` then reproduces it). λ and boost targets are hand-authored and
   listed as such; a boost target comes from a source or the owner, never a guess.
7. **Measure** (T1 runs `sweep <engine-id> --fuel <fuel>`; **T3 classifies any miss**). For a real engine, compare
   against bands stated in advance. If it misses, classify why (content? missing capability? shared model
   simplification?) and document it. **Never change a shared model constant to hit one engine's numbers.**
8. **Car** (T1/T2): a bellhousing interface on the block; `drive <engine-id> --vehicle <car>`; a scenario for the game.
9. **Tests** (T2 writes them; **T3 decides any re-baseline**): reference tests for a real engine (like
   `M54ReferenceTests`); the whole suite must stay green — including `EngineAgnosticTests` (source audit) and the
   regression fingerprint (the K20/M54 pins). A real engine joins the fingerprint matrix; re-baseline with
   `carsim fingerprint --write tests/baselines/fingerprint.txt` only for new cases or a documented generic correction.
10. Run the `car-sim-verify` checklist (T1; Godot smoke test with the new scenario included).
11. **Report the authoring cost** (T1 prepares from `git diff --numstat` and the command log): files and lines by kind
    (data, generated, code, tests, docs), commands, validation iterations, hand edits, every line of code needed and
    why, and the tier each step used with every escalation (docs/milestones/ENGINE_AUTHORING_FACTORY_1.md,
    "Authoring-cost benchmark").

## Never (guide §8)
- `if (engine.Id == …)`, `if (part.Id == …)`, cylinder-count or layout branches anywhere in `src/`.
- Per-engine constants, correction curves, dyno lookup tables, or "effective" specs that lie about the part to make a
  curve fit. Reference curves are evidence, never targets.
- Reading "the" part of a bank-scoped category (`FindByCategory`) — use `PartFor` / `PartsOf` / `BankConfiguration`.
- Hand-editing a calibrated table silently, or tying a car to an engine family instead of an interface.
- Copying code, assets, data files or text from other games or mods; third-party data without an explicit licence.

## If the engine needs code (guide §7) — T3 designs, T2 implements
Write the physics down first and name the engines that have the feature. Add it as a capability: spec fields with
units and validation, ECU output and tune field if needed, bank-aware physics, bit-identical for engines without the
hardware, capability summary, CLI/UI exposure, tests including a mutation check, a synthetic matrix engine using it,
and the docs (guide §5, SIMULATION_SPEC.md, PARTS_DATABASE.md, the ARCHITECTURE.md decision log). Code needed outside
`src/` for one engine (tests, fingerprint cases, CI) is an authoring-pipeline gap: note it in the cost report.
