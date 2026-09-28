# Game Vision — the North Star

This is the canonical statement of what this project is for. Every other document (design, architecture, simulation,
parts, roadmap) serves it. Read it first; if a change works against it, stop and say so.

## The game
A deep automotive **building, repair, tuning and simulation** game, inspired by

- **Car Mechanic Simulator** — the workshop: taking cars apart, finding what is wrong, fixing it;
- **Street Legal Racing: Redline** — building and modifying your own car from parts, and living with the result;
- **Automation** — engines as engineering: architecture, geometry, cams, induction, calibration;
- **BeamNG.drive** — consequences that come from physics, not from scripts,

with substantially more emphasis than any of them on

- mechanical construction (engines and cars assembled from real subsystems, in a real order);
- tuning (ECU tables and hardware choices whose effects you can measure);
- diagnostics (symptoms, measurements, inspection — a failure always has a cause you can find);
- part compatibility (parts fit because their interfaces match, not because a list says so);
- realistic failure (parts break from measurable overstress, heat, starvation, fatigue — never from dice);
- the dyno and telemetry (every claim the game makes can be checked on an instrument);
- rebuilding and experimentation (the fun is in trying something, breaking it, understanding why, and trying again).

## The long-term loop
BUY → INSPECT → DISASSEMBLE → DIAGNOSE → REPAIR → BUILD → MODIFY → SWAP → TUNE → DYNO → DRIVE → BREAK → DIAGNOSE → REBUILD

The prototype implements the core of it (inspect, disassemble, replace, reassemble, start, tune, dyno, drive, break,
read the report, repair). Buying, a parts market, repairs by machining, customer jobs and full engine swaps come later
(ROADMAP.md).

## Scale goal
Hundreds of engine families, thousands of parts, many cars — and engine swaps between them. The engines differ in
every way real engines differ: inline, V and flat layouts; one, two or more banks; 1 to 16 cylinders; two to five
valves per cylinder; OHV, SOHC and DOHC valvetrains; cam phasing, variable valve lift, variable intake runners;
naturally aspirated, turbocharged, twin-turbocharged, (later) supercharged; one or several intake and exhaust paths;
different fuel, lubrication and cooling systems.

> **Adding the 100th engine should be almost as easy as adding the 2nd.**

That is the technical objective behind every architecture decision. It means:

- **Adding an engine is a content/data task.** A new engine is JSON: an architecture (banks, slots), parts with specs
  and interfaces, and a calibrated base tune. No simulation code knows any engine by name.
- **The simulation is generic physics + generic capabilities + engine/part data + calibration.** Never engine-specific
  physics code. If a real engine has a physical feature the model cannot represent (VANOS, VTEC, DISA, a second turbo),
  that feature becomes a *capability*: reusable, driven by part data, usable by every future engine.
- **Parts are compatible through interfaces** (mounting patterns, flanges, bellhousings, bores, valvetrain type…),
  so a part can fit any engine or car it physically fits, and engine swaps stay possible.
- **Real engines are regression content, synthetic engines are architecture tests.** The Kestrel K20 and the Isar M54
  (a real BMW M54B30 reference) pin the model's behaviour; the synthetic engine matrix (`content/test/engine-matrix/`:
  V6, V8, flat-four, twin turbo, pushrod, VVL, variable intake…) proves that architectures the code was never written
  for run through the whole pipeline as data.

## Content scale is a first-class goal (2026-09-28)
Whether the simulator *can* represent different engines is answered: it can, as data, for every architecture in the
synthetic matrix. The question now is how cheaply and how honestly it can represent **many** of them. The project
optimizes for

**correctness + genericity + authoring speed + provenance + verifiability**,

not for raw simulation complexity or for one engine's accuracy. Concretely:
- **Physics is code, engines are content.** The physics answers "what does a runner do?"; the content answers "how long
  is this engine's runner?" An engine that needs code is missing a capability. The capability is added generically,
  once, for every engine (ENGINE_AUTHORING_GUIDE.md).
- **Adding an engine is a pipeline:** specify, source, author parts, validate, derive, generate the base tune, verify,
  dyno, load in the game. It is not a software project. The number that matters is how much unique code the next real
  engine needed, and the answer should be none.
- **Every number has a known origin** (published, measured, secondary, derived, estimated, fitted), recorded so
  that it can be checked. Today this is written in prose; a machine-readable form the tools can check is proposed.
- **Reference data is evidence, not a target.** When the model disagrees with a published curve, the disagreement is
  classified (content, missing capability, shared simplification), never patched.
- **Physics and content advance in parallel.** An engine whose real hardware the model lacks (direct injection, say)
  is authored with that feature declared as not modelled, not faked. Today it is declared as a documented
  simplification, as the M54's exhaust VANOS is.

The plan and its measurements: [docs/ENGINE_AUTHORING_FACTORY_AUDIT.md](docs/ENGINE_AUTHORING_FACTORY_AUDIT.md) and the
proposed [Engine Authoring Factory 1.0](docs/milestones/ENGINE_AUTHORING_FACTORY_1.md) milestone.

## Principles that do not change
1. **Mechanical changes have simulation consequences.** No "Stage 1/2/3" upgrades; a part changes the engine only
   through its specifications.
2. **No engine-, part- or car-specific branches in simulation code** (`if (engine.Id == "M54")` is forbidden — the
   test suite audits the source for it).
3. **Do not fake realism.** A model that is honestly simple and documented beats one tuned to match a curve. Generic
   constants are never re-fitted to make one engine look right.
4. **Failures are diagnosable.** Every failure produces a report: what broke, the measured cause, the rating it
   exceeded, and what would prevent it.
5. **The player can always answer:** what changed, why, what measurement proves it, what failed and why.
6. **Simulation-critical code has automated tests**, and new invariants are mutation-checked (shown to fail on the
   bug they guard).
7. **Architecture first, then content scale through tools.** Do not add hundreds of parts, an open world or
   multiplayer before the systems underneath — and, for content, the authoring pipeline — can carry them.
8. **Know where every number comes from.** A value is published, measured, derived, estimated or fitted, and it says
   which. A fitted or estimated value never passes for a published or measured one.

## Not goals (for now)
A massive open world, multiplayer, procedural traffic, an NPC economy, photorealistic graphics, perfect accuracy for
any single real engine.

## Where to go next
- [ENGINE_AUTHORING_GUIDE.md](ENGINE_AUTHORING_GUIDE.md) — how engines, parts, capabilities and swaps work, and the
  exact procedure for adding an engine (and what not to do).
- [GAME_DESIGN.md](GAME_DESIGN.md) — pillars, player fantasy, what the systems should feel like.
- [ARCHITECTURE.md](ARCHITECTURE.md) — technology, layers, data flow, testing strategy, decision log.
- [SIMULATION_SPEC.md](SIMULATION_SPEC.md) — the models and their equations, limits and calibration.
- [PARTS_DATABASE.md](PARTS_DATABASE.md) — the content schema.
- [ROADMAP.md](ROADMAP.md) — what exists, what is next, known issues and technical debt.
- [docs/ENGINE_ARCHITECTURE_AUDIT.md](docs/ENGINE_ARCHITECTURE_AUDIT.md) — every structural engine assumption found
  in the code, and what was done about it.
