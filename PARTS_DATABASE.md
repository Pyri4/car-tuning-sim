# Parts Database

## Purpose
Define the canonical data model for mechanical parts before creating large content sets.

## Generic part fields
- id
- display_name
- category
- manufacturer/tier
- mass
- price
- dimensions
- compatibility_tags
- operating_limits
- durability
- performance_effects
- dependencies
- installation_requirements

## Engine component examples
### Piston
- bore compatibility
- compression contribution
- mass
- max RPM
- temperature limit
- strength

### Connecting rod
- length
- mass
- strength
- RPM limit
- compatible piston/crank geometry

### Crankshaft
- stroke
- journal specs
- mass
- balance factor
- RPM limit
- torque limit

### Camshaft
- duration
- lift
- lobe separation
- RPM range
- overlap

### Turbocharger
- compressor map reference
- turbine characteristics
- inertia
- wastegate
- pressure ratio limits

### Injector
- flow rate
- impedance
- fuel compatibility
- max duty cycle

## Compatibility
Compatibility must be explicit and validated by the simulation/core domain layer.

## Content strategy
Start with one coherent engine family. Expand only after the component schema and tooling are stable.
