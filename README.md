# Car Tuning Simulator

A mechanically deep car-building and tuning simulator.

## Vision
Car Mechanic Simulator × Street Legal Racing: Redline × Automation × BeamNG.drive.

The main attraction is building and tuning cars where component choices have meaningful mechanical consequences.

## Core gameplay loop
BUY → INSPECT → REPAIR → BUILD → TUNE → DYNO → DRIVE → BREAK → REBUILD

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
how close the model gets.

## Repository docs
- AGENTS.md — persistent coding-agent instructions
- GAME_DESIGN.md — gameplay and simulation goals
- ARCHITECTURE.md — technical architecture decisions
- ROADMAP.md — staged development plan
- SIMULATION_SPEC.md — initial simulation model and equations
- PARTS_DATABASE.md — initial component/data schema

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
dotnet run --project tools/CarSim.Cli -- inspect isar_m54              # stock engine build, geometry, compatibility
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
offers every scenario), `--smoke-test` (headless CI check); for the track: `--drive`
(start there), `--autodrive`, `--warp=20` (simulate 20 s ahead), `--camera=trackside`, and
`--drive --smoke-test` (headless drive check).

## Modding
Mods are folders of JSON under `content/mods/`, loaded after the base game; they can add parts,
engines, fuels, tunes, scenarios and cars, or redefine existing ones by id. See PARTS_DATABASE.md
("Mods") and the example in `docs/example-mod/`. Limits: new parts in existing categories are data only, new
categories need code, and an engine family must fit what the engine model represents (one part or set per
category — no twin turbos, per-bank air paths, superchargers or dry sumps yet); anything else is rejected at load
with a reason. A second, real engine family was added as data only (the one model abstraction it needed, cam timing,
is now data too); the level-setting constants are still the K20's, which puts the M54 about 10 % under its published
power (SIMULATION_SPEC.md, "Second engine family").

## Current status
Early implementation. See ROADMAP.md for what exists and what is next.
