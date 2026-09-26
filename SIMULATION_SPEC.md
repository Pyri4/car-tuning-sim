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
2. ECU pre-step: rev limiter (fuel cut, 150 rpm hysteresis), idle-air PI controller.
3. Air path solved quasi-statically at this speed and throttle.
4. ECU meters fuel and picks spark from its tables using the MAP it can *measure*.
5. Fuel system delivers what injectors and pump physically can.
6. Combustion → IMEP; pumping (PMEP) and friction (FMEP) → BMEP → torque.
7. Heat split to coolant, oil and exhaust; loads on rods and bearings; oil pressure.
8. Integrate temperatures, knock control, and speed (when free).

Outputs are an `EngineTelemetry` record (≈60 channels).

Performance: ≈30 µs per step on one core (Release), so the engine can run at hundreds of Hz
alongside vehicle physics.

## Geometry (`Engines/EngineGeometry.cs`)
- Swept volume per cylinder `V_d = π/4 · B² · S`; displacement `V = n · V_d`.
- Deck clearance `= deck_height − (S/2 + rod_length + compression_height)`.
- Clearance volume `V_c = V_chamber + V_gasket + V_deck + V_dish`, `V_gasket = π/4 · B_gasket² · t`,
  `V_deck = π/4 · B² · deck_clearance`.
- Compression ratio `CR = (V_d + V_c) / V_c` — always derived, never authored.
- Reciprocating mass `m_recip = m_piston + m_rod / 3`.
- Rod inertia load (tensile, TDC exhaust) `F = m_recip · ω² · r · (1 + r/l)`.

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
- Tuned mean piston speed `v = 15 + 0.15 · (intake_duration − 220°)` m/s → `rpm_cam = v·60/(2S)`.
- Intake runner shifts it: `rpm_peak = rpm_cam · (0.300 m / L_runner)^0.25`.
- Shape (x = rpm/rpm_peak): below peak `1 − a_lo(1−x)²` with `a_lo = 0.50 + 0.004·overlap°`
  (overlap costs low-rpm filling); above peak `1 − 0.25(x−1)²`; floor 0.25; scaled by 1.02.
- Header scavenging: `+ gain · exp(−((rpm − rpm_tuned)/(0.25·rpm_tuned))²)`,
  `rpm_tuned = 5.2e6 / primary_length_mm`.
- Valve float: above the float speed VE collapses by up to 60 % over the next 8 %.

### Residuals and reversion
With pressure ratio `r = p_exhaust_port / p_intake_port` and clearance share `c = V_c/(V_c+V_d)`:
`f_res = 1 − c · [(r^(1/1.3) − 1) + (overlap°/15) · max(0, r − 1.25)]`, clamped to [0.4, 1.03].
The first term is burnt gas left in the clearance volume; the second is exhaust pushed back into the
intake during overlap once exhaust pressure is well above intake pressure (tuned NA exhausts keep
pulse pressure favourable below the 1.25 margin; turbo manifolds exceed it).

## Valvetrain (`Engines/ValvetrainModel.cs`)
Harmonic lift profile over the advertised event (duration@1mm + 50°):
`rpm_float = (D/6) · √(F_open / (2π² · m_valve · L))`. Spring wear reduces force by up to 15 %.

## ECU (`Ecu/`)
- Tables over rpm × **measured** MAP (kPa): target λ, spark advance. Optional boost target vs rpm.
- MAP sensor reading is clipped at `map_sensor_max_kpa`. The speed-density air estimate is exact
  while the sensor is in range and under-reads by `MAP_read / MAP_actual` beyond it (stock ECU on
  boost → lean).
- Fuel command: `m_f = m_air_est / (λ_target · fuel_stoich_afr_calibrated)`; pulse width uses the
  calibrated injector flow (`injector_flow_cc_min`). Wrong injector scaling or fuel calibration gives
  the corresponding AFR error.
- Rev limit: `min(tune, hardware max)`, fuel cut with 150 rpm hysteresis.
- Knock control (if hardware + tune enable it): retard at 6°/s per degree of knock, up to 10°;
  recover at 1°/s.
- Idle: PI on idle-air valve while throttle < 2 %.

