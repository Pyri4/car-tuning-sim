# Car Tuning Simulator

A mechanically deep car-building and tuning simulator.

## Vision
Car Mechanic Simulator × Street Legal Racing: Redline × Automation × BeamNG.drive — with more emphasis on mechanical
construction, tuning, diagnostics, part compatibility, realistic failure, the dyno and telemetry, rebuilding and
experimentation. **[GAME_VISION.md](GAME_VISION.md) is the North Star.**

The main attraction is building and tuning cars where component choices have meaningful mechanical consequences. The
long-term scale is hundreds of engine families and thousands of parts, so engines are data: *adding the 100th engine
should be almost as easy as adding the 2nd* ([ENGINE_AUTHORING_GUIDE.md](ENGINE_AUTHORING_GUIDE.md)). Content scale is a
first-class goal: the project optimizes for correctness, genericity, authoring speed, provenance and verifiability
([docs/ENGINE_AUTHORING_FACTORY_AUDIT.md](docs/ENGINE_AUTHORING_FACTORY_AUDIT.md)).

## Gameplay loop
BUY → INSPECT → DISASSEMBLE → DIAGNOSE → REPAIR → BUILD → MODIFY → SWAP → TUNE → DYNO → DRIVE → BREAK → DIAGNOSE → REBUILD

## Prototype
The first milestone is intentionally small:
- one vehicle
- one engine
- garage interaction
- engine disassembly/assembly
- component replacement
- ECU tuning
- dyno
- drivable test track
- basic failure simulation

Two engine families ship: the fictional Kestrel K20 four (the prototype's engine) and the Isar M54 straight six, a
real engine (BMW M54B30, European E46 330i) authored purely as data to prove engine families are content — see
PARTS_DATABASE.md ("Isar M54 reference engine") and SIMULATION_SPEC.md ("Second engine family") for its sources and
how close the model gets. A synthetic engine matrix (`content/test/engine-matrix/`: V6, twin-turbo V6, pushrod and
DOHC V8s, a flat-four, VVL, variable intake) proves that other architectures run through the same code as data.

## Repository docs
- GAME_VISION.md — the North Star: what the game is, its scale goal and the principles that do not change
- AGENTS.md — persistent coding-agent instructions
- ENGINE_AUTHORING_GUIDE.md — engine architecture, parts and interfaces, capabilities, how to add an engine or a capability
- GAME_DESIGN.md — gameplay and simulation goals
- ARCHITECTURE.md — technical architecture decisions
- ROADMAP.md — staged development plan
- SIMULATION_SPEC.md — initial simulation model and equations
- PARTS_DATABASE.md — initial component/data schema
- docs/ENGINE_ARCHITECTURE_AUDIT.md — structural engine assumptions found in the code and what was done about each
- docs/VERIFICATION.md — the regression fingerprint, the tune-regeneration driver and the mutation harness
- docs/ENGINE_AUTHORING_FACTORY_AUDIT.md — what adding an engine costs today, what should become data or tooling, and the
  Engine Authoring Factory milestone
- docs/milestones/ — milestone definitions (the next one is proposed until the owner authorizes it)

Coding agents also get this project's procedures as agent skills (`.agents/skills/`, `.claude/skills/`, managed with
`npx skills`; sources in `tools/agent-skills/`). See AGENTS.md, "Agent skills".

## Technology
Godot 4.7 (.NET) for presentation; the mechanical simulation is a pure C# (.NET 8) library with no
engine dependency. See ARCHITECTURE.md for the decision record.

## Building and testing
Requirements: .NET SDK 8.0 (and Godot 4.7.2 .NET to run the game).

```
dotnet build CarTuningSim.sln      # core, gameplay, tests, CLI and the Godot C# project
dotnet test CarTuningSim.sln       # simulation, content, damage, dyno and gameplay tests
```

## Running
Game (Godot 4.7.2 .NET): open `game/project.godot` in the editor and press Play, or

```
godot --path game
```

Command-line tools (no Godot needed):

