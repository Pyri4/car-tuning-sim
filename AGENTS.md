# Car Tuning Simulator — Agent Instructions

## Read this first: the North Star
**[GAME_VISION.md](GAME_VISION.md) is the canonical statement of what this project is.** In short:

A deep automotive building, repair, tuning and simulation game inspired by Car Mechanic Simulator, Street Legal Racing:
Redline, Automation and BeamNG.drive — with more emphasis than any of them on mechanical construction, tuning,
diagnostics, part compatibility, realistic failure, the dyno and telemetry, rebuilding and experimentation.

Long-term loop:
BUY → INSPECT → DISASSEMBLE → DIAGNOSE → REPAIR → BUILD → MODIFY → SWAP → TUNE → DYNO → DRIVE → BREAK → DIAGNOSE → REBUILD

Scale goal: hundreds of engine families, thousands of parts, many cars, engine swaps.
**Adding the 100th engine should be almost as easy as adding the 2nd.**

Mechanical depth and tuning are more important than arcade racing. You do not need the user to re-explain any of
this; the repository holds it.

## Development principles
- Prefer modular, data-driven systems over hardcoded vehicle-specific logic.
- Avoid fake "Stage 1/2/3" upgrades as the primary progression mechanic.
- Mechanical changes must have simulation consequences.
- Components have specifications, compatibility constraints, operating limits, wear, and failure conditions.
- Simulation-critical code requires automated tests.
- Preserve working functionality unless a change is intentional and documented.
- Before large implementation work, inspect the architecture and existing systems.
- Keep systems independently testable.
- Document major architectural decisions.
- Do not generate large quantities of placeholder code merely to appear complete.

## Engine architecture rules
Full reference: [ENGINE_AUTHORING_GUIDE.md](ENGINE_AUTHORING_GUIDE.md).
- **Adding an engine is a content/data task**: an architecture (banks, slots), parts with specs and interfaces, and a
  tune calibrated with the dev calibrators. Follow the guide's procedure (§6).
- **Never branch on identity** in simulation, damage, ECU or gameplay code: no `engine.Id ==`, `part.Id ==`,
  family names, cylinder-count or layout special cases. `EngineAgnosticTests` audits `src/` for them.
- The model is **generic physics + generic capabilities + part data + calibration**. A missing physical feature becomes a
  reusable capability (guide §7), driven by part data, tested, and bit-identical for engines without the hardware.
- **Do not re-fit shared model constants to make one engine match its reference.** Document the miss and classify it.
- Parts fit through **interfaces** (`provides`/`requires`, bank-aware); cars and engines fit through interfaces too
  (bellhousing today). Never tie a car to an engine family or a part to a list of engines.
- Bank-scoped categories (head, gasket, springs, cams, intake, throttle, exhaust manifold, exhaust, turbo, intercooler)
  may have several parts: read them per bank (`PartFor`, `PartsOf`, `BankConfiguration`), never "the first one".
  Where the model deliberately has one of something (the MAP sensor, the knock retard), say so in a comment.
- **Regression content vs architecture tests:** the Kestrel K20 and Isar M54 pin behaviour (single-bank output must stay
  bit-identical unless a documented generic correction changes it); the synthetic matrix in
  `content/test/engine-matrix/` proves architectures and must stay content-only.
- Record structural findings in `docs/ENGINE_ARCHITECTURE_AUDIT.md`.

## Prototype scope (complete)
The first playable prototype exists: two cars, two engine families, one garage, one dyno, one test track, and the
required loop (inspect, remove engine, disassemble, replace components, reassemble, start, tune ECU, dyno, drive,
trigger mechanical failure through bad parts/build/tuning). See ROADMAP.md for what is next.

## Desired simulation areas
- Engine assembly and internal components
- Fuel and ignition
- Forced induction
- Cooling and lubrication
- ECU tuning
- Transmission and gearing
- Differential
- Suspension and alignment
- Brakes and tires
- Vehicle damage and wear
- Dyno and telemetry
- Garage/economy
- Save/load
- Modding/data definitions

