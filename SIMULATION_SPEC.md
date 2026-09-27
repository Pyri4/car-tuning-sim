# Simulation Specification

This document describes the model **as implemented**. Code references are to `src/CarSim.Core/`.
When the code changes, this file changes in the same commit.

## Philosophy
- Mean-value model: cycle-averaged quantities, no crank-angle resolution, no CFD.
- Every output must respond in the right direction to every relevant part and tune change, for a
  reason a player can learn ("the exhaust is restrictive → backpressure → less top-end").
- SI units internally. Content uses automotive units with the unit in the field name.
- Deterministic. No random numbers anywhere in the model.
- Constants are named, documented, and tested for direction of effect; a handful of reference points
  are pinned by tests so calibration drift is visible.

## Engine model overview (`Simulation/EngineSimulation.cs`)
Each `Step(dt, inputs)`:
1. Engine speed: imposed (`SpeedMode.Held`: dyno brake / drivetrain) or integrated (`Free`).
2. ECU pre-step: rev limiter (fuel cut, 150 rpm hysteresis), idle-air PI controller, intake cam phaser target.
3. Air path solved quasi-statically at this speed and throttle.
4. ECU meters fuel and picks spark from its tables using the MAP it can *measure*.
5. Fuel system delivers what injectors and pump physically can.
6. Combustion → IMEP; pumping (PMEP) and friction (FMEP) → BMEP → torque.
7. Heat split to coolant, oil and exhaust; loads on rods and bearings; oil pressure.
8. Integrate temperatures, knock control, and speed (when free).

Steps 2–3, 6 and 7 run once per **bank** (see "Banks and air paths"); speed, temperatures of coolant and oil, the
ECU and the fuel command are engine-wide.

Outputs are an `EngineTelemetry` record (≈60 engine channels, plus `Banks[]` and `Turbos[]`).