## Fuel system (`Simulation/FuelSystem.cs`)
- Commanded duty `= pulse_width / (120/rpm)`; physically capped at 100 % (static).
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
- Knock-limited spark advance (degrees):
  `24 + 1.3(RON − 95) − 3.2(CR − 10.5) − 14(p_port/1 atm − 1) − 0.25(T_charge − 313 K)
   − 0.20·max(0, T_coolant − 363 K) + 20(1 − min(λ, 1.3)) + 2.5(rpm − 3000)/1000
   − 1.5·max(0, deck_clearance_mm − 1) + 60·max(0, 0.45 − p_port/1 atm)`. The last term keeps light
  load (overrun, cruise below ~45 kPa) knock-free whatever the part-load timing: there is too little
  end-gas pressure to autoignite.
- Knock intensity `KI = max(0, advance − KLSA)` degrees. Effects: −1 %/° torque, +4 %/° peak
  pressure, +8 K/° piston crown temperature, +1 %/° heat to coolant (capped at 10°).
- `W_i = burned · LHV · η · f_λ · f_spark · f_knock`, `IMEP = W_i / V_d`.
- Peak cylinder pressure `= p_port·CR^1.3 + 3.4·IMEP·clamp(1 + 0.025(adv − MBT), 0.3, 2)`, × (1 + 0.04·KI).

## Pumping and friction
- `PMEP = p_exhaust_port − p_intake_port`.
- `FMEP [kPa] = 45 + 4.0·v_p·√(μ/μ_100°C) + 0.2·v_p² + 12·F_spring_open/560 N + 0.004·PCP[kPa]`.
- `BMEP = IMEP − PMEP − FMEP`, torque `T = BMEP · V / (4π)`, power `P = T · ω`.

## Heat and temperatures
- **Energy balance (exact every step):** `fuel power = brake power + heat to coolant + heat to oil +
  exhaust heat`. Released fuel power = burned fuel · LHV · `f_rich(λ)`, where for λ < 1 the oxygen is
  shared across the excess fuel and CO/H₂ leave with their heating value:
  `f_rich = (525 − 119/λ)/406` (406 kJ released per mol O₂ by full oxidation — Thornton's rule — and
  525 kJ per mol O₂ of deficit left in CO/H₂): 96 % at λ 0.88, 90 % at λ 0.75. `CombustionModel.SplitHeat`
  divides the released power into indicated work, coolant (`0.28 − 0.06·clamp(rpm/7000, 0, 1.4) + 0.01·KI`,
  × 1.3 with a blown head gasket), oil (0.03) and exhaust (the remainder, so the split cannot create
  energy). Pumping work leaves with the exhaust gas; friction heat goes 35 % to oil, 65 % to coolant.
  Motoring (fuel cut, ignition off), the gas picks up 30 % of the coolant-to-charge temperature
  difference from the chamber walls at the coolant's expense.
