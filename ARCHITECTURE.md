# Architecture

## Status
Decision pending. Codex should analyze candidate engines/frameworks before implementation.

## Architecture goals
- deterministic and testable core simulation
- clear separation between simulation and presentation
- data-driven parts and vehicle definitions
- save-compatible serialized state
- extensible modding model
- eventual support for multiple vehicles and engines

## Proposed boundaries
### Simulation core
Pure or mostly pure logic for:
- engine cycle approximation
- torque/power production
- temperatures
- pressures
- fuel/air state
- ignition
- ECU calculations
- drivetrain
- suspension state
- component wear/failure

### Domain/data layer
Definitions for:
- parts
- engines
- vehicles
- materials/specs
- compatibility
- prices
- service requirements

### Game layer
- garage
- UI
- inventory
- economy
- career
- save/load
- workshop interactions

### Driving/physics layer
Adapter around the chosen game engine's vehicle physics.

### Tools
Developer tooling for:
- part authoring
- dyno inspection
- telemetry
- simulation tests
- content validation

## Data philosophy
Parts and vehicles should be defined as data, not duplicated logic.

## Testing philosophy
Simulation math and compatibility rules should be testable without launching the game.