## Agent workflow
When starting a task:
1. Read GAME_VISION.md, README.md, GAME_DESIGN.md, ARCHITECTURE.md, ROADMAP.md, and the relevant system docs
   (ENGINE_AUTHORING_GUIDE.md for anything touching engines or parts; SIMULATION_SPEC.md; PARTS_DATABASE.md).
2. Identify affected systems and dependencies.
3. Make the smallest coherent implementation.
4. Add/update tests.
5. Run available tests/build checks:
   - `dotnet build CarTuningSim.sln` and `dotnet test CarTuningSim.sln`;
   - `dotnet run --project tools/CarSim.Cli -- validate --mods content/test` (base content plus the synthetic matrix);
   - for engine or UI changes, the Godot smoke tests (`godot --headless --path game -- --smoke-test [--scenario=<id>]`,
     and `--drive --smoke-test`; `CARSIM_MODS_DIR=<repo>/content/test` loads the synthetic engines).
6. Summarize changed files, validation, and remaining risks.

The project's agent skills (below) package steps 5–6 (`car-sim-verify`), engine work (`car-sim-add-engine`) and the
end-of-milestone gate (`car-sim-project-gate`); use them when they apply.

Architecture work comes before content scale: do not add hundreds of parts, an open world, multiplayer or unrelated UI
before the systems underneath can carry them.

## Choosing and running milestones
- Choose work by long-term value, not by ease: prefer what improves every engine or unlocks many future systems (a
  generic intake model over another engine; a capability over a one-off feature). ROADMAP.md "Next recommended tasks"
  is the ordered list; milestone definitions live in `docs/milestones/`.
- A milestone starts only when the owner authorizes it. A milestone marked *proposed* is not authorized.
- Every milestone ends with a **project gate**: re-verify from scratch (don't trust earlier reports), review the
  architecture and docs for contradictions, classify remaining debt (A generic / B documented limitation / C must fix
  before the next milestone / D future), recommend a merge order, and define — not start — the next milestone.
- Do not chase an isolated dyno number, add engines ahead of the capabilities they need, or put UI ahead of the
  simulation architecture. Validation (tests, CLI, Godot smoke tests, CI) is part of the work, never optional.
- Branches may be stacked on unmerged PRs; say so in the PR, and merge in dependency order.

## Agent skills
Skills are managed with the open-source skills CLI (`npx skills`, https://github.com/vercel-labs/skills; needs Node.js).
- **Installed:** `car-sim-verify`, `car-sim-add-engine`, `car-sim-project-gate` (this project's own procedures) and
  `find-skills` (from `vercel-labs/skills`: discovering skills on skills.sh). Canonical copies live in
  `.agents/skills/` (read by Codex and other agents); `.claude/skills/` holds symlinks for Claude Code;
  `skills-lock.json` pins each skill's source and content hash.
- **Edit a project skill** in its source, `tools/agent-skills/<name>/SKILL.md`, then reinstall:
  `DISABLE_TELEMETRY=1 npx skills add ./tools/agent-skills -a claude-code -a codex -y`. Never edit `.agents/skills/`
  directly: CI fails when an installed copy differs from its source. New project skill: `npx skills init <name>` inside
  `tools/agent-skills/`. Skills point at the canonical docs (this file, ENGINE_AUTHORING_GUIDE.md, …) instead of
  copying them; when a procedure changes, change the doc and the skill together.
- **Third-party skills run with full agent permissions.** Install one only when the owner asks for it or approves it,
  after reading its SKILL.md and any scripts it ships; record why in the commit. Prefer skills from known sources.
- Telemetry: the CLI reports installs to its maintainers unless `DISABLE_TELEMETRY=1` (or `DO_NOT_TRACK=1`) is set;
  it is off automatically in CI.
- Windows checkouts without symlink support: reinstall with `--copy`, or run `npx skills experimental_install` (it also
  refreshes `find-skills` from upstream).

