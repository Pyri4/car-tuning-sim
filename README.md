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
dotnet run --project tools/CarSim.Cli -- inspect                       # stock engine build, geometry, compatibility
dotnet run --project tools/CarSim.Cli -- sweep --swap exhaust=exhaust.race_76mm
dotnet run --project tools/CarSim.Cli -- hold --rpm 6500 --sump-g 1.3  # abuse test: warnings, failure report, inspection
dotnet run --project tools/CarSim.Cli -- drive --laps 5 --wear clutch=0.4 [--chassis clutch=clutch.sport] [--trace 1]
                                                                       # autopilot laps: times, clutch/brake heat, wear, failures
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
`--screenshot=out.png --frames=30`, `--smoke-test` (headless CI check); for the track: `--drive`
(start there), `--autodrive`, `--warp=20` (simulate 20 s ahead), `--camera=trackside`, and
`--drive --smoke-test` (headless drive check).

## Current status
Early implementation. See ROADMAP.md for what exists and what is next.
