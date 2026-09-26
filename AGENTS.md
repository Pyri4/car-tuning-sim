# Car Tuning Simulator — Agent Instructions

## Project vision
Build a deep car-building and tuning simulator inspired by Car Mechanic Simulator, Street Legal Racing: Redline, Automation, and BeamNG.drive.

Core loop:
BUY → INSPECT → REPAIR → BUILD → TUNE → DYNO → DRIVE → BREAK → REBUILD

Mechanical depth and tuning are more important than arcade racing.

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

## Prototype scope
First playable prototype:
- One car
- One engine
- One garage
- One dyno
- One test track

Required loop:
1. Inspect vehicle
2. Remove engine
3. Disassemble engine
4. Replace components
5. Reassemble engine
6. Start engine
7. Tune ECU
8. Run dyno
9. Drive
10. Trigger mechanical failure through bad parts/build/tuning

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
1. Read README.md, GAME_DESIGN.md, ARCHITECTURE.md, ROADMAP.md, and relevant system docs.
2. Identify affected systems and dependencies.
3. Make the smallest coherent implementation.
4. Add/update tests.
5. Run available tests/build checks.
6. Summarize changed files, validation, and remaining risks.

Do not begin broad gameplay implementation until the architecture document defines the engine/framework, simulation boundaries, data model, and test strategy.
