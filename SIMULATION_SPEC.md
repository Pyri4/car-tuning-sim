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
- **Charge temperature**: `T_man + 0.12·(T_coolant − T_man) − 5 K · charge_cooling_factor · (F/A)/(F/A)_stoich`.

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
   − 1.5·max(0, deck_clearance_mm − 1)`.
- Knock intensity `KI = max(0, advance − KLSA)` degrees. Effects: −1 %/° torque, +4 %/° peak
  pressure, +8 K/° piston crown temperature, +1 %/° heat to coolant (capped at 10°).
- `W_i = burned · LHV · η · f_λ · f_spark · f_knock`, `IMEP = W_i / V_d`.
- Peak cylinder pressure `= p_port·CR^1.3 + 3.4·IMEP·clamp(1 + 0.025(adv − MBT), 0.3, 2)`, × (1 + 0.04·KI).

## Pumping and friction
- `PMEP = p_exhaust_port − p_intake_port`.
- `FMEP [kPa] = 45 + 4.0·v_p·√(μ/μ_100°C) + 0.2·v_p² + 12·F_spring_open/560 N + 0.004·PCP[kPa]`.
- `BMEP = IMEP − PMEP − FMEP`, torque `T = BMEP · V / (4π)`, power `P = T · ω`.

## Heat and temperatures
- Coolant fraction of burned-fuel energy `0.28 − 0.06·clamp(rpm/7000, 0, 1.4) + 0.01·KI`;
  oil fraction 0.04; friction heat split half/half between coolant and oil.
- Exhaust gas temperature `= T_charge + E_exhaust/(ṁ_exh·c_p) − 300 K·max(0, 1 − λ)`, where
  `E_exhaust = fuel power − indicated power − coolant/oil shares`. Lag 0.2 s (gas), 1.5 s (sensor).
- Piston crown temperature `= T_coolant + 170 K·(q/14.2 MW/m²)^0.7·(1 + 1.5·max(0, λ − 0.9)) + 8 K·KI`,
  q = burned-fuel power per piston area. Lag 3 s.
- Coolant: capacity = coolant (1.05 kg/L, 3600 J/kgK) + half of block and head metal (900 J/kgK).
  Radiator `Q = UA · (v_air/v_ref)^0.6 · thermostat · (T_coolant − T_ambient)`,
  thermostat opening smoothstep over [T_open, T_open + 10 K]. Surface loss 15 W/K.
- Oil: capacity = sump oil (0.88 kg/L, 1900 J/kgK) + 8 kJ/K metal. Oil↔coolant exchange 120 W/K;
  sump convection 12 W/K·(1 + 0.1·v_air).
- Dyno cells can hold coolant temperature (`CoolantTemperatureOverride`).

## Lubrication
- Pump delivery `Q = displacement · rpm/60 · 0.9 · supply`, supply falls to zero over 0.3 g beyond
  the pan's `max_sustained_g` (pickup starvation).
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
- Corrected flow `ṁ_c = ṁ·√(T₁/298.15 K)/(p₁/1 atm)`; choke ratio `x = ṁ_c / choke_flow`.
- Tip speed `U = ω · D/2`; actual specific work `w = 0.75 · U² · ψ(x)`, `ψ = max(0.02, 1 − 0.95·x⁸)`
  (speed lines droop toward choke and collapse past it).
- Efficiency island: `η = η_peak·(1 − 0.25·Δm² − 0.15·Δp²)`, `Δm = (ṁ_c − ṁ_peak)/(0.5·choke)`,
  `Δp = (PR − PR_peak)/1.5`, halved over x ∈ [0.9, 1.1], clamped to [0.40, η_peak].
  Surge when `ṁ_c < surge_flow_at_pr2 · (PR − 1)`: efficiency × 0.85 and a telemetry flag.
- `PR = (1 + η·w/(c_p·T₁))^(γ/(γ−1))`, `T₂ = T₁ + w/c_p`, absorbed power `ṁ·w`.

### Charge cooling
- Intercooler: `T = T₂ − ε·(T₂ − T_ambient)`, `ε = min(0.95, ε_ref·(ṁ_ref/ṁ)^0.15·clamp((v_air/15)^0.25, 0.5, 1.1))`;
  pressure drop through its `flow_cfm` restriction.
- Without an intercooler the charge pipes shed 10 % of the compressor's temperature rise.

### Turbine and wastegate
- Turbine and open wastegate are parallel nozzles: turbine inlet pressure is the upstream pressure
  that passes the exhaust flow through `A_turbine + A_wastegate·opening`; the turbine receives the
  `A_turbine / A_total` share of the flow.
- Power `P_t = ṁ_t · η_t · c_p,exh · T₃ · (1 − (p₄/p₃)^((γ−1)/γ))`, with
  `η_t = η_peak · max(0.25, 1 − ((BSR − 0.7)/0.5)²)`, BSR = turbine tip speed / √(2·Δh_s).
- Turbine outlet temperature drops by `P_t/(ṁ_t·c_p)`; mixed with the (hot) wastegate flow it sets the
  exhaust-system gas temperature on the next step.
- Drive pressure (turbine inlet / boost) is an emergent result of the power balance. For the mid-size
  turbo at 0.75 bar it is ≈ 1:1; a small turbine at high rpm drives it well above boost, which is when
  valve overlap causes reversion.

### Shaft
- Energy `E = ½·I·ω²`, `dE/dt = P_t − P_c − k·ω²` (k = 2e−6 journal, 1e−6 ball bearing).
- Overspeed (ω above `max_shaft_rpm`) is flagged in telemetry for the damage model.

### Boost control
- Mechanical: wastegate opening = clamp((boost_gauge − spring)/20 kPa, 0, 1) at the compressor outlet,
  first-order lag 0.08 s. Proportional → boost creeps above the spring as flow rises; an undersized
  wastegate cannot hold boost at all (boost creep).
- ECU (only with `boost_control` hardware and a `boost_target_kpa` table): PI loop on compressor
  outlet boost; the solenoid can only keep the gate shut longer (`opening = min(mechanical, PI)`), so
  targets below the spring are unreachable (validator warning).
- A stock ECU with a 105 kPa MAP sensor cannot see boost: it meters fuel and picks spark as if at
  105 kPa → lean and over-advanced under boost (validator warning `map_sensor_range`).

### Calibration reference (forged K20, 550 cc, standalone ECU, RON 98, 175 kPa target)
Steady-state full boost ≈ 3500 rpm (small), ≈ 4500 rpm (mid), ≈ 7000 rpm (big); peak ≈ 220 hp
(small, choking and over-speeding at 7000 rpm), ≈ 235 hp (mid), ≈ 240 hp and still climbing (big).

## Failure model
Not yet implemented. Planned: stress ratios for every rated component (above), fatigue
accumulation above an endurance threshold, immediate failure above the rating, and a structured
failure report (cause, measured values, ratings, recommendations).

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
control, boost creep, intercooling, stock-ECU MAP saturation, overlap reversion under drive pressure.