- Exhaust gas temperature `= T_charge + (exhaust heat − latent heat of the excess fuel)/(ṁ_exh·c_p)`
  (80 % of the unburned fuel's latent heat is absorbed in the cylinder). Richer mixtures run cooler
  from these two effects alone. Lag 0.2 s (gas), 1.5 s (sensor).
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
  `η_t = η_peak · max(0.25, 1 − ((BSR − 0.7)/0.5)²)`, BSR = turbine tip speed / √(2·Δh_s).
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
- ECU (only with `boost_control` hardware and a `boost_target_kpa` table): PI loop on compressor
  outlet boost (error normalised by the 20 kPa actuator span, integral gain 3/s); the solenoid can only
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
Steady-state full boost ≈ 3800 rpm (small), ≈ 4600 rpm (mid), above 6800 rpm (big: ≈ 155 kPa at
6800 rpm); peak ≈ 205 hp (small: near choke above 5500 rpm, efficiency falling to ≈ 0.4, drive pressure
≈ 1.35× boost), ≈ 233 hp (mid), ≈ 215 hp (big, not yet at full boost). Asked for 200 kPa, the small turbo over-speeds (≈ 1.15× rated at 7000 rpm) and
fails; the mid turbo holds 240 kPa at 7000 rpm just above its rating.

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

Fatigue per mode accumulates on the part instance (`PartDamage`), deterministically:
`rate = 0` for `r ≤ endurance`; `(1/T)·((r − e)/(1 − e))³` up to the rating; `(1/T)·(1 + 50(r − 1))²`
above it; instant failure at `r ≥ 1.3` (1.05 for valve contact). `T` is the life at exactly the
rating (10–120 s depending on mode, see `FailureModeInfo`). Endurance ratios: 0.80 rods/pistons/
bearings, 0.85 gasket/block/crank torque, 0.90 crown temperature, 0.95 crank speed/flywheel/turbine
temperature, 0.97 turbo speed.

Special processes:
- **Detonation**: pistons `0.002·KI²·(120 bar / piston rating)^1.5` per s; gasket `0.0005·KI²`; rod
  bearings `0.0003·KI²`; ring wear `0.0005·KI`.
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
- Live `EngineWarning`s (knock, low oil pressure, oil surge, coolant/oil temperature, coolant loss,
  lean under load, fuel limits, MAP saturation, valve float, surge, EGT, any stress above endurance).
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
- Load sensitivity: `µ = µ₀·(1 − k·log₂(Fz/3500 N))`, clamped to [0.3, 1.3]·µ₀.
- Slip: `κ = (ω·r − u_w)/max(|u_w|, 2 m/s)`, `α = atan2(v_w, max(|u_w|, 2 m/s))` (low-speed guard).
- Rolling radius from the size (`rim/2 + width·aspect`).

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

### Driveline
- Clutch: torque `clamp(K·(ω_engine − ω_gearbox), ±engagement·capacity)`, K = 0.8 × reduced inertia /
  substep (stiff but stable). The engine is integrated on its side of the clutch with its rotating inertia.
- Automatic clutch with manual gears: open in neutral and during shifts (shift time per gearbox, throttle
  cut), slips to launch from rest (engagement rises with engine speed), opens to avoid stalling. After a
  gear change on the move it re-engages smoothly: until engine and gearbox are within 50 rpm it passes at
  most |engine torque| + 80 N·m (a good driver's engagement), so shifts do not shock the gearbox.
  Launches from rest are not limited.
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
  (a T35 on E85 at 300 kPa, ≈ 475 N·m once spooled, breaks the 400 N·m stock box in a few pulls; the 550 N·m dog box
  survives). The report names engine torque vs rating, and clutch capacity vs rating when the clutch
  could pass more than the gearbox can take.
- Reference: the stock car at the test driver's pace runs its front brakes at ≈ 240 °C and wears
  ≈ 0.3 % of the pads and ≈ 0.25 % of the tyres per lap. The T28 turbo build (≈ 300 N·m) slips the OEM
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
limit. ≈ 40 µs per 2 ms step including the engine.

## Calibration reference (stock Kestrel K20, RON 95, 90 °C coolant)
Pinned loosely by tests (`EngineOutputTests.StockEngineCalibration`):
peak torque ≈ 189 N·m at ≈ 4000 rpm, peak power ≈ 110 kW (148 hp) at ≈ 7000–7500 rpm,
peak VE ≈ 0.93, WOT peak cylinder pressure ≈ 60–66 bar, hot oil pressure ≈ 1.3 bar at idle and
5.2 bar (relief) above ≈ 4000 rpm.

## Validation
Deterministic xUnit tests cover: unit conversions, compressible flow, root finding, geometry and
compression ratio, valvetrain, combustion functions, torque/power identity (P = T·ω), BMEP
identity, throttle/vacuum, exhaust restriction, cams/runners/heads, fueling limits (injectors,
pump, calibration errors), knock and timing, rev limiter, valve float, rod loads, oil pressure,
thermal behaviour, starting/idle/revving, determinism, compressor speed lines/choke/surge, compressor
energy consistency, turbine power, spool order by turbo size, transient lag vs steady state, boost
control, boost creep, intercooling, stock-ECU MAP saturation, overlap reversion under drive pressure,
fatigue curve, survival within limits, over-rev → bent valves / flywheel / rods (weak-link order),
detonation, knock control protection, inspection before failure, turbo on stock ECU, oil starvation
and the baffled pan, overheating → blown gasket with power loss, turbo overspeed, repair restores
the engine, report content, warnings, compression test, damage determinism, tyre force shape,
combined slip, load sensitivity, acceleration, top speed, gearing, threshold vs locked braking,
compound grip, roll dynamics, LSD vs open differential, mass from parts, missed downshift over-rev,
oil surge in sustained corners, driving determinism, track closure and surfaces, clean autopilot
lap, lap time vs tyres and power, lap timer.