Performance (measured in the validation pass, Release, .NET 8 defaults, one core of a shared 4-core container;
best of 5 × 20 000 steps, ±10 % run to run; the second-family milestone's own measurements follow the table):

| Step | Time | Allocated | At 500 Hz |
|---|---|---|---|
| NA engine | ≈ 62–75 µs | 10.6 KB | 5.2 MB/s |
| Turbo engine | ≈ 84–87 µs | 13.9 KB | 6.8 MB/s |
| ↳ air-path solve (8 outer Brent evaluations, ≈ 8 orifice inversions each) | 37 / 54 µs | 5.2 / 7.6 KB | |
| ↳ knock limit (two cycle integrals) | ≈ 12 µs | 0 | |
| ↳ damage update + live warnings | ≈ 5–7 µs | 2.9 KB | |
| ↳ telemetry record | 0.1 µs | 0.6 KB | |
| Vehicle (engine + 8 chassis substeps, ride, tyre thermal) | ≈ 79–82 µs | 11.9 KB | 5.8 MB/s |

Second-family milestone (`carsim bench <engine>`: 2 ms steps, full throttle held at 5,000 rpm, then the car on
autopilot; best of 5 × 20 000 steps, three runs each, this session's container):

| Step | K20 before the milestone | K20 after | M54 |
|---|---|---|---|
| Engine | 43.7–47.2 µs, 10,560 B | 42.6–49.4 µs, 10,576 B | 44.2–48.3 µs, 10,576 B |
| Vehicle (engine + chassis) | 71.5–72.3 µs, 13,220 B | 57.4–73.3 µs, 13,236 B | 63.6–73.5 µs, 13,249 B |

Six cylinders cost the same as four: the mean-value model has no per-cylinder loop, so cylinder count only scales
numbers. The +16 B is the one air-path closure carrying the cam-phase field. That is ≈ 4–5 % of a core at 500 Hz. The allocations are short-lived gen-0 garbage: closures in the orifice root
finds, and warning strings rebuilt every step. An allocation-free (struct-generic) root finder was built and
measured: bit-identical results, half the allocation, turbo steps 10 % faster — but NA and vehicle steps ≈ 25 %
*slower* under .NET 8's default dynamic PGO (equal or faster with PGO off), so it was not merged. An
allocation-budget test (16.5 KB per turbo step) guards regressions.

Engine-architecture milestone (`carsim bench`, same settings, two runs each, the PR #4 head built in the same container
for the before column):

| Engine step | Before (PR #4 head) | After |
|---|---|---|
| K20 (1 bank, NA) | 55.0–56.0 µs, 10,576 B | 55.0–55.9 µs, 10,360 B |
| M54 (1 bank, NA) | 53.9–55.8 µs, 10,576 B | 53.6–55.7 µs, 10,360 B |
| syn_i4_turbo (1 bank, turbo) | — | 71.7–73.1 µs, 13,096 B |
| syn_v6_na, syn_v8_dohc_vvt (2 banks, NA) | — | 101.4–103.5 µs, 18,352 B |
| syn_v6_tt (2 banks, 2 turbos) | — | 151.7–152.5 µs, 28,032 B |

Single-bank engines cost the same (and allocate 216 B less). Cost scales with the number of air paths — each bank solves
its own — so two banks cost ≈ 1.85× one and the twin-turbo V6 ≈ 2.1× a single-turbo four: 7.6 % of a core at 500 Hz.
Cylinder count still costs nothing. The allocation-free root finder would now pay off twice over on multi-bank engines.

## Banks and air paths (`Simulation/BankConfiguration.cs`, `Simulation/EngineConfiguration.cs`)
An engine family declares banks (ids and cylinder numbers; one implicit bank `main` when none are authored). The model's
resolution is the bank: there is no per-cylinder state. Architecture rules and authoring: ENGINE_AUTHORING_GUIDE.md.

- **Scopes.** Engine-wide parts (block, bearings, crank, rods, pistons, injectors, fuel pump, oil pump, sump, radiator,
  flywheel, ECU) are one part or set for the engine. Each bank is served by one head gasket, head, spring set, camshaft
  set, intake manifold, throttle, exhaust manifold and exhaust, and at most one turbocharger and intercooler; one part
  may serve several banks.
- **Shared elements.** Bank `b` with `n_b` cylinders sees a part serving `n_s` cylinders with the share
  `s = n_b / n_s`: restrictions enter its path with `CdA · s`; a shared turbocharger's compressor is evaluated at the
  bank's mass flow `/ s` (the whole compressor's operating point when the banks are alike) and the bank's turbine and
  wastegate areas are `× s`; an intercooler's effectiveness is read at the bank's flow `/ s`. The air path of each bank
  is then solved independently. For alike banks this is exact — two alike banks give the same result as one bank of all
  the cylinders to within solver tolerance (`TwoAlikeBanksAreTheSameEngineAsOneBankOfAllItsCylinders`, 10⁻⁶) — and for
  a single bank `s = 1` exactly, so single-bank engines are bit-identical to the pre-bank model. For **dissimilar**
  banks sharing an element it is an approximation: there is no cross-feed between banks (a restricted bank does not
  draw more through a common plenum; a failed turbo's bank runs naturally aspirated although the other turbo shares
  its plenum).
- **Turbochargers.** One shaft, wastegate and boost-control integrator per turbo. The shaft is driven by the sum of
  its banks' turbine power shares; its wastegate reads the compressor outlet of the first bank it serves and the boost
  controller reads the ECU's one MAP sensor (exact for alike banks). Each bank's turbine outlet temperature is its own.
- **ECU.** One MAP sensor, on the plenum of the first bank's air path; one fuel command and one spark advance for all
  cylinders, so banks that breathe differently run at different λ (a real symptom). Every bank's cam phaser, valve-lift
  stage and runner stage follow the same tables and switch speeds.
- **Per bank:** air per cycle, port and exhaust-port pressures, charge temperature, λ, MBT, knock limit and intensity,
  IMEP/PMEP/FMEP (with that bank's springs and valve count), peak cylinder pressure, heat split, EGT (and its sensor),
  exhaust-port wall heat, turbine outlet temperature, cam advance, lift stage, runner stage, valve-float speed.
- **Engine-wide aggregation:** torque, air and fuel flow, heat flows and power are sums; IMEP/PMEP/FMEP/BMEP, MBT and λ
  are cylinder-weighted means (weights `n_b/N`, exactly 1 for one bank); peak cylinder pressure, knock intensity and
  the lowest knock limit, the lowest valve-float speed and the hottest crown target are the worst bank's (the knock
  sensor hears the worst bank; one knock retard applies to all; the pistons are one set). The rod and bearing loads use
  the worst bank's peak pressure.
- **Damage.** Stress readings name the part instance: a gasket from the peak pressure of the banks it serves, a head
  and spring set from their own bank's float speed, a turbo from its own shaft and inlet temperature. A failure's
  collateral damage stays on the failed part's banks.
- **Workshop.** The compression test reads each bank (`EngineDiagnostics.CompressionTestByBank`): a failed gasket or a
  bent valve shows on its own bank.

## Geometry (`Engines/EngineGeometry.cs`)
- Swept volume per cylinder `V_d = π/4 · B² · S`; displacement `V = n · V_d`.
- Deck clearance `= deck_height − (S/2 + rod_length + compression_height)`.
- Clearance volume `V_c = V_chamber + V_gasket + V_deck + V_dish`, `V_gasket = π/4 · B_gasket² · t`,
  `V_deck = π/4 · B² · deck_clearance`.
- Compression ratio `CR = (V_d + V_c) / V_c` — always derived, never authored.
- Reciprocating mass `m_recip = m_piston + m_rod / 3`.
- Rod inertia load (tensile, TDC exhaust) `F = m_recip · ω² · r · (1 + r/l)`.
- **Per bank:** the bottom end (block, crank, rods, pistons) is shared; the gasket and head are the bank's own, so each
  bank has its own clearance volume, quench and compression ratio (`BankConfiguration.Geometry`). Combustion, knock,
  residuals, the validator's quench and compression checks and the compression test use the bank's geometry. The
  engine-level `EngineConfiguration.Geometry` is the first bank's, used only for bottom-end quantities (bore, stroke,
  displacement, reciprocating mass). A different head or gasket on one bank changes only that bank
  (`EachBankCompressesToItsOwnHeadAndGasket`).

## Air path (`Simulation/AirPath.cs`, `Simulation/CompressibleFlow.cs`)
Every restriction is an isentropic nozzle:
`ṁ = CdA · p_up / √(R·T_up) · Ψ(p_down/p_up)`, choked below the critical ratio (0.528 for air).
Effective area comes from the part's flow-bench rating:
`CdA = Q_cfm / √(2 · 6974 Pa / 1.204 kg/m³)` (28 inH₂O standard).

Chain: ambient → intake/filter (`flow_cfm`) → throttle → manifold → intake port → cylinder;
cylinder → exhaust port → exhaust manifold → exhaust system → ambient.

- **Throttle area**: `CdA_WOT · (1 − cos(θ·π/2)) + idle-valve bypass (2 % · valve) + leak (0.08 %)`.
- **Ports** see pulsed flow: the average per-cylinder flow divided by the valve-event fraction
  `(duration@1mm + 50°)/720`. Port `CdA` is the flow-bench curve averaged over a harmonic lift
  profile of the cam's peak lift, so extra lift on a head whose flow plateaus gains little, while a
  ported head rewards lift.
- **Exhaust**: temperature-dependent density (hot exhaust needs more pressure drop for the same mass).
  The exhaust system sees gas 150 K cooler than the port. Only 50 % of the exhaust mass (the part
  not expelled during blowdown) loads the exhaust ports during the exhaust stroke.
- **Solve**: engine demand `ṁ_eng = VE_dyn · f_res · ρ_port · V_d · n · rpm/120`. The through-flow
  is the root of `ṁ_eng(ṁ) − ṁ = 0` (Brent's method); this is monotonic because every restriction's
  drop grows with ṁ.
- **Charge temperature**: `T_man + 0.12·(T_coolant − T_man) − ΔT_evap`, with
  `ΔT_evap = 0.2·(F/A)·h_vap/c_p` (20 % of port-injected fuel evaporates from the air before the inlet
  valve closes): ≈ 5 K for gasoline at λ 1, ≈ 17 K for E85. Latent heat per kg of fuel is derived from
  the fuel's `charge_cooling_factor`: `h_vap = 350 kJ/kg · factor · AFR_stoich/14.7`.

### Volumetric efficiency (tuning component)
- Tuned mean piston speed `v = 15 + 0.15 · (intake_duration − 220°) + 0.30 · Δ_IVC` m/s → `rpm_cam = v·60/(2S)`, where
  `Δ_IVC` is how many crank degrees later the intake closes than the reference installation (see "Cam timing"; 0 for
  straight-up cams, so every K20 cam is exactly as before). `v` is guarded at ≥ 2 m/s.
- Intake runner shifts it: `rpm_peak = rpm_cam · (0.300 m / L_runner)^0.25`.
- Shape (x = rpm/rpm_peak): below peak `1 − a_lo(1−x)²` with `a_lo = 0.50 + 0.004·overlap°`
  (overlap costs low-rpm filling); above peak `1 − 0.25(x−1)²`; floor 0.25; scaled by 1.02.
- Header scavenging: `+ gain · exp(−((rpm − rpm_tuned)/(0.25·rpm_tuned))²)`,
  `rpm_tuned = 5.2e6 / primary_length_mm`.
- Valve float: above the float speed VE collapses by up to 60 % over the next 8 %.
- Proposed replacement (not implemented): "Intake gas dynamics 2.0 — proposed model" below separates valve-event filling
  from runner/plenum gas dynamics.

### Residuals and reversion
With pressure ratio `r = p_exhaust_port / p_intake_port` and clearance share `c = V_c/(V_c+V_d)`, the
displaced fraction is `x = c · [(r^(1/1.3) − 1) + (overlap°/15) · max(0, r − 1.25)]` and
`f_res = 1 / (1 + x)`. The first term is burnt gas left in the clearance volume; the second is exhaust
pushed back into the intake during overlap once exhaust pressure is well above intake pressure (tuned
NA exhausts keep pulse pressure favourable below the 1.25 margin; turbo manifolds exceed it).
`1/(1+x)` equals `1 − x` to first order but only tends to zero as the backflow grows (reverted gas
raises the port pressure and chokes its own backflow). The earlier `1 − x` clamped at 0.4 put a flat
floor and a kink into part-load VE for long-overlap cams (VE 0.39 flat below 31 kPa MAP, then 0.60 at
45 kPa), which no fuel table can follow. `f_res` is at most `1/(1 − c)` (exhaust below intake).

## Valvetrain (`Engines/ValvetrainModel.cs`)
Harmonic lift profile over the advertised event (duration@1mm + 50°):
`rpm_float = (D/6) · √(F_open / (2π² · m_valve · L))`. Spring wear reduces force by up to 15 %.

## Cam timing (`Parts/Specs/TopEndSpecs.cs`, `Simulation/EngineConfiguration.cs`, `Simulation/AirPath.cs`)
Added for the second engine family (double VANOS). The duration correlation above reads intake duration as a stand-in
for intake closing (IVC), fitted on straight-up cams whose separations span 108–114° and pinned by the K20's 112° OEM
pair: implicitly every cam sat at an intake centreline of ≈ 112°. A cam pair may now state its installed lobe
centrelines — degreed-in cams, or the **park** position of cam phasers — and an intake phaser range:
- `Δ_IVC = ICL_installed − advance − 112°` when centrelines are authored, 0 otherwise
  (`EngineConfiguration.ReferenceIntakeCenterlineDeg`). The intake closes one degree later per degree of centreline,
  but only half a degree per degree of duration, hence `0.30 = 2 × 0.15` m/s per degree — no new fitted constant.
- Overlap `= (D_i + D_e)/2 − (ICL + ECL) + advance` (the existing `− 2·LSA` when straight up); it feeds the low-side
  VE curvature and reversion as before.
- Phaser: the ECU looks its `intake_cam_advance_deg` table up at the MAP it read on the previous step, clamps it to
  `[0, intake_phaser_range_deg]` (0 without `cam_phase_control`, without a table, or when the engine is not running —
  no oil pressure), and the phaser follows with a 0.15 s time constant. Telemetry `IntakeCamAdvance`; dyno CSV
  `intake_cam_deg` (a cam-position sensor reads it).
- Effect: advancing closes the intake earlier — better filling at low speed, worse at high speed, more overlap. The M54
  map advances 50° at idle and parks (late closing) above 5,500 rpm.
- Guard (`CamTuningFloorActive`): `v ≥ 2 m/s`. The correlation is linear and a very early closing would extrapolate it
  through zero; below the guard the VE peak sits at a few hundred rpm. Tested never to act on the shipped calibrations of
  either family; reached only by commanding the M54's full 60° at high speed.
- Not modelled: exhaust phasing (an exhaust phase would only add overlap, which this model counts purely as a filling
  cost — there is no exhaust-opening/blowdown or scavenging term), part-load internal-EGR strategies (no pumping or
  emissions benefit is modelled, so the phaser map is a filling optimum everywhere), the phaser's oil-pressure
  dependence, and intake gas dynamics that a phaser cannot move — the phaser shifts the whole filling hump, so a phased
  engine fills at its ceiling at every speed and the runner has no effect under it (see "M54 torque-curve
  investigation").

## Variable valve lift (two-stage) and variable intake runners
Generic capabilities added with the engine-architecture milestone. Both are part data plus an ECU output and a tune
switch speed; without the ECU output the hardware stays in its base state and the validator warns.

- **Variable valve lift** (VTEC/VVL type): a camshaft may carry a `high_lift_profile` (intake/exhaust duration and
  lift). The ECU (`valve_lift_control`) switches to it above the tune's `valve_lift_switch_rpm` and back 150 rpm below
  (`EcuController.SwitchHysteresisRpm`), only while running. On the high profile the bank's port flow (mean flow over
  the lift profile), event lengths, the duration term of the tuned piston speed, overlap and valve-float speed are the
  high profile's; the springs must clear the higher lift (coil bind is checked against the larger lift).
- **Variable intake runner** (DISA/VIS type): an intake manifold may carry a `switched_runner_length_mm`. The ECU
  (`intake_runner_control`) switches to it above `intake_runner_switch_rpm` (same hysteresis). The switched stage
  replaces the runner length in the tuning factor `(0.300 m / L)^0.25`.
- **Limits.** The runner stage acts through the single filling hump: on a fixed-cam engine it moves the hump as a
  runner swap would, but under a cam phaser the phaser re-centres the hump and the runner has little effect — the
  limitation found in the M54 torque-curve investigation. The M54's DISA is therefore not authored; splitting intake gas
  dynamics from valve timing is the next milestone (ROADMAP).
- Telemetry `HighValveLift`, `SwitchedRunner` (engine, from the ECU outputs) and per bank.

## ECU (`Ecu/`)
The ECU only knows its sensors and its calibration; it never sees the engine's true airflow.
- Tables over rpm × **measured** MAP (kPa): target λ, spark advance, volumetric efficiency. Optional
  boost target vs rpm.
- MAP sensor reading is clipped at `map_sensor_max_kpa` (stock ECU on boost: the reading and so the
  fuel estimate stop rising → lean).
- Speed-density air estimate per cylinder per cycle:
  `m_air_est = VE_table(rpm, MAP_read) · MAP_read · (displacement_cc / cylinders) / (R · IAT)`, with
  IAT the manifold air temperature (the sensor sits upstream of the injectors).
- Fuel command: `m_f = m_air_est / (λ_target · fuel_stoich_afr)`; pulse width
  `= m_f / (fuel_density_kg_l · injector_flow_cc_min) + injector_dead_time_ms` — every term is the tune's belief,
  never the installed injectors or the fuel in the tank.
- λ therefore follows the ratio of true to estimated air. Everything the table was not calibrated
  for moves it: cams, head, header, exhaust, intake runners, turbo and manifold, a stroker
  (displacement), and even the fuel — E85's stronger evaporative cooling happens after the IAT sensor
  and packs in ≈ 4–6 % more air than a gasoline table expects. The IAT correction covers most of a
  missing intercooler (≈ 3 % λ error for ≈ 11 % less dense charge). Wrong injector scaling or fuel
  calibration gives the corresponding AFR error: the stoich ratio, and the density (race fuel is 4 % lighter
  than pump fuel, E85 4 % denser). A wrong dead time misfuels short pulses: 0.4 ms is ≈ 20 % at idle and
  ≈ 2 % at full load. A regulator above the injectors' rated pressure runs rich by √(ΔP/ΔP_rated). Coolant
  temperature moves the mixture too: the charge picks heat up after the IAT sensor, so a cold engine runs
  lean and a hot one rich (no coolant correction).
- Not modelled: coolant/warm-up enrichment, transient (wall-film) fuelling, closed-loop λ trim (open-loop
  errors persist — tested), battery-voltage and pressure dependence of the dead time. The wideband λ on the
  dyno is the feedback the player tunes the VE table with.
- Base-map tools for content authors (the game never calls them): `CamPhaseCalibrator` (`carsim calibrate-cams`: the
  intake advance that traps the most air at each rpm and throttle of the VE sweep, mapped onto the tune's MAP axis),
  `SparkCalibrator` (`carsim calibrate-spark`: `min(MBT − 1°, knock limit − 1.5°)` on the fuel given, three passes because
  the knock limit is exact near the running advance), and:
- `VeCalibrator` (`carsim calibrate-ve`) is the content author's base-map generator: it holds the
  engine on a steady-state dyno over a 13-point throttle sweep at every rpm column, computes
  `VE = m_air · R · IAT / (MAP · V_cyl)` and interpolates onto the table's load axis (two passes, the
  second fuelled from the first; boost target raised to the top load row; columns above the rev limit
  repeat the last one inside it). The game never calls it. The shipped tables are its output, and
  `SpeedDensityTests` fails when physics changes make them stale (stock engine off target by > 2 %).
- Rev limit: `min(tune, hardware max)`, fuel cut with 150 rpm hysteresis.
- Knock control (if hardware + tune enable it): retard at 6°/s per degree of knock, up to 10°;
  recover at 1°/s.
- Knock sensor (what the player sees, `Ecu/KnockSensor.cs`): a level — none, trace (< 0.3°), light (< 1°),
  moderate (< 2.5°), heavy — not the degrees past the limit, from which one reading would give the limit away.
  Onset is exact, so the limit is found by sweeping timing. The dyno screens and CSV log show only measurable
  channels (no MBT, knock limit, charge temperature; `iat_c` is the manifold air the sensor reads).
- Idle: PI on idle-air valve while throttle < 2 %.

## Fuel system (`Simulation/FuelSystem.cs`)
- Commanded duty `= pulse_width / (120/rpm)`; physically capped at 100 % (static). Fuel flows for
  `pulse_width − dead_time` of each pulse (`dead_time_ms` per injector; OEM 0.90, 550 cc 1.00, 1000 cc 1.10 ms).
- Injector flow `∝ √(ΔP / rated ΔP)`, minus up to 25 % with wear (clogging).
- Pump: `Q = Q_free · (1 − 30 % wear) · (1 − (ΔP + boost)/p_max)`. Regulator is 1:1 manifold
  referenced, so boost reduces pump capacity. If demand exceeds supply, rail ΔP sags until they balance.
- Delivered fuel sets the actual λ; `FuelLimit` reports injector or pump limitation.

## Combustion (`Simulation/CombustionModel.cs`)
- Burned fuel `= min(m_f, m_air / AFR_stoich)`.
- Indicated efficiency `η = (1 − CR^(−0.3)) · 0.80 · (1 − 0.10 · ring wear)`.
- Mixture factor table (peak 1.04 at λ ≈ 0.88; lean side includes lean-burn efficiency; misfire
  beyond λ ≈ 1.5).
- MBT `= 14 + 14·√(clamp(rpm/6000, 0.05, 1.5)) − 7·(ρ_charge/ρ_ref − 1)` degrees.
- Spark factor `= 1 − k·(adv − MBT)²`, `k = 0.0003` retarded, `0.0004` over-advanced.
- Knock (`Simulation/KnockModel.cs`): end-gas autoignition, Livengood–Wu. The end gas knocks if
  `∫ dt/τ ≥ 1` before 90 % of the charge has burned, with the Douaud–Eyzat delay
  `τ = 17.68 ms · (OI/100)^3.402 · (p/atm)^−1.7 · exp(3800 K/T) · exp(2(1 − λ))`.
  - Cycle: single zone in 2° steps from 70° BTDC. Polytropic compression (n = 1.32) from the port
    pressure. The compression-start temperature is the charge mixed with hot residual gas:
    `x_r = 1/(1 + (CR − 1)·(p_in/p_ex)·(900 K/T_charge))`, ≈ 4 % and +20 K at full throttle.
  - Heat release: Thornton's rule, 2.94 MJ per kg of air at λ ≤ 1 (× partial oxidation when rich,
    ÷ λ when lean). 80 % of it raises the pressure, on a Wiebe curve (a = 5, m + 1 = 3) whose
    duration puts 50 % burned at 8° ATDC when spark is at MBT. The resulting peak pressures match
    the engine's PCP model (56–69 bar at WOT and MBT).
  - End gas: isentropic with the cylinder pressure, + 0.5 K per K of coolant above 90 °C. Poor
    quench keeps it alive 3° longer per mm of deck clearance beyond 1 mm.
  - OI is Kalghatgi's octane index, `RON − K·(RON − MON)`. K is 0 naturally aspirated and falls by
    0.5 per bar of boost to −1, so under boost high-sensitivity fuels (E85: RON 105, MON 88) beat
    their RON.
  - KLSA is found from the integral at the running advance and 4° less (ln I is nearly linear in
    advance). The engine knocks exactly when `I(advance) ≥ 1`, and the degrees past the limit are
    accurate near it. There is no light-load special case: at part load the end gas never gets hot
    and dense enough. Cost ≈ 12 µs per engine step.
  - Result, stock K20 (CR 10.5) at WOT: knock-limited below ≈ 3200 rpm on RON 91, ≈ 2900 on RON 95
    and ≈ 2700 on RON 98 (11.6° under MBT at 1500 rpm on RON 95). Above that it can run MBT.
    Octane is worth ≈ 0.5° per RON at the limit. The factory table's knock-limited cells sit
    1.5° under the RON 95 limit; on RON 91 the knock sensor retards them.
- Knock intensity `KI = max(0, advance − KLSA)` degrees. Effects: −1 %/° torque, +4 %/° peak
  pressure, +8 K/° piston crown temperature, +1 %/° heat to coolant (capped at 10°).
- `W_i = burned · LHV · η · f_λ · f_spark · f_knock`, `IMEP = W_i / V_d`.
- Peak cylinder pressure `= p_port·CR^1.3 + 3.4·IMEP·clamp(1 + 0.025(adv − MBT), 0.3, 2)`, × (1 + 0.04·KI).

## Pumping and friction
- `PMEP = p_exhaust_port − p_intake_port`.
- `FMEP [kPa] = 45 + 4.0·v_p·√(μ/μ_100°C) + 0.2·v_p² + 12·(F_spring_open/560 N)·(valves/4)·(0.5 L/V_cyl) + 0.004·PCP[kPa]`
  (the valvetrain term is spring work per valve over the cylinder volume; it was a K20-only `12·F/560` until the
  validation pass).
- `BMEP = IMEP − PMEP − FMEP`, torque `T = BMEP · V / (4π)`, power `P = T · ω`.

## Heat and temperatures
- **Energy balance (exact every step):** `fuel power = brake power + heat to coolant + heat to oil +
  exhaust heat`. Released fuel power = burned fuel · LHV · `f_rich(λ)`, where for λ < 1 the oxygen is
  shared across the excess fuel and CO/H₂ leave with their heating value:
  `f_rich = (525 − 119/λ)/406` (406 kJ released per mol O₂ by full oxidation — Thornton's rule — and
  525 kJ per mol O₂ of deficit left in CO/H₂): 96 % at λ 0.88, 90 % at λ 0.75. `CombustionModel.SplitHeat`
  divides the released power into indicated work, in-cylinder coolant heat (`0.265 − 0.06·clamp(rpm/7000, 0, 1.4)
  + 0.01·KI`, × 1.3 with a blown head gasket), oil (0.03) and exhaust (the remainder, so the split cannot
  create energy). Pumping work leaves with the exhaust gas; friction heat goes 35 % to oil, 65 % to coolant.
- **Exhaust port:** on its way out the gas exchanges heat with the coolant-jacketed port walls, by the
  exact uniform-pipe law the manifold uses: `T_port = T_wall + (T_adiabatic − T_wall)·exp(−UA_port/(ṁ_exh·c_p))`
  with `T_wall = T_coolant` and `UA_port = 150 W/(m²·K) · 1.3·B² · cylinders` (≈ 5.8 W/K for the K20; port
  measurements, Caton & Heywood 1981, give 400–1000 W/(m²·K) while the valve is open, a third of the cycle).
  The heat goes to the coolant. At full load it is 1–2 % of the fuel energy (the in-cylinder fraction above
  was 0.28 before the port was split out, so the full-load coolant total is unchanged at ≈ 27–31 %); on
  closed-throttle overrun a few g/s carry the whole pumping work and the walls take nearly all of it — without
  them the gas left the ports at 1,500–4,500 °C and burned the turbine on the next tip-in (fixed in the
  validation pass). Motoring with an open throttle, cold gas picks heat up from the walls by the same law.
  UA is held constant with flow (h ∝ ṁ^0.8 would under-cool overrun, where the tidal backflow through a
  nearly shut throttle, not the net flow, scrubs the port); documented simplification.
- Exhaust gas temperature `= T_charge + (exhaust heat − latent heat of the excess fuel)/(ṁ_exh·c_p)`
  after the port loss (80 % of the unburned fuel's latent heat is absorbed in the cylinder). Richer mixtures
  run cooler from these two effects alone. Lag 0.2 s (gas), 1.5 s (sensor).
- Piston crown temperature `= T_coolant + 150 K·(q/14.2 MW/m²)^0.7·(1 + 1.5·max(0, λ − 0.9)) + 8 K·KI`,
  q = burned-fuel power per piston area. Lag 3 s.
- Coolant: capacity = coolant (1.05 kg/L, 3600 J/kgK) + half of block and head metal (900 J/kgK).
  Radiator `Q = UA · (v_air/v_ref)^0.6 · thermostat · (T_coolant − T_ambient)`,
  thermostat opening smoothstep over [T_open, T_open + 10 K]. Surface loss 15 W/K.
- Oil: capacity = sump oil (0.88 kg/L, 1900 J/kgK) + 8 kJ/K metal. Oil↔coolant exchange 400 W/K;
  sump convection 12 W/K·(1 + 0.1·v_air).
- Boiling: coolant is capped at 128 °C (50/50 mix under a ~1.1 bar cap). Surplus heat boils coolant off
  (latent heat 2.0 MJ/kg) and lowers the coolant level; radiator heat rejection scales with the level.
- Dyno cells can hold coolant temperature (`CoolantTemperatureOverride`).

## Lubrication
- Pump delivery `Q = displacement · rpm/60 · 0.9`.
- Oil surge: beyond the pan's `max_sustained_g` the pickup draws air; pressure is multiplied by an
  aeration factor that falls from 1 to 0 over the next 0.15 g (surplus pump capacity does not help).
- Leakage conductance `K = 1.157e−9 m³/(s·Pa) · (V/2.0 L) · (μ_100/μ) · (0.6·C + 0.4)`,
  `C = mean over main/rod bearings of (clearance·(1+wear) / 0.040 mm)³`.
- `p_oil = Q/K`, capped by the relief valve (5 % slope above relief).
- Requirement `p_req = 0.3 bar + 0.25 bar per 1000 rpm` (used by the damage model).
- Oil viscosity: 5W-30 table, log-interpolated (10 mPa·s at 100 °C).

## Speed dynamics and starting
- Rotating inertia = crank + flywheel + n·(⅔ rod·r² + ½·m_recip·r²) + 0.02 kg·m² accessories.
- Free mode: `dω/dt = (T_engine + T_starter − T_load)/(I + I_load)`.
- Starter: 60 N·m at 0 rpm falling to 0 at 300 rpm. Combustion from 150 rpm; running above 400 rpm;
  stalls below 250 rpm without the starter.

## Forced induction (`Simulation/TurbochargerModel.cs`)
The turbo is optional (a `turbocharger` slot, `intercooler` slot after it). The compressor sits
between the intake/filter and the intercooler/throttle; the turbine and wastegate sit between the
exhaust manifold and the exhaust system.

### Compressor
Closed form (no iteration), so every operating point has exactly one answer that varies continuously.
Flows are corrected: `ṁ_c = ṁ·√(T₁/298.15 K)/(p₁/1 atm)`; `n` = corrected tip speed / tip speed at
`max_shaft_rpm`.
- Choke flow grows with speed: `ṁ_choke(n) = compressor_choke_flow · √n` (the authored value is the choke
  flow at the rated maximum speed). Flow ratio `x = ṁ_c/ṁ_choke(n)`. A small wheel can only pass more air
  by spinning faster, which is how small turbos over-speed at high engine rpm.
- Euler work `w = 0.68·U²·(1 − 0.25·x/(1 + x/2))`: backswept blades do slightly less work per kilogram at
  high flow, but the work never collapses (≥ 50 % of 0.68·U²) — past choke the wheel keeps absorbing
  power, so the shaft cannot run away.
- Efficiency island in flow and speed (never pressure ratio, which removed the old circular
  efficiency↔PR iteration): `η = η_peak/(1 + (Δṁ/ṁ_choke,max)² + 0.5·(n − n_peak)²)`, where `n_peak`
  is the speed whose speed line passes through the authored island centre (peak-efficiency flow and
  PR). Choke collapses it: `× (1 − smoothstep(0.85, 1, x))`.
- Surge line `ṁ_surge = surge_flow_at_pr2·(PR₀ − 1)` (PR₀ = pressure ratio before surge losses). Left of
  it, efficiency falls smoothly by up to 15 % over half the surge flow, and half of the flow deficit
  recirculates through the wheel (absorbing work, heating the housing): a compressor surging after a
  throttle lift keeps braking the shaft.
- `PR = (1 + η·w/(c_p·T₁))^(γ/(γ−1)) · exp(−10·((x − 1)·n)²)` for x > 1: beyond choke the wheel is a
  restriction whose loss grows with tip speed and vanishes on a stopped turbo. PR rises monotonically
  with speed for any flow below twice the speed's choke flow.
- `T₂ = T₁ + w/c_p` (all through-flow work heats the charge). Shaft power
  `P_c = (ṁ + ṁ_recirc)·w + ½·0.005·ρ₁·ω³·r⁵` (recirculation and disk friction).
- The reported efficiency is the effective isentropic efficiency `(PR^((γ−1)/γ) − 1)·c_p·T₁/w`; it is
  negative past choke.

### Charge cooling
- Intercooler: `T = T₂ − ε·(T₂ − T_ambient)`, `ε = min(0.95, ε_ref·(ṁ_ref/ṁ)^0.15·clamp((v_air/15)^0.25, 0.5, 1.1))`;
  pressure drop through its `flow_cfm` restriction.
- Without an intercooler the charge pipes shed 10 % of the compressor's temperature rise.

### Turbine and wastegate
- Turbine and open wastegate are parallel nozzles: turbine inlet pressure is the upstream pressure
  that passes the exhaust flow through `A_turbine + A_wastegate·opening`; the turbine receives the
  `A_turbine / A_total` share of the flow.
- Turbine inlet temperature T₃: the port gas cools through the manifold's heat-loss conductance
  (`UA = 0.0075 W/K per mm of primary per cylinder`: 7.5 W/K for the log manifold, 13.5 W/K tubular),
  `T₃ = T_amb + (T_port − T_amb)·exp(−UA/(ṁ·c_p))` — the exact solution for a uniform pipe. Longer
  primaries lose more heat (a small spool penalty for the better-flowing tubular manifold).
- Power `P_t = ṁ_t · η_t · c_p,exh · T₃ · (1 − (p₄/p₃)^((γ−1)/γ))`, with
  `η_t = η_peak · (1 − ((BSR − 0.7)/0.5)²)`, BSR = turbine tip speed / √(2·Δh_s), held at ≥ 0.25·η_peak
  below the optimum only (a slow wheel still turns the flow, and the energy form of the shaft equation needs power
  at zero speed to spool from rest — reached spooling from standstill and at idle, never on boost; tested). Past
  BSR 1.2 η_t goes negative: a wheel spinning faster than its gas windmills and does work on the gas (bounded,
  ≈ 2·η_peak·ṁ·U²) — what slows a turbo on overrun. Until the validation pass the 0.25 floor applied on both
  sides and credited a coasting T35 ≈ 50 W, more than its bearing drag. Debug telemetry: `TurbineBladeSpeedRatio`.
- Turbine outlet temperature drops by `P_t/(ṁ_t·c_p)`; mixed with the (hot) wastegate flow it sets the
  exhaust-system gas temperature on the next step.
- Drive pressure (turbine inlet / boost) is an emergent result of the power balance: ≈ 0.87–0.99 for the
  mid-size turbo at 0.75 bar (the authored efficiencies are steady-flow map peaks, so this errs slightly
  optimistic), ≈ 1.35 for the small turbine at high rpm, which is when valve overlap causes reversion.

### Shaft
- Energy `E = ½·I·ω²`, `dE/dt = P_t − P_c − k·ω²` (k = 2e−6 journal, 1e−6 ball bearing). No speed clamp:
  the shaft settles where the powers balance.
- Overspeed (ω above `max_shaft_rpm`) is flagged in telemetry for the damage model.

### Boost control
- Mechanical: wastegate opening = clamp((boost_gauge − spring)/20 kPa, 0, 1) at the compressor outlet,
  first-order lag 0.08 s. Proportional → boost creeps above the spring as flow rises; an undersized
  wastegate cannot hold boost at all (boost creep).
- ECU (only with `boost_control` hardware and a `boost_target_kpa` table, above 80 % pedal — below it the
  solenoid releases and the spring alone sets boost): PI loop on the ECU's **MAP sensor reading** (error
  normalised by the 20 kPa actuator span, integral gain 3/s). A target above the sensor's range is never seen
  as reached: the gate stays shut and the engine over-boosts (validator warning
  `boost_target_above_map_sensor`). The solenoid can only
  keep the gate shut longer (`opening = min(mechanical, PI)`), so targets below the spring are
  unreachable (validator warning). Anti-windup: the integrator stops while the output is pinned in the
  direction of the error (gate held shut during spool, or at the mechanical limit), so a tip-in peaks
  within ≈ 3 kPa of the target instead of spiking.
- Surge (a throttle lift at boost — there is no blow-off valve) wears the turbo's bearings at
  `0.002/s · surge depth · (PR − 1)`; bearing drag grows × (1 + 3·wear), so a surge-abused turbo spools
  more slowly. Inspection reports the worn bearings.
- A stock ECU with a 105 kPa MAP sensor cannot see boost: it meters fuel and picks spark as if at
  105 kPa → lean and over-advanced under boost (validator warning `map_sensor_range`).

### Calibration reference (forged K20, 550 cc, standalone ECU, RON 98, 175 kPa target)
Steady-state full boost ≈ 3800 rpm (small), ≈ 4600 rpm (mid), ≈ 7200 rpm (big: ≈ 157 kPa at 6800 rpm);
peak ≈ 205 hp (small: near choke above 5500 rpm, efficiency falling to ≈ 0.37, drive pressure ≈ 1.38× boost),
≈ 238 hp (mid), ≈ 244 hp at 7200 rpm (big, only just at full boost). Asked for 200 kPa, the small turbo over-speeds
(≈ 1.09× rated at 7000 rpm, 193 kPa) and fails (`TurboOverspeed`, with a report); the mid turbo holds 240 kPa at
7000 rpm at 1.015× its rating. (Re-measured in the validation pass, after the exhaust-port heat exchange.)

## Damage and failure (`Damage/`)
Every step, `StressEvaluator` turns the operating point into stress ratios `r = load / rating`:

| Mode | Load | Rating (part) |
|---|---|---|
| Rod tensile | `m_recip·ω²·r·(1+r/l)` | `max_tensile_load_kn` (rods) |
| Rod compressive | `PCP·A_piston − inertia` | `max_compressive_load_kn` (rods) |
| Piston pressure | PCP | pistons `max_cylinder_pressure_bar` |
| Piston crown | crown °C | `max_crown_temperature_c` |
| Head gasket | PCP | gasket `max_cylinder_pressure_bar` |
| Block deck | PCP | block `max_cylinder_pressure_bar` |
| Crank overspeed / torsion | rpm, torque | crank `max_rpm`, `max_torque_nm` |
| Flywheel | rpm | flywheel `max_rpm` |
| Rod / main bearings | peak rod force (mains 60 %) | bearing `max_load_kn` |
| Valve–piston contact | rpm | 110 % of the valve-float speed |
| Turbo overspeed / turbine temperature | shaft rpm, inlet °C | turbo `max_shaft_rpm`, `max_turbine_inlet_temperature_c` |

Damage per mode accumulates on the part instance (`PartDamage`), deterministically, by one of three
laws (`FailureModeInfo`, `DamageLaw`):

- **Cycle fatigue** (Miner's rule, Basquin-type): each load cycle at stress ratio `s` uses
  `x^6 / N_rated` of the part's life, `x = (s − e)/(1 − e)`; nothing below the endurance limit `e`,
  one smooth curve through the rating (no kink), instant fracture at `s ≥ 1.3`. Cycles: per
  combustion (`rpm/120`: rods, pistons, gasket, block; `N = 3·10⁴`, `e` 0.80/0.85), per firing
  (`rpm/120 · cylinders`: crank torque, `N = 1.2·10⁵`), per revolution (bearings, crank speed,
  flywheel `N = 10⁵`; gear teeth per input-shaft / pinion revolution `N = 2·10⁵`, `e` 0.9, instant at
  2×). Rods at the rating last ≈ 9 min at 7000 rpm, at 90 % ≈ 9 h, at 110 % ≈ 45 s; running the
  same load at twice the speed halves the running time to failure. **Deliberate simplification:** the
  exponent acts on the overstress past the endurance limit, not on `s` itself, so the curve is much
  steeper near the rating than a plain Basquin `s^−6` (life ratio 100 % → 110 %: rods ≈ 11×, gasket and
  gearbox ≈ 64×, turbo overspeed with speed² stress ≈ 900×, against 1.8× for `s^6`). Lives are
  game-compressed (minutes at the rating, not 10⁶–10⁷ cycles). Identical sessions add identical damage
  (tested across a save/load); the engine's thermal state restarts warm each session, so thermally
  activated damage in split sessions is slightly lower than in one long run.
- Parts **rated in rpm** (crankshaft, flywheel, turbo) are stressed with the square of speed
  (centrifugal/inertia stress ∝ ω²): `s = (rpm/rating)²`, so a flywheel bursts at 114 % of its rated
  speed (stress 1.3). The crank's speed rating is a fatigue limit (instant only at stress 1.6).
- **Stress rupture** (turbo overspeed: a steady load on a hot spinning wheel creeps rather than
  cycles): the same power law per second, `e` 0.90 (stress), 30 min at the rating.
- **Thermal** (piston crown, turbine inlet): Arrhenius in kelvin, `rate = exp(Θ·(1/T_rated − 1/T)) /
  t_rated`; crown `Θ` = 20 000 K, 20 min at the rating (280 °C on a 300 °C piston ≈ 70 min, each 10 K
  hotter ≈ ×1.8 faster), melting 100 K over the rating; turbine `Θ` = 50 000 K, 30 min, +150 K. The
  old model divided Celsius values ("280 °C is 93 % of 300 °C") and lasted 9 minutes there.
- **Valve–piston contact** begins at 103 % of the valve-float speed (`ValveContactOnset`, the valves
  hanging open past the valve-to-piston clearance), per revolution, `N = 300`, instant at 1.05 of
  the reading (110 % of float speed).
Special processes:
- **Detonation**: per knocking combustion (`rpm/120`), pistons `10⁻⁴·KI²·(120 bar / piston
  rating)^1.5`; gasket `2.5·10⁻⁵·KI²`; rod bearings `1.5·10⁻⁵·KI²` (the same per-second rates as
  before at 2500 rpm); ring wear `0.0005·KI` per s.
- **Lubrication**: bearing wear `0.05·deficit²·(0.3 + load ratio)` per s, deficit = `1 − p_oil/p_req`,
  plus `0.0005` per 10 K of oil above 150 °C; bearing wear ≥ 1 → spun bearing (oil starvation).
- **Overheating**: gasket `0.01·(T_coolant − 115 °C)/10 K + 0.05·coolant_lost` per s; cylinder head
  warp `0.01` per 10 % of coolant lost beyond 20 %.
- **Valve float**: spring wear `0.1·(rpm/float − 1)` per s.

Failures:
- **Catastrophic** (rods, pistons, block, crank, flywheel, bearings, bent valves): the engine seizes
  and cannot run until the part is replaced. Collateral damage is applied (a broken rod destroys the
  pistons and the block and gouges the crank; a spun bearing scores the crank journals; bent valves
  mark the pistons; ...).
- **Degraded** (head gasket, warped head, turbo): the engine runs with a penalty — blown gasket
  ×0.75 efficiency and +30 % heat into the coolant; warped head ×0.9; failed turbo makes no boost.
- Each failure produces a `FailureReport`: cause, measured values vs ratings, contributing factors
  drawn from the operating history (`OperatingHistory`: extremes, knock seconds, time below required
  oil pressure, coolant lost, ...), recommendations (e.g. safe rpm for the installed rods, rating to
  buy, timing to remove), and collateral damage. Diagnosis uses the state *before* collateral damage.
- Every part keeps a **damage ledger** (`PartDamage.Exposure`, saved with the part): per mode, the peak
  load ratio seen while taking damage and the time spent taking damage (faster than 10 h of life).
  Reports quote it with the fatigue carried in from earlier sessions ("70 % of its life already used
  before this run … the final load only finished it off"), so a failure several sessions in the
  making explains itself.
- Live `EngineWarning`s (knock, low oil pressure, oil surge, coolant/oil temperature, coolant loss,
  lean under load, fuel limits, MAP saturation, valve float, surge, EGT). Stress warnings come from the
  damage law itself: caution when the part would last under an hour at the current load, danger under
  two minutes or above the rating, with the remaining life in the text.
- `PartInspector` reports visible signs once fatigue passes 25 % (minor) / 60 % (major) and wear
  findings (bearing clearance, ring wear, spring sag, ...); `EngineDiagnostics.CompressionTestBar`
  gives a cranking compression figure that drops with ring wear, a blown gasket, bent valves.

## Vehicle dynamics (`Vehicles/`)
A planar (x, y, yaw) chassis coupled to the engine model. `VehicleSimulation.Step(dt)` runs the engine
once, speed-held at the current crank speed, then 8 driveline/chassis substeps.

### Tyres (`TireModel`)
- Normalised combined slip: `sx = κ/κ_peak`, `sy = tan α / tan α_peak`, `s = |(sx, sy)|`.
- `F = µ(Fz)·Fz·sin(1.5·atan(B·s))` with B chosen so the peak is at s = 1; sliding force falls to
  ≈ 77 % of peak at 10× peak slip (→ 71 %). Force is split along the slip direction (friction ellipse).
- Load sensitivity: `µ = µ₀·(1 − k·log₂(Fz/F_z0))`, clamped to [0.3, 1.3]·µ₀, with the tyre's nominal
  load `F_z0 = 3500 N · width/205 mm` (Pacejka's nominal load scales with tyre size: load rating and
  the width of the patch carrying it). At the same load a wider tyre sits further down its
  load-sensitivity curve, so it grips more and loses a smaller share of grip to load transfer:
  165 → 225 → 305 mm (same radius) gives 0.87 → 0.92 → 0.97 g on the skidpad. `k` is a property of
  the construction/compound, not of the size.
- Peak slip angle scales with `(205 mm / width)^0.5` (and with pressure, below): a wider, shorter
  patch on a wider belt is stiffer in cornering. The authored `peak_slip_angle_deg` is the
  construction's value at 205 mm.
- Slip: `κ = (ω·r − u_w)/max(|u_w|, 2 m/s)`, `α = atan2(v_w, max(|u_w|, 2 m/s))` (low-speed guard).
- Rolling radius from the size (`rim/2 + width·aspect`). Mass, inertia, rolling resistance and
  thermal mass are authored per tyre (a wider tyre's weight is its trade-off).
- Not modelled: relaxation length (forces follow slip instantly; the 8 chassis substeps keep this
  stable), aquaplaning, aero effect of width.

### Tyre temperature and pressure (`TyreThermalModel`, `TireModel`)
- One lumped temperature per tyre (tread + carcass, `thermal_mass_j_per_k`):
  `C·dT/dt = 0.7·P_sliding + P_rolling − (8 + 1.5·v)(T − T_amb)`, with `P_rolling = C_rr(p)·F_z·v` (so a
  soft tyre flexes and heats more). Street tyres settle around 75 °C at the test driver's pace.
- Pressure is set cold (at 25 °C) and follows the ideal gas law: `p_abs = p_cold,abs · T / 298.15 K`.
  Cold 175 kPa → ≈ 221 kPa at 75 °C.
- Grip × `1 − loss·(1 − exp(−((T − T_opt)/window)²))`: street (75 °C, ±40, 10 %), sport (80 °C, ±30,
  15 %), semi-slick (90 °C, ±22, 25 %) — cold semi-slicks are poor until warm; overheated tyres fade.
- The authored friction is the tyre at its optimum temperature and pressure; simulations start with
  warm tyres, a drive from the garage starts at ambient (an out lap).

### Inflation and camber (`TireModel`, set-up)
- Pressure error `e = (p − p_opt)/p_opt` (hot pressure): grip × `(1 − 0.8e²)`; peak slip angle × `(p_opt/p)^0.5` and
  peak slip ratio × `(p_opt/p)^0.3` (a soft carcass needs more slip: lazier response); rolling resistance
  × `(p_opt/p)^0.6`; tread wear × `(1 + 3e²)`.
- Camber relative to the road for each wheel = static camber − side × roll × (1 − camber gain), roll =
  lateral load-transfer moment / total roll stiffness. The lateral force is multiplied by
  `(1 − L(Δ))/(1 − L(2.5°))` with `L(Δ) = 0.08·(1 − exp(−(Δ/2.5°)²))` and Δ the distance from the
  optimum (leaning 2° into the force). The reference offset keeps a typical road alignment at the
  authored grip; a good alignment gains up to ≈ 5 %, leaning out of the corner loses. Longitudinal force
  × `(1 − 0.012·|camber|)/(1 − 0.006)`; tread wear × `(1 + 0.08·max(0, |static camber| − 1°))`.
- Brakes: rear torque × `rear_pressure_factor` (balance bar). With the big brake kit the rears lock
  first above ≈ 1.5.

### Chassis
- Mass = curb mass + (fitted engine parts − factory engine parts) + (fitted chassis parts − factory);
  engine mass changes act on the front axle. CG height moves with ride height (×0.8).
- Body: `u̇ = F_x/m + r·v`, `v̇ = F_y/m − r·u`, `ṙ = M_z/I_z`; aero drag `½ρ·Cd·A·u|u|`.
- Load transfer targets: longitudinal `−m·a_x·h/L`, lateral `m·a_y·h` split between axles by roll
  stiffness `K_φ = k_wheel·t²/2 + ARB`. Both follow damped second-order responses whose natural
  frequency and damping ratio come from spring rates, dampers and estimated roll/pitch inertia —
  stiffer suspension transfers load faster; more front roll stiffness gives more understeer.
- Steering: front wheels (no Ackermann), 50 ms actuator lag.

### Ride and mechanical grip (`RideModel`)
The planar model has no wheel hop, so road roughness enters through a linear quarter-car per axle,
solved once per set-up in the frequency domain and scaled at run time:
- Sprung mass = static corner load/g − unsprung; unsprung = tyre pair/2 + brakes/4 + ½·suspension/4
  + 12 kg hub; spring = wheel rate; damper per wheel; tyre radial rate `4.4 · p_hot · width`
  (≈ 200 N/mm for 205 mm at 220 kPa).
- Road: ISO 8608 `G(n) = G₀·(n/0.1)⁻²` — circuit asphalt 4·10⁻⁶ m³, kerbs 256·10⁻⁶, grass 1024·10⁻⁶.
  Road velocity is then white noise of intensity `(2π)²·G₀·0.01·v`, so every variance is exactly
  proportional to speed: `σ_Fz = √(v·G₀·I)`, `I` = ∫|H(f)|² over 0.05–200 Hz (600-point log grid).
  Checked against a time-domain Monte Carlo quarter car to 10 %.
- Uncorrelated left/right bumps are half heave (anti-roll bar idle) and half roll (bar adds
  `2·K_arb/t²` to the wheel rate), so bars cost grip on bumps.
- Bump travel = vehicle `bump_travel_mm` + ride-height offset. Each corner's compression from roll
  (`θ·t/2`) and pitch (`ΔF/2k`) against its travel, with the roughness spread of suspension travel
  (floor 2 mm), gives the share of time on the bump stop `P = Φ((δ − travel)/σ_δ)`; on the stop the
  wheel rate gains 300 N/mm and `σ² = (1 − P)·σ²_free + P·σ²_stop`.
- Grip × `1/(1 + 0.6·(σ_Fz/Fz)²)` on every tyre force (load sensitivity plus force lag after load
  dips; the 0.6 is empirical). Telemetry: `MechanicalGrip`, `Bottoming` per wheel.
- Consequences: on circuit asphalt the effect is ≈ 0.1 % (a smooth skidpad still rewards stiff and
  low, as it does in reality); on kerb-grade roughness the OEM suspension keeps ≈ 93 % grip and track
  coilovers ≈ 88 %, damping has an optimum near ζ ≈ 0.3 inside the coilovers' range, and the lowest
  ride height lands on the bump stops (−60 mm: 0.826 g vs 0.848 g at −50 mm). The chassis bench reports
  a bumpy skidpad beside the smooth one.
- Not modelled: roll centres / geometric load transfer (all transfer is elastic and delayed), per-corner
  vertical dynamics in the time domain, discrete kerb strikes, aero platform.

### Driveline
- Clutch: torque `clamp(K·(ω_engine − ω_gearbox), ±engagement·capacity)`, K = 0.8 × reduced inertia /
  substep (stiff but stable). The engine is integrated on its side of the clutch with its rotating inertia.
- Automatic clutch with manual gears: open in neutral and during shifts (shift time per gearbox, throttle
  cut), slips to launch from rest (engagement rises with engine speed), opens to avoid stalling. After a
  gear change on the move it re-engages smoothly: until engine and gearbox are within 50 rpm it passes at
  most |engine torque| + 80 N·m (a good driver's engagement), so shifts do not shock the gearbox.
  Launches from rest are not limited. That controlled sync slip heats and wears the facings like any slip but is not a
  "clutch slipping" warning or failure cause — unless the clutch itself cannot hold the sync torque (until the second
  engine family, whose heavier flywheel and wider first-to-second step make the sync last > 0.3 s, it was flagged on
  every upshift).
- Gearbox efficiency applied to drive torque; gearbox input inertia reflected onto the driven wheels.
- Differential: open (equal torque), clutch LSD (locking torque = preload + locking fraction × input
  torque, accel/decel separately) or locked; implemented as a clamped coupling between the two wheels.
- Brakes: per-wheel torque from pedal × per-axle maximum (no ABS; locking the fronts is possible);
  handbrake on the rear; rolling resistance torque. Brake torque can stop but never reverse a wheel.

### Heat and wear of the friction parts (`ChassisWearModel`)
Every joule the clutch, brakes and tyres dissipate heats a lumped mass and wears the friction
material; hot material fades and wears much faster, so abuse snowballs. Wear lives on the part
instances (persists in the garage and saves).
- Energy per step: clutch `|T_clutch·(ω_engine − ω_gearbox)|`; brakes (per axle) `T_pedal·|ω_wheel|`
  (a locked wheel puts its energy into the tyre instead); tyres `|F_x·(ωR − u)| + |F_y·v|` per wheel.
- Clutch: `C·dT/dt = P − 15 W/K·(T − T_amb)`. Capacity `= rated · (1 − 0.5·wear) · (1 − 0.4·s)`,
  `s = smoothstep((T − T_fade)/200 K)`. Wear rate `= P/life · (1 + max(0, T − T_fade)/25 K)²`. At wear 1 the
  clutch burns out (capacity 8 % of rated) with a report built from the peak engine torque while
  slipping and the wear when slipping began. "Slipping" = commanded fully engaged, |slip| > 100 rpm.
- Brakes (per axle): `C·dT/dt = P − (10 + k·v)(T − T_amb) − 0.15 m²·σ(T⁴ − T_amb⁴)`, k = 3.5 W/K per
  m/s front (vented), 2.0 rear. Torque `× (1 − 0.45·smoothstep((T − T_fade)/250 K))`; pad wear
  `= Σ P·(1 + max(0, T − T_fade)/100 K)² / (2·pad_life)`; worn out → 40 % torque.
- Tyres (per axle pair): grip `× (1 − 0.10·wear)`, wear `= Σ P / tread_life`; worn out → grip × 0.75.
- Warnings: clutch slipping (> 0.3 s), clutch above its fade temperature, brakes fading, tyres > 85 %.
- Gearbox and differential overload: gearbox input torque (= clutch torque) and differential pinion
  torque (= axle torque / final drive) are low-passed (50 ms: shorter spikes are shared by several teeth
  and the shafts' wind-up) and compared with the part's `max_torque_nm`. Fatigue uses the engine's
  stress-ratio law with endurance 0.9, 300 s to failure at the rating, instant failure at 2×. A broken
  gearbox or differential means no drive. Traction limits what reaches them in the low gears (a clutch
  dump on street tyres just spins the wheels); the realistic overload is engine torque above the rating
  (a T35 on E85 at 300 kPa puts ≈ 445 N·m through the 400 N·m stock box near peak torque: ≈ 0.8 % of its life per
  fourth-gear pull, about a hundred hard pulls; a 360 N·m box breaks in ≈ 7; the 550 N·m dog box takes no damage).
  Until the validation pass a test claimed "a few pulls" for the stock box — true only because its harness never
  lifted between pulls, so the turbo met 4,400 rpm at full shaft speed and over-boosted.
  The report names engine torque vs rating, and clutch capacity vs rating when the clutch
  could pass more than the gearbox can take.
- Reference: the stock car at the test driver's pace runs its front brakes at ≈ 240 °C and wears
  ≈ 0.3 % of the pads and ≈ 0.25 % of the tyres per lap. The T28 turbo build (≈ 290 N·m) slips the OEM
  clutch (280 N·m new) a little; on the project car's 40 %-worn clutch (224 N·m) it slips, passes 250 °C
  in two laps and burns out in about six; the sport clutch (500 N·m) never slips.

### Engine coupling
- The engine sees the crank speed imposed by the clutch — a missed downshift drags it past the rev
  limiter (fuel cut cannot prevent it) and the damage model reacts (valve float, rods, ...).
- Radiator air speed = vehicle speed + 2 m/s (fan). Sump acceleration = |(a_x, a_y)| (oil surge).

### Test facility and test driver
- `TrackLayout.TestFacility()`: 1083 m circuit built from straights and constant-radius arcs (R25
  hairpin complex, R45 chicane, R55 sweeper, R20 final corner), 11 m wide, closes exactly. Surface
  grip multiplies tyre forces: asphalt 1.0, kerb (1.2 m) 0.9, grass 0.55.
- `TrackDriver`: pure pursuit (look-ahead 8 m + 0.6 s) with a speed-scheduled lateral-offset
  correction; target speed from exact curvature (`√(a_lat/κ)`) with backward braking passes; planned
  lateral/braking g = 0.8/0.78 × tyre µ; throttle cut on wheelspin or body slip; shifts judged from
  gearbox-side speed; restarts a stalled engine; caps throttle to the friction circle
  (`≤ max(0.15, √(1 − turning²))`) so boost arriving mid-corner does not spin the car. Used for
  lap-time regression tests and demos. In a `DrivingSession` the autopilot recovers to the track
  after 2 s lost (> 4 m beyond the kerbs, heading error > 115°, or stuck).
- `LapTimer` counts a lap only if the car passed the far side of the circuit since the last crossing
  (a spin back and forth over the line is not a lap); a recovery voids the lap in progress.
- Reference laps: stock ≈ 52 s, semi-slicks ≈ 48 s.

### Calibration reference (stock Kestrel S2, street tyres)
0–100 km/h ≈ 8.5 s, top speed ≈ 220 km/h (drag-limited), 100–0 ≈ 48 m threshold braking (with a
0.3 s pedal ramp) vs ≈ 54 m locked, skidpad ≈ 0.9 g (≈ 1.15 g on semi-slicks), mild understeer at the
limit. ≈ 80 µs per 2 ms step including the engine (8 chassis substeps, ride model, tyre thermal).

## Clamps, guards and calibration constants
Every `Clamp`/`Min`/`Max` in the simulation was reviewed in the validation pass. Three kinds remain:

- **Physical limits (they are the model):** wheel load ≥ 0 (a wheel lifts), clutch and LSD torque ≤ capacity,
  choked orifice flow at the critical pressure ratio, the oil relief valve, shaft kinetic energy ≥ 0, injector
  duty ≤ 100 %, ECU actuator ranges (idle valve, wastegate solenoid, knock retard ≤ 10°), intercooler
  effectiveness < 0.95, the turbo overspeed and turbine temperature failures (real limits reported as failures,
  not clamps — the 2× shaft-speed clamp of PR #1 is gone).
- **Numerical (class B):** the low-speed slip-ratio reference, the Brent brackets (the compressor's
  `MaxPressureRatio` provably bounds every operating point — tested), exponential-lag factors.
- **Guards on fitted laws** — tested not to act in normal running (`ClampActivationTests`); debug flags say when
  they do:
  | Guard | Reached when | Telemetry / helper |
  |---|---|---|
  | VE shape floor 0.25 | far outside every shipped cam's rev range | `VeFloorActive`, `AirPath.VeShape` |
  | Wall-heat scaling in `SplitHeat` | never across NA/built/turbo envelopes | `WallHeatLimited` |
  | Charge temperature ≥ 200 K | never (tested > 250 K across the envelopes) | — |
  | Spark factor ≥ 0.1, knock torque ≥ 0.5, PCP timing ×[0.3, 2] | 55°/50°/28–40° off MBT (never on shipped tunes) | — |
  | Tyre μ ∈ [0.3, 1.3]·μ₀ (load sensitivity) | stock car never; semi-slicks on track coilovers ≈ 2 % of wheel-steps, ≈ 1 % of tyre force (unloaded inside front) | `TireModel.FrictionCapped` |
  | Turbine efficiency ≥ 0.25·η_peak below the optimum BSR | spooling from standstill and idle, never on boost | `TurbineBladeSpeedRatio`, `TurbineOnEfficiencyFloor` |
  | Front weight share ∈ [25, 75] %, CG ≥ 15 cm | no shipped part combination | `VehicleConfiguration.WeightDistributionClamped`, `CgHeightFloored` |
  | Knock mixture term λ ∈ [0.6, 1.3]; coolant wall heating only above 90 °C | outside the correlation's range / a cold engine (a colder wall does not *reduce* knock — known asymmetry) | — |
  | Tuned piston speed ≥ 2 m/s (cam timing) | the M54's intake fully advanced at high speed; never on either shipped calibration | `CamTuningFloorActive` |

**Calibration constants.** Classes: (A) physical constants (gas constants, c_p, γ, Stefan–Boltzmann, heating
values, Thornton's 406 kJ/mol O₂, Douaud–Eyzat); (B) numerical (tolerances, step sizes); (C) empirical
correlations with a physical form, scaled by geometry where the physics says so; (D) gameplay tuning. Class C
constants that set overall levels and were fitted to the stock K20's published output: the Otto realisation
factor 0.80 and the FMEP coefficients (together they set ≈ 148 hp) — they are the engine-family calibration and
the first thing a second family would re-fit — the second family (below) was run through them **unchanged**, which lands
it at −10 % power and +1 % torque against its real reference. The duration correlation's reference centreline (112°,
from the K20 OEM cams) joins this class. Made size-aware in the validation pass (K20 unchanged within
0.1 %): the valvetrain FMEP (valves, cylinder volume), block/sump/oil↔coolant conductances (∝ (V/2 L)^(2/3)), and
the exhaust-port conductance (∝ bore² × cylinders). Still K20-sized and documented as debt: the piston-crown
correlation's reference heat flux (14.2 MW/m² ↔ +150 K, dimensional, not size-specific but fitted on one engine),
the header/runner tuning constants (in piston-speed and length terms, fitted on one family), the oil conductance
reference (scaled by displacement). Class D (explicit gameplay): failure severities (a blown gasket × 0.75
efficiency and × 1.3 coolant share, a warped head × 0.9), game-compressed fatigue lives, wear rates.

## Calibration reference (stock Kestrel K20, RON 95, 90 °C coolant)
Pinned loosely by tests (`EngineOutputTests.StockEngineCalibration`):
peak torque ≈ 189 N·m at ≈ 4000 rpm, peak power ≈ 110 kW (148 hp) at ≈ 7000–7500 rpm,
peak VE ≈ 0.93, WOT peak cylinder pressure ≈ 60–66 bar, hot oil pressure ≈ 1.3 bar at idle and
5.2 bar (relief) above ≈ 4000 rpm. Unchanged by the second-family milestone: stock (RON 95 and 98), race-cam and T28
turbo builds give bit-identical steady-state sweeps and dyno runs before and after it (the cam-timing terms are exactly
zero for cams without authored centrelines); `EngineAgnosticTests` pins the dyno peak to the pre-milestone value.

## Second engine family (Isar M54 = BMW M54B30 reference)
The architecture test of the second-family milestone: a real engine (PARTS_DATABASE.md, "Isar M54 reference engine")
represented through content only, run through every generic system, compared with its published figures. No
simulation code knows it; the one missing abstraction it exposed — cam timing — was added generically (above).

**Experiment A0 (before cam timing existed):** the same content with its true 1 mm durations and the old implicit
112° centreline gave 296 N·m (−1 %) but 130 kW at 5,500 rpm (**−23 %**): the fixed-cam correlation could place the
filling peak at the torque peak or at the power peak, not both. A single "effective" duration would have meant
authoring a false cam. With the intake phaser (60°, schedule from `calibrate-cams`) the engine is represented by its
real cams.

Acceptance bands (set from what a mean-value model with constants fitted on another engine can claim, and pinned by
`M54ReferenceTests`): geometry exact (displacement ±1 cc, bore/stroke/rod exact), compression ±0.05, peak torque and
torque at 3,500 rpm ±10 %, peak power ±15 % (a mean-value model with level-setting constants fitted on another
engine; the fictional K20's 148 hp is also ≈ 7 % under the real K20A3's 160 hp it resembles, though its parts were not
authored to that engine), peak-power speed 5,500–6,500 rpm, a broad NA curve (≥ 85 % of peak torque 1,500–5,000 rpm), idle at the tune's
700 ± 75 rpm.

| RON 98, 90 °C coolant | Reference | Model | Δ |
|---|---|---|---|
| Displacement | 2979 cc | 2979.3 cc | +0.01 % |
| Compression ratio | 10.2 | 10.20 (derived) | — |
| Peak torque | 300 N·m @ 3,500 | 303.9 N·m @ 2,000 (flat within 1 % 1,500–2,750) | +1.3 % |
| Torque at 3,500 rpm | 300 N·m | 291.1 N·m | −3.0 % |
| Peak power | 170 kW @ 5,900 | 153.0 kW @ 6,250 (dyno sweep 153.9 kW @ 6,475) | −10.0 % |
| Power at 5,900 rpm | 170 kW | ≈ 149.5 kW | −12 % |
| Torque at 6,000 rpm | ≈ 270 N·m (from 170 kW @ 5,900) | 240.0 N·m | ≈ −11 % |
| Rev limit | 6,500 rpm | fuel cut at 6,500 (150 rpm hysteresis); valve float ≈ 7,070 rpm | — |
| Idle | ≈ 700 rpm | 700 rpm at ≈ 15 kPa MAP, λ 1.02 | — |

Model reference (not a validation target): peak VE 1.00, peak BMEP 12.8 bar, WOT peak cylinder pressure ≤ 66 bar, oil
4.1 bar at 3,000 rpm and 4.7 bar at 6,000 (relief 4.5 bar + slope), rotating inertia 0.208 kg·m²; RON 95 and RON 91 lose
≈ 1 % at 2,000 rpm to knock retard and nothing at the power peak (octane only limits the NA engine at low speed; see
Known issues on the octane sensitivity). In the Isar C30 the autopilot laps the test facility in 52.1 s (the stock
Kestrel S2: 51.8 s).

Why the shape differs (a plateau from 1,500 to 2,750 rpm instead of a peak at 3,500): see "M54 torque-curve
investigation" below. Low idle MAP (≈ 15 kPa; K20 ≈ 21 kPa) comes from the absence of accessory load in the model.

Content-only variant (a test fixture, not shipped): the M54 turned by JSON edits alone into an 80 × 76 mm straight
eight (3,057 cc, 11:1, 7,500 rpm, 205° intake) builds, fires eight times per two revolutions, respects its own limit,
has less friction at the same speed (shorter stroke) and peaks later and higher; the M54 under renamed ids is
bit-identical (`EngineAgnosticTests`).

### M54 torque-curve investigation
Question (review of PR #4): the reference peaks at 300 N·m at 3,500 rpm; the model plateaus at ≈ 303 N·m from 1,500 to
2,750 rpm and falls from there (291 N·m at 3,500). Is the difference **(A)** wrong M54 content, **(B)** missing generic
physics or **(C)** an unavoidable simplification of the mean-value model?

Reference shape (web-search summaries of E46 dyno threads; no digitised factory curve could be opened): a double hump —
first peak at 3,500 rpm, a dip at ≈ 4,000–4,200 rpm where DISA switches (flap closed below ≈ 3,750 rpm, open above
≈ 4,100), a second hump, 170 kW at 5,900 rpm (≈ 275 N·m). No point values below 3,500 rpm were found, so the low end is
compared by shape, not by number.

Experiments: RON 98, warm, full load, steady state, damage off; wherever a change moves the optimum, the tune is
recalibrated with the dev tools (cams → VE → spark → VE). E1–E9 run the shipped code with content or tune edits.
E10–E11 ran a prototype of the missing term, which is not shipped.

| # | Experiment | Result |
|---|---|---|
| E1 | Shipped M54: phaser position and VE shape along the full-load curve | The phaser holds the filling peak on the engine speed, within one 2.5° map step (the correlation moves it ≈ 95 rpm per crank degree). The VE shape factor is 0.994–1.000 from 1,000 to 6,250 rpm: the engine fills as if tuned at every speed. What is left of the shape is flow loss, friction and knock |
| E2 | Cam calibration against the fixed-phase envelope (0–60° in 10° steps) | The shipped schedule traps the best fixed phase's air at every speed (1,500 rpm: 586.6 mg for both); the best phase moves from 50° at 1,000 rpm to 0° from 5,000 rpm. The calibrator adds nothing the physics does not offer |
| E3 | The same VE model on the K20 (fixed cams) | Shape 0.67 at 1,000 rpm, 0.81 at 2,000, 0.94 at 3,500, 1.00 at 5,500: the model is not flat by itself. The M54 with its phaser parked has the same hump (190 → 273 N·m from 1,000 to 3,500 rpm) |
| E4 | Intake runner 250 / 380 / 550 / 800 mm | Under VANOS the curves agree within 1 N·m up to 5,000 rpm. Parked, 2,000 rpm torque goes 234 / 241 / 248 / 256 N·m. The runner only shifts the one hump, and the phaser shifts it back |
| E5 | Phaser range 60 (sourced) / 40 / 25 / 0° | 1,000 rpm: 293 / 271 / 224 / 190 N·m; 2,000: 304 / 304 / 286 / 241; identical from 3,000 up. Only an unsourced cut to ≈ 25° makes the low end rise, and then the peak sits at ≈ 2,750 |
| E6 | The K20 given a phaser by content (park 20° late, 50° range, standalone ECU) | Fixed: 126 / 164 / 189 / 189 N·m at 1,000 / 2,000 / 3,500 / 4,000 rpm. Phased: 155 / 204 / 200 / 195. The same flattening, with the peak moving from 4,000 to 2,500 rpm: the effect is generic |
| E7 | Estimated flows at 6,000 rpm | Port flows +20 %: +4.5 % torque. Exhaust manifold + system +30 %: +0.5 %. Intake + throttle +30 %: +1.4 %. All together: +6.6 % (160 kW). At 3,500 rpm the same changes give +0.2 to +2.3 % |
| E8 | Low-speed spark: shipped map against MBT − 1° with knock control off | Knock binds below ≈ 2,500 rpm (1,500 rpm: 10.5° against MBT 21.1°). Spark at MBT knocks and gives less torque (1,500 rpm: 282 against 302 N·m). The shipped low end is knock-limited, not over-advanced |
| E9 | A 3,500 rpm filling resonance, emulated with the model's existing wave-tuning hump (gain 0.03 / 0.06 / 0.09) | 3,500 rpm: 291 → 299 / 308 / 316 N·m, and at the larger gains the peak moves to 3,000–3,500. 1,000–2,000 rpm is unchanged (293 / 302 / 304). Under a phaser a resonance can add a hump but cannot lower the plateau |
| E10 | Prototype (not shipped): the tuning curve split in two parts. A share *g* stays with the intake's gas dynamics, tuned at the speed of the same cam and runner installed straight up (3,314 rpm for the M54), which a phaser cannot move. The other 1 − *g* follows intake closing | *g* = 0 / 0.25 / 0.5 / 0.75. 1,000 rpm: 293 / 274 / 255 / 236 N·m. 2,000: 304 / 298 / 292 / 285. 3,500: 291 in all. 6,000: 239 / 231 / 222 / 213. Peak power: 152.6 / 146.2 / 139.5 / 133.9 kW. Peak torque at 2,000 / 2,500 / ≈ 2,750 / 3,000 rpm. The K20 is unchanged apart from floating-point rounding |
| E11 | E10 at *g* = 0.5 plus an idealised two-stage intake: at 3,900 rpm the gas-dynamic tuning switches to a shorter effective runner | 250 / 150 / 100 mm: peak power 144.8 / 148.9 / 151.0 kW; below the switch, as E10. The top end the split loses is DISA's open stage |

The intake cam schedule across speed and load: the correlation moves the filling peak ≈ 95 rpm per crank degree of
intake closing, so the sourced 60° of VANOS covers ≈ 900–5,400 rpm; the calibrated schedule uses 47.5° of it between
1,000 and 5,500 rpm and parks above. The VE shape has no load term, so the schedule is the same in every load row. A real
VANOS map also varies with load (internal EGR, idle quality), which is not modelled and has no effect at full load. At
idle the map advances fully where the real engine idles retarded; that is harmless here, because the M54's cams have no
overlap at 1 mm lift in any phase.

Conclusion:
- **(B) Missing generic physics is the root cause of the shape.** The VE model has one filling hump for intake closing
  and intake gas dynamics (runner ram and resonance) together, its height does not depend on speed, and a cam phaser
  moves all of it (E1). Consequences:
  - a phased engine fills at its ceiling at every speed — a phased K20 does the same (E6);
  - the runner has no effect under a phaser (E4);
  - a switched runner such as DISA has nothing to act on, so the reference's 3,500 rpm hump, 4,000 rpm dip and second
    hump cannot be expressed (E9).
- **Not (A) for the shape.** No sourced or estimated M54 value moves the plateau: the schedule (E2) is sourced in range
  and calibrated, the phaser range (E5) is sourced, and the runner (E4) is inert.
- **(A) in part for the top end.** The estimated flows are worth up to +6.6 % at 6,000 rpm (E7), about half of the
  −12 % there. They stay as estimated: raising them to close the gap would be fitting.
- **(C) for the rest of the level.** Speed-independent combustion efficiency and charge heating, the shared FMEP and the
  knock model's weak octane sensitivity set the absolute level for both families; the K20 also sits ≈ 7 % under its real
  counterpart at its power peak. The shared constants were not re-fitted to either engine.
- **The calibration methodology is sound.** The cam schedule is the fixed-phase envelope (E2), spark is knock-limited
  where it matters (E8), and the VE table only sets fuelling (λ): the calibrators do not create the low end.

Why E10 is not shipped, although it is the missing physics: it is necessary but not sufficient.
- On its own it makes the low end rise, as the real engine's does, but it costs 6–19 kW at the top. The real M54's top
  end comes from DISA's open stage (E11); at *g* = 0.5 the M54 would make 139.5 kW (−18 %, outside the ±15 % band).
- Completing it needs two values we do not have. The share *g* would be a new shared constant with no source and no
  second phased engine to check it on. DISA's open-stage effective runner length is not published. Choosing either to
  make the M54 match is the fitting the review forbids.

It is the next generic-physics task (ROADMAP, with its prerequisites). Until then the limitation is pinned by
`TorqueCurveDiagnosisTests`:
- filling at the ceiling under a phaser;
- the runner inert under a phaser;
- a phased K20 flattening;
- the cam calibration equal to the fixed-phase envelope;
- knock-limited low-speed spark;
- the top end's sensitivity to flow;
- the plateau-then-fall shape.

## Intake gas dynamics 2.0 — proposed model (Phase 0 specification, NOT implemented)
Status: written in Phase 0 of docs/milestones/INTAKE_GAS_DYNAMICS_2.md for review **before any code**. Nothing in this
section runs today; the model that runs is "Volumetric efficiency (tuning component)" above. The acceptance tests that
will hold this model are in `tests/CarSim.Core.Tests/Acceptance/` (pending until Phase 1).

### What the current model does, measured in Phase 0
The acceptance rig (`IntakeRig`: full-load sweeps with the cam phase and runner stage held; two runners on the same cams
at the same phase, so the valve-event part cancels) measured today's model:
- **Runner length:** the tuned speed goes as L^−0.254 — the `(0.300 m / L)^0.25` factor. Acoustic tuning goes as L^−½
  (Helmholtz) to L^−1 (quarter wave).
- **Charge temperature:** no effect on the tuned speed (heating the charge from ≈ 270 to ≈ 323 K moves nothing); the speed of
  sound, and so every tuned speed, goes as √T.
- **Cam phase:** with the intake cam at 50° advance the long and short runners never cross between 1,500 and 6,400 rpm;
  at 10° they do — the runner's effect travels with the cam (the phaser moves the whole hump).
- **Switched runner under a phaser:** a 380/250 mm two-stage intake on the M54 with its calibrated VANOS map changes
  full-load torque by less than 2 % at every speed (investigation E4: within 1 N·m).
- **A switch speed the model contradicts:** on `syn_i6_vis` (fixed cams, 450/250 mm) the switched stage makes less
  full-load torque than the primary at every speed below ≈ 6,400 rpm (5–10 N·m less from 1,000 to 4,500 rpm), so its
  shipped 4,600 rpm switch speed costs up to ≈ 2.6 % (6 N·m at 4,750 rpm), falling to nothing at ≈ 6,400 rpm
  (`carsim regenerate-tunes`, runner-switch step).
  Under a single hump a shorter runner simply moves the hump up; it cannot win in its own band as a real VIS does.

### Structure: three factors per bank, closed form, per step
`VE_dyn = η_ve · G_wave · (1 + scavenging) · float` (scavenging and valve float unchanged). One evaluation per bank per
step, no new root finds (the air-path solve already iterates on flow; `G_wave` depends only on speed, geometry, charge
temperature and the valve event, all known before the solve).

**1. Valve-event filling η_ve (what the cams do).** The existing parabola, re-read as valve timing only: it peaks at the
piston speed where the charge column's inertia still fills the cylinder at intake closing (IVC) instead of being pushed
back — later closing peaks at higher speed (Heywood 1988, §6.2.3, valve timing and VE). `x = v_p / v_ivc`, with
`v_ivc = v₀ + k_d·(D − 220°) + 2·k_d·Δ_IVC` (today's form, `Δ_IVC` as in "Cam timing"). What changes is the meaning of
`v₀` and `k_d`: today's 15 m/s and 0.15 m/s/° absorb the K20's runner (at the 0.300 m reference). With the runner term
separate they are **re-fitted once, jointly** on the K20, the M54 and the synthetic fixed-cam engines (class: fitted,
shared), never per engine. A cam phaser moves η_ve — and only η_ve.

**2. Runner/plenum wave gain G_wave (what the intake does).** The runner and the cylinder volume behind the open valve
form a Helmholtz resonator (Engelman 1973; the "two types of resonance" — runner/cylinder and plenum — of Thompson &
Engelman 1969). The induction pulse excites it once per cycle; the charge gained is proportional to the pressure at the
valve at IVC (Ohata & Ishida 1982: VE follows the dynamic inlet pressure at IVC).
- Speed of sound of the runner gas: `a = √(γ·R·T_man)` (γ = 1.4, R = 287 J/(kg·K), `T_man` the manifold temperature —
  after the intercooler on a boosted engine).
- Effective runner length `L_eff = L + δ·r`, `r` the runner radius, `δ` the end correction of the plenum mouth
  (≈ 0.61 unflanged, Levine & Schwinger 1948; ≈ 0.82–0.85 flanged, Rayleigh; proposal δ = 0.85 for a bellmouth into a
  plenum).
- Runner–cylinder resonance `f_H = (a / 2π) · √(A_r / (L_eff · V_eff))`, `A_r = π r²`, `V_eff = V_c + V_d/2` (the mean
  cylinder volume during induction, `V_d·(CR+1)/(2·(CR−1))` per cylinder).
- Tuned speed `N_t = 60 · f_H / K`. Engelman's published design rule corresponds to K ≈ 2.1 (the natural frequency
  about twice the crank frequency) — **to verify against the paper in review** (see Sources). Every quantity except K is
  geometry or physics; K is the one sourced-empirical constant.
- Response: a periodically forced damped oscillator, `r = N / N_t`,
  `M(r) = 1 / √((1 − r²)² + (2ζr)²)`, `φ(r) = atan2(2ζr, 1 − r²)`; the gain uses `M(r)/M(1)`, `M(1) = 1/(2ζ)` (the
  response at resonance; the true maximum, at `r = √(1 − 2ζ²)`, is within a few per cent of it for ζ ≤ 0.3).
- Gain: `G_wave = 1 + A · [M(r)/M(1)] · cos(φ(r) − φ_ivc)`, bounded to `[1 − A, 1 + A]`.
  - `φ_ivc` — where the valve event sits against the wave: the crank angle of IVC after BDC, expressed as a phase of
    the resonance period (`2π · θ_ivc,ABDC / (360 · K)`), zero at the reference closing the constant K was stated for.
    **This is the cam ↔ runner interaction:** moving IVC (phaser, VVL profile, a longer cam) changes the realised gain
    and its sign, but not `N_t`.
  - Amplitude `A = min(A_max, κ · M_r)`, `M_r = ū_r / a` the runner Mach number of the mean induction flow
    (`ū_r = V_d · (N/120) / (A_r · f_event)`, `f_event` the intake event fraction). Pressure waves scale with the
    acoustic impedance times the velocity they carry, `Δp ~ ρ·a·u`, so the relative gain goes as `u/a` (Winterbone &
    Pearson 2000, linear acoustics) — derived form, one shared fitted coefficient κ.
  - Damping `ζ` (wall friction, flow separation at the valve): shared fitted constant, bounded to [0.1, 0.5].
- **Plenum mode** (Thompson & Engelman's second resonance): the plenum volume `V_p` with the throttle bore (or the
  intake snorkel) as its neck, `f_P = (a/2π)·√(A_neck/(L_neck·V_p))`, entering `G_wave` as a second, weaker oscillator
  (amplitude share `s_P`, shared). Engines without a plenum (individual throttle bodies, `plenum_volume_l = 0`) have no
  plenum mode; their runners open to the airbox.
- **Switched geometry (N stages):** a stage is `{length_mm, diameter_mm, plenum_volume_l?}`; the ECU selects stage *i*
  above its switch speed with today's 150 rpm hysteresis. A DISA-type flap that connects two plenum halves is a stage
  whose *effective* resonance length/volume changes — authored as such, with provenance.
- **Boost:** `G_wave` acts on the runners downstream of the plenum; the gain is relative, so it scales with the boost
  pressure it multiplies; `a` follows the intercooled charge (hot charge → higher tuned speed). The plenum mode is off
  on boosted engines by default (the charge-air system's volume detunes it) — open question Q4.
- **Multi-bank:** each bank reads its own intake part (runner stages per bank); a shared plenum's volume enters each
  bank at its cylinder share, exact for alike banks (as every shared element today). Two alike banks = one bank of all
  cylinders (existing invariant).

**3. Defaults for engines without the new data** (documented, flagged by the validator as `default_intake_geometry`,
never silently the K20's): runner diameter `0.40 · bore` (typical intake-runner to bore ratios are 0.35–0.45 —
assumption A5), plenum volume = displacement (A6), end correction δ = 0.85. Existing `runner_length_mm` and
`switched_runner_length_mm` stay valid (a two-stage intake is the N = 2 case).

### Constants and their classes
| Symbol | Proposed value | Class | Source / reason |
|---|---|---|---|
| γ, R | 1.4, 287 J/(kg·K) | physical | air |
| `a = √(γRT)` | — | physical | ideal-gas speed of sound |
| Helmholtz `f = (a/2π)√(A/(L·V))` | — | physical | lumped acoustic resonator (Kinsler et al., *Fundamentals of Acoustics*) |
| δ (end correction) | 0.85 | physical, sourced | Rayleigh (flanged, ≈ 0.82–0.85); 0.61 unflanged (Levine & Schwinger 1948) |
| `V_eff = V_c + V_d/2` | — | derived | Engelman 1973 (mean induction volume) — verify |
| K | ≈ 2.1 | sourced-empirical | Engelman 1973 design rule — verify the exact constant |
| M(r), φ(r) | — | physical | forced damped oscillator |
| ζ | fitted, [0.1, 0.5] | fitted, shared | joint fit K20 + M54 + synthetic, never per engine |
| κ, A_max | fitted; A_max ≤ 0.15 | fitted, shared; bound | amplitude ∝ Mach (derived form); bound: assumption A3 |
| s_P | fitted | fitted, shared | plenum-mode share |
| v₀, k_d (η_ve) | re-fitted | fitted, shared | replaces 15 m/s / 0.15 m/s/° (which absorbed the K20's runner) |
| 112° reference ICL | kept | derived from the K20 OEM cams | as today (documented class C) |
| runner diameter default | 0.40·bore | assumption A5 | typical ratio; flagged when used |
| plenum default | 1.0·V_d | assumption A6 | typical; flagged when used |

Removed with the change: the `(0.300 m / L)^0.25` factor, `EngineConfiguration.ReferenceRunnerLength` and the reading of
the runner stage as a hump shift. Kept: `VeCeiling`, the parabola's side coefficients (re-examined in the joint fit),
the floor guard, scavenging and valve float.

### Bounds (tested by construction and by spec fuzz)
`0.25·ceiling ≤ η_ve ≤ ceiling`; `1 − A_max ≤ G_wave ≤ 1 + A_max`; `N_t` finite and positive for any accepted part
data (the validator bounds length 50–1000 mm, diameter 15–120 mm, plenum 0–30 L); `r` guarded at N = 0; no NaN or
negative pressure for any `T_man` in [200, 500] K. VE_dyn ≤ 1.35 is asserted by `IntakeGasDynamicsGuardTests`.

### Sourced facts vs model assumptions
Sourced (physics or literature):
- S1 speed of sound `a = √(γRT)`; S2 Helmholtz resonator frequency and end corrections (standard acoustics); S3 two
  resonance types in intake tuning, runner/cylinder and plenum (Thompson & Engelman 1969, ASME 69-DGP-11); S4 the
  runner–cylinder Helmholtz model and design rule for a tuned manifold (Engelman 1973, ASME 73-WA/DGP-2); S5 VE follows
  the dynamic inlet pressure at intake closing (Ohata & Ishida 1982, SAE 820407); S6 VE vs speed, valve timing and
  tuning effects (Heywood 1988, *Internal Combustion Engine Fundamentals*, §6.2); S7 wave action in manifolds, linear
  acoustics (Winterbone & Pearson 2000, *Theory of Engine Manifold Design*, SAE).
- Verification status: the bibliographic records of S3, S4, S5 and S7 were confirmed in Phase 0; the texts could not be
  opened from the dev container, so the **exact form and constants of Engelman's rule (K, V_eff) are cited from its
  published summary and must be checked against the paper before Phase 1 code**.

Model assumptions (not sourced; each named so a reviewer can reject it):
- A1 one lumped Helmholtz mode per runner stage (plus an optional plenum mode) represents the runner response of a
  mean-value engine; higher pipe-mode orders are not modelled.
- A2 the IVC phase coupling `cos(φ − φ_ivc)` with φ_ivc proportional to the closing angle.
- A3 A_max ≤ 0.15 (production intakes' tuning gains are of order 10 %; to be checked against S6 in review).
- A4 plenum mode off on boosted engines.
- A5, A6 the default geometry above.

### Not modelled
CFD or 1-D wave-action solvers; per-cylinder pulses and inter-cylinder interference on a common plenum (beyond the
plenum mode); pipe-mode harmonics; exhaust wave tuning beyond the scavenging bump; intake noise; EGR.

### Data needed from content (Phase 1)
Runner diameter and plenum volume (optional, defaults above); N-stage runners; the M54's DISA as an effective two-stage
resonance geometry **with provenance** — measured or estimated from the part, with its uncertainty and a sensitivity
run, **never back-solved from the dyno curve** (open question Q1).

### Open design questions (owner review before Phase 1 code)
- Q1 Where the M54 DISA's effective geometry comes from, and what provenance level is acceptable.
- Q2 Helmholtz-only (A1) or also a pipe mode for long runners.
- Q3 The joint fit's reference data: the K20 reference numbers, the M54's reference curve shape (shape criteria only,
  acceptance criterion 1) and synthetic sanity bounds — and whether the K20 may move by a documented generic correction
  (the milestone says it will; the fingerprint measures it).
- Q4 Plenum mode on boosted engines (A4).
- Q5 Whether tunes are regenerated under the old physics first (a separate, documented commit) so the Phase 1 physics
  diff is not mixed with recipe drift (see VERIFICATION.md: no shipped tune is an exact fixed point of its recipe today).

## Validation
Deterministic xUnit tests cover: unit conversions, compressible flow, root finding, geometry and
compression ratio, valvetrain, combustion functions, torque/power identity (P = T·ω), BMEP
identity, throttle/vacuum, exhaust restriction, cams/runners/heads, cam phasing and the M54 torque-curve diagnosis, fueling limits (injectors,
pump, calibration errors), speed-density fuelling (shipped tables on target, breathing mods, stroker
displacement, IAT compensation, recalibration, smooth part-load VE), knock and timing, rev limiter, valve float, rod loads, oil pressure,
thermal behaviour, starting/idle/revving, determinism, compressor speed lines/choke/surge, compressor
energy consistency, turbine power, spool order by turbo size, transient lag vs steady state, boost
control, boost creep, intercooling, stock-ECU MAP saturation, overlap reversion under drive pressure,
fatigue laws (per-cycle counting, smooth through the rating, speed² for rpm-rated parts, Arrhenius in kelvin), damage ledger across sessions and saves, survival within limits, over-rev → bent valves / flywheel / rods (weak-link order),
detonation, knock control protection, inspection before failure, turbo on stock ECU, oil starvation
and the baffled pan, overheating → blown gasket with power loss, turbo overspeed, repair restores
the engine, report content, warnings, compression test, damage determinism, tyre force shape,
combined slip, load sensitivity, acceleration, top speed, gearing, threshold vs locked braking,
compound grip, roll dynamics, LSD vs open differential, mass from parts, missed downshift over-rev,
oil surge in sustained corners, driving determinism, track closure and surfaces, clean autopilot
lap, lap time vs tyres and power, lap timer.

Added in the validation pass (see ROADMAP.md, "Validation pass", and ARCHITECTURE.md §7): the first law over a
warm-up with stored heat, brake efficiency under the Otto limit, no failure raising torque, exhaust-port heat
bounds and lift/re-apply without turbine damage; ECU observability (delivered fuel reconstructed from the ECU's own
inputs, coolant/regulator/density/dead-time errors, no hidden closed loop, MAP-fed boost control, the knock
level, measurable-only dyno logs); turbo invariants to 350 kPa (first/second law at every compressor point,
closed-loop matrix, square waves, target steps, overspeed failure, windmilling turbine); spec fuzzing of every
fitted part; clamp activation; tyre-width and suspension trade-offs; knock factors over an envelope grid and
the knock-limited range vs compression and octane; fatigue additivity across a save/load; save v3 → v4
migration. New regression tests were mutation-checked against the bugs they guard.

Added for the second engine family: the M54 against its published figures (topology, geometry, compression, start and
idle, rev limit, torque/power bands, curve shape, fuelling vs VE/injector scaling/dead time/fuel pressure/fuel
properties/MAP/IAT/coolant, timing and knock, detonation and over-rev failures, damage through save/load, deterministic
dyno); both families through one gameplay pipeline (build → drive → failure); identity invariance under renamed ids;
a content-only eight-cylinder variant; a source audit (no family token, content id or size-specific branch in `src/`);
the K20's pre-milestone dyno reference pinned; cam timing (straight-up cams untouched, degreed-in at the reference
identical to straight up, validation, overlap, phaser lag, ECU without cam control, filling low vs high, shipped cam and
spark maps not stale, the tuned-speed guard); spec fuzz and every-part-in-every-slot on the M54. Mutation-checked: a
family-id hack, a cylinder-count hack, the content loader dropping the cam table, a phaser without effect.
