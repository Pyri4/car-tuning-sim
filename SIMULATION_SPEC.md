# Initial Simulation Specification

## Philosophy
The first model should be simple enough to validate but structurally capable of becoming deeper.

## Engine outputs
At minimum model:
- RPM
- torque
- power
- intake pressure
- boost
- air/fuel state
- ignition timing
- coolant temperature
- oil temperature
- oil pressure
- component stress/failure risk

## Relationships
Power should derive from the simulated torque curve:
P = T × omega

Use consistent SI units internally.

## Air and fuel
The model should estimate air mass flow from engine geometry, RPM, manifold pressure, volumetric efficiency and air density.

Fuel delivery should be constrained by injector capacity and fuel pressure.

## Combustion
The first model may use a simplified combustion efficiency representation rather than full CFD.

Ignition and mixture should influence:
- torque
- efficiency
- exhaust heat
- knock tendency

## Forced induction
Turbo/supercharger systems should expose:
- compressor capacity
- pressure ratio
- efficiency
- response/inertia
- wastegate/control behavior

The first version can use simplified maps.

## Thermal model
Heat generation should feed coolant/oil temperature states.
Cooling capacity should depend on radiator/cooling system capacity and airflow.

## Failure model
Failures should use thresholds plus accumulated damage/fatigue rather than pure random rolls.

Examples:
- knock damage
- thermal damage
- lubrication damage
- over-rev damage
- fatigue damage

## Validation
Every simulation change that affects outputs should have deterministic tests.