```
dotnet run --project tools/CarSim.Cli -- validate                      # check all content
dotnet run --project tools/CarSim.Cli -- inspect isar_m54              # architecture, stock build, geometry, compatibility
dotnet run --project tools/CarSim.Cli -- validate --mods content/test  # base content plus the synthetic engine matrix
dotnet run --project tools/CarSim.Cli -- sweep syn_v6_tt --mods content/test --fuel gasoline_98   # per-bank/per-turbo columns
dotnet run --project tools/CarSim.Cli -- drive syn_v8_ohv --mods content/test --vehicle isar_c30   # an engine swap
dotnet run --project tools/CarSim.Cli -- sweep kestrel_k20 --swap exhaust=exhaust.race_76mm
dotnet run --project tools/CarSim.Cli -- sweep isar_m54 --fuel gasoline_98
dotnet run --project tools/CarSim.Cli -- hold kestrel_k20 --rpm 6500 --sump-g 1.3  # abuse test: warnings, failure report, inspection
dotnet run --project tools/CarSim.Cli -- drive kestrel_k20 --laps 5 --wear clutch=0.4 [--chassis clutch=clutch.sport] [--trace 1]
                                                                       # autopilot laps: times, clutch/brake heat, wear, failures
dotnet run --project tools/CarSim.Cli -- calibrate-ve kestrel_k20 --swap camshafts=k20.cams.sport [--hold 1]
                                                                       # measure a build's VE table (base map for a tune file)
dotnet run --project tools/CarSim.Cli -- calibrate-cams isar_m54 --fuel gasoline_98   # cam-phaser base map
dotnet run --project tools/CarSim.Cli -- calibrate-spark isar_m54 --fuel gasoline_98  # spark base map (MBT / knock margins)
dotnet run --project tools/CarSim.Cli -- bench isar_m54                # step time and allocation, engine and car
```

### Driving
From the Garage tab, **Take it to the test track** (the engine must be in the car, runnable and not
seized; every chassis slot filled). Controls:

| Key | Action | Gamepad |
|---|---|---|
| W / ↑ | throttle | right trigger |
| S / ↓ | brake | left trigger |
| A D / ← → | steer (keyboard steering assist limits lock at speed) | left stick |
| E / Shift, Q / Ctrl | shift up / down (automatic clutch, manual gears) | RB / LB |
| Space | handbrake | A |
| T | starter (hold) | Y |
| R | recover to the track (voids the lap) | Back |
| P | autopilot (the built-in test driver) | |
| C | camera: chase, bumper, trackside | X |
| Esc | back to the garage (a failure takes you to Reports) | Start |

Missed shifts, over-revving, oil surge and overheating break parts exactly as on the dyno; the failure
report appears on track and in the Reports tab.

Development aids for the game (arguments after `--`): `--tab=dyno`, `--autorun`, `--select=pistons`,
`--screenshot=out.png --frames=30`, `--scenario=isar_c30_six` (start another scenario; the Garage tab's New game also
offers every scenario; with `CARSIM_MODS_DIR=<repo>/content/test` the synthetic engines' scenarios such as
`syn_v8_swap` too), `--smoke-test` (headless CI check); for the track: `--drive`
(start there), `--autodrive`, `--warp=20` (simulate 20 s ahead), `--camera=trackside`, and
`--drive --smoke-test` (headless drive check).

## Modding
Mods are folders of JSON under `content/mods/`, loaded after the base game; they can add parts,
engines, fuels, tunes, scenarios and cars, or redefine existing ones by id. See PARTS_DATABASE.md
("Mods") and the example in `docs/example-mod/`. New engine families are data: inline, V and flat layouts with any
number of banks, per-bank or shared intake and exhaust paths, any number of turbochargers, SOHC/DOHC/OHV valvetrains,
cam phasing, two-stage variable valve lift and variable intake runners (ENGINE_AUTHORING_GUIDE.md). New parts in
existing categories are data only; new categories and new capabilities need code. Superchargers, direct injection,
dry sumps and exhaust cam phasing are not modelled yet; families that need them are rejected at load with a reason.
The level-setting constants are still the K20's, which puts the M54 about 10 % under its published power
(SIMULATION_SPEC.md, "Second engine family").

## Current status
Early implementation. See ROADMAP.md for what exists and what is next.
