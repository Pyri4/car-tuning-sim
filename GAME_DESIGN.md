# Game Design

What the game should feel like and which systems carry it. [GAME_VISION.md](GAME_VISION.md) is the North Star this
serves (the game, the long-term loop, the scale goal and the principles that do not change).

## Loop
BUY → INSPECT → DISASSEMBLE → DIAGNOSE → REPAIR → BUILD → MODIFY → SWAP → TUNE → DYNO → DRIVE → BREAK → DIAGNOSE → REBUILD

## Pillars
1. Mechanical authenticity
2. Experimentation
3. Build freedom
4. Cause-and-effect tuning
5. Satisfying workshop interaction

## Player fantasy
Start with an imperfect or cheap car, diagnose it, rebuild it, choose a direction, tune it, test it, break it, learn from the failure, and improve it.

## Vehicle progression
Vehicles and parts should create different engineering problems rather than simply increasing a numerical power tier.

## Engine variety and swaps
The game should eventually offer hundreds of engine families that differ the way real engines do — layout (inline, V,
flat), banks, cylinder count, valvetrain (OHV, SOHC, DOHC) and valves per cylinder, cam phasing, variable valve lift,
variable intakes, natural aspiration, single and twin turbocharging (later supercharging), one or several intake and
exhaust paths — and let the player swap them between cars. An engine goes into a car when its interfaces match
(bellhousing today; mounts, clearances, cooling, fuel, exhaust routing, wiring, driveshaft and differential later), not
because a list says it may. Each engine brings its own engineering problems: a pushrod V8's single cam feeds both
banks, a twin-turbo V6 can lose one turbo, a variable intake needs an ECU that can switch it, a different head on one
bank changes only that bank's compression. How engines are authored: ENGINE_AUTHORING_GUIDE.md.

## Engine building
An engine is assembled from real-ish subsystems:
- block
- crankshaft
- connecting rods
- pistons
- bearings
- cylinder head
- valves
- valve springs/retainers
- camshafts
- intake
- throttle
- injectors
- fuel pump
- exhaust
- turbocharger(s)/supercharger
- intercooler
- ignition
- ECU
- cooling
- lubrication

## Tuning
Tunables should include, where supported:
- fuel targets
- ignition timing
- boost target
- boost control
- idle
- rev limiter
- launch control
- cam timing (phaser maps), valve-lift and intake-runner switch speeds
- fuel pressure
- sensor calibration

## Chassis
Eventually support:
- springs
- dampers
- anti-roll bars
- alignment
- steering
- differential
- gearbox ratios
- final drive
- brakes
- tires
- weight distribution

## Failure
Failures should result from measurable causes:
- excessive cylinder pressure
- detonation/knock
- overheating
- oil starvation
- over-rev
- lean operation
- component fatigue
- drivetrain overload

Failures should be diagnosable rather than random punishment.

## UX
The player should always be able to answer:
- What changed?
- Why did it change?
- What measurement proves it?
- What failed and why?

## Prototype non-goals
Do not initially build:
- massive open world
- multiplayer
- hundreds of cars
- procedural traffic
- advanced NPC economy
- photorealistic graphics
