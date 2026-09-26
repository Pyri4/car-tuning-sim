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
Requirements: .NET SDK 8.0.

```
dotnet build CarTuningSim.sln
dotnet test CarTuningSim.sln
```

## Current status
Early implementation. See ROADMAP.md for what exists and what is next.
