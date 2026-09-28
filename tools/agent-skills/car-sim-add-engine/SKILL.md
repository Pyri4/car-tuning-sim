---
name: car-sim-add-engine
description: Add a new engine family (real or fictional) to car-tuning-sim as content — architecture, parts with interfaces, a calibrated tune, a car or scenario — and decide when a missing physical feature needs a generic capability instead. Use when asked to add, author, model or port an engine (e.g. "add an LS3", "make a V12", "author an engine from this spec sheet"), to add engine parts, or when an engine cannot be expressed with the existing data.
---

# Add an engine (car-tuning-sim)

The rule: **adding an engine is a content/data task.** Simulation code never learns an engine's name. The canonical
reference is `ENGINE_AUTHORING_GUIDE.md` — read §1–§6 before authoring and §8 before finishing; this skill is the
checklist, not a replacement.

## Before starting
- Check ROADMAP.md: engines are not added ahead of the capabilities they need (AGENTS.md, "Choosing and running
  milestones"). If the owner has not asked for this engine, say so instead of adding it.
- Check the architecture fits (guide §2, §5, §9). Examples: an inline engine has one bank today (no inline parallel
  twin turbos yet); superchargers, direct injection, dry sumps and exhaust cam phasing are not modelled. If the engine
  needs one of these, stop and propose a capability (guide §7) — do not approximate it with fake specs.

## Steps (guide §6)
1. **Reference data** with provenance for every value (published / measured / derived / converted / estimated /
   calibrated), as PARTS_DATABASE.md does for the M54. Author volumes; never author compression ratio.
2. **Architecture**: layout, banks (ids + cylinder numbers), bank angle, firing order; which bank-scoped parts are per
   bank and which are shared (plenum, turbo, exhaust) — that choice *is* the air-path topology.
3. **Parts** with specs and **interfaces** (`provides`/`requires`: deck, flanges, bellhousing, cam tunnel…). Reuse
   universal parts (injectors, ECUs, fuel pumps, turbos) where they physically fit.
4. **Family**: slots (`banks`, `install_after`), `stock_parts`, `stock_tune`.
5. **Validate**: `dotnet run --project tools/CarSim.Cli -c Release -- validate [--mods <dir>]` and
   `inspect <engine-id>`; fix every error, read every warning (valve float, coil bind, quench, compression, interfaces).
6. **Calibrate the base tune with the dev calibrators** — never hand-fit a curve: `calibrate-cams` (only with a
   phaser) → `calibrate-ve` → `calibrate-spark` → `calibrate-ve`, each with `--fuel <fuel>`; paste the printed tables;
   say in the tune's `description` how it was made, and add the recipe to `tools/CarSim.Verification/tune-manifest.json`
   (a test requires one per tune; `carsim regenerate-tunes --tune <id>` then reproduces it).
7. **Measure**: `sweep <engine-id> --fuel <fuel>`. For a real engine, compare against bands stated in advance. If it
   misses, classify why (content? missing capability? shared model simplification?) and document it.
   **Never change a shared model constant to hit one engine's numbers.**
8. **Car**: a bellhousing interface on the block; `drive <engine-id> --vehicle <car>`; a scenario for the game.
9. **Tests**: reference tests for a real engine (like `M54ReferenceTests`); the whole suite must stay green —
   including `EngineAgnosticTests` (source audit) and the regression fingerprint (the K20/M54 pins). A real engine joins
   the fingerprint matrix; re-baseline with `carsim fingerprint --write tests/baselines/fingerprint.txt`.
10. Run the `car-sim-verify` checklist (Godot smoke test with the new scenario included).

## Never (guide §8)
- `if (engine.Id == …)`, `if (part.Id == …)`, cylinder-count or layout branches anywhere in `src/`.
- Per-engine constants, or "effective" specs that lie about the part to make a curve fit.
- Reading "the" part of a bank-scoped category (`FindByCategory`) — use `PartFor` / `PartsOf` / `BankConfiguration`.
- Hand-editing a calibrated table silently, or tying a car to an engine family instead of an interface.

## If the engine needs code (guide §7)
Write the physics down first and name the engines that have the feature. Add it as a capability: spec fields with
units and validation, ECU output and tune field if needed, bank-aware physics, bit-identical for engines without the
hardware, capability summary, CLI/UI exposure, tests including a mutation check, a synthetic matrix engine using it,
and the docs (guide §5, SIMULATION_SPEC.md, PARTS_DATABASE.md, the ARCHITECTURE.md decision log).
