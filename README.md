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
```

Development aids for the game (arguments after `--`): `--tab=dyno`, `--autorun`, `--select=pistons`,
`--screenshot=out.png --frames=30`, `--smoke-test` (headless CI check).

## Current status
Early implementation. See ROADMAP.md for what exists and what is next.
