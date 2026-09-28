# Intake Gas Dynamics 2.0 — Phase 1 authorization proposal

**Status: final authorization gate, 2026-09-27. Proposal only.** No intake gas-dynamics physics is implemented, nothing
under `src/` changed, and nothing is merged. This document resolves U1–U7 of the
[design resolution](INTAKE_GAS_DYNAMICS_2_DESIGN_RESOLUTION.md) and locks what Phase 1 would implement if the owner
authorizes it. Where the two documents differ, this one governs.

Terms:
- **locked**: implemented exactly as written.
- **pre-registered**: a parameter's value is fixed here, before any Phase 1 output exists. It is never changed afterwards
  to make a test, an anchor or a reference comparison pass. A miss is reported and classified instead.

Evidence produced for this gate lives in the repository. Scratch calculations are described with their inputs so that
they can be re-run; none of them is part of the model.

## 1. Decisions (U1–U7)
| Item | Decision |
|---|---|
| **U1** Engelman K | Primary verification is impossible from this environment (section 3.1). **K is classified as an empirical, shared model parameter:** nominal 2.1, band 2.0–2.2, pre-registered, never fitted to any engine. |
| **U2** Frequency model | **Locked:** the fundamental of the runner plus cylinder as a 1-D resonator, from `x·tan x = β`, with the end correction and a speed of sound from the manifold gas temperature (section 2.3). |
| **U3** Damping and amplitude | **Damping has a derived floor.** The finite intake event alone sets the effective damping of a steady resonance curve at ζ ≈ 0.33–0.35, and losses raise it to about 0.40. ζ is pre-registered at 0.35, band 0.33–0.45. **Amplitude has no defensible source:** it is an explicit shared parameter with a bounded band. The acceptance tests no longer depend on either (section 5). |
| **U4** M54 DISA | **A-D1 remains defensible and is locked.** Two effective stages; the open stage is the runner estimate; the closed stage is derived from the sourced switch band through the gain functions alone. Every value is classified, and everything is frozen before any M54 output is seen (section 4). |
| **U5** Acceptance tests | **Tests 1, 4 and 5 are rewritten** as physical and model relationships, all amplitude- and damping-independent; tests 2 and 3 are unchanged. All five fail on today's model for their intended reasons. |
| **U6** Intake-closing coupling | **Deferred from Phase 1.** No defensible generic formulation exists (section 3.5). It is a documented class B limitation with an estimated size. |
| **U7** K20 anchor | **Three levels:** numerical (bit-identity), physics (air per cycle ±3 %) and calibration (peak torque, peak power and 0–100 km/h ±3 %). These are executable now as `K20AnchorGuardTests` with a frozen reference. |
| Project-car lap | **Reclassified as a knife-edge diagnostic:** reported, not failed. Its dyno section and every other drive stay hard checks. |

---

## 2. Locked equations
### 2.1 Structure
`VE_dyn = η_ve(N, cams) · G_wave(N, T_man, stage) · (1 + scavenging) · float`

- The whole product is evaluated once per bank per step, before the air-path root find, exactly where `VE_dyn` is
  evaluated today.
- Scavenging, valve float, residuals and the air-path solve are unchanged.
- **Removed:** the `(0.300 m / L)^0.25` factor and `EngineConfiguration.ReferenceRunnerLength`.

### 2.2 Valve-event filling η_ve
- The form is today's parabola around the tuned piston speed `v_ivc = v₀ + k_d·(D − 220°) + 2·k_d·Δ_IVC`.
- v₀ and the VE ceiling are re-anchored on the K20 stock (section 6); these are the only fitted constants.
- k_d, the side coefficients, the floor and the 112° reference centreline are unchanged.
- The cam phaser, the VVL profile and the installed centrelines act here and only here.

### 2.3 Tuned speed of a runner stage (U2, locked)
Symbols: A_r is the runner cross-section, L_eff its effective length, V_eff the effective cylinder volume, x the
dimensionless fundamental, β the runner-to-cylinder volume ratio.

| Quantity | Definition |
|---|---|
| Effective length | `L_eff = L + δ·r_r`, where L is the acoustic length from the plenum mouth to the valve seat (runner plus head port) and r_r the runner radius |
| Effective cylinder volume | `V_eff = V_c + V_d/2` per cylinder of the bank: the intake-stroke mean of the cylinder volume, `V_d·(CR+1)/(2(CR−1))` |
| Volume ratio | `β = A_r·L_eff / V_eff`, the runner's volume over the cylinder's |
| Fundamental | the root `x ∈ (0, π/2)` of `x·tan x = β` |
| Frequency | `f₁ = x·a / (2π·L_eff)`, with `a = √(γ·R·T_man)`, γ = 1.4, R = 287 J/(kg·K) |
| Tuned speed | `N_t = 60·f₁ / (K·c₁)`, where `c₁ = c(1) = 0.8603` and `c(β) = x(β)/√β` |

**Derivation.** Take a uniform duct with a pressure node at the plenum (p = 0 at x = 0) and the cylinder's compliance
`V_eff/(ρa²)` at the valve. The 1-D wave equation then gives `kL_eff·tan(kL_eff) = β`. This tends to the Helmholtz
frequency `a/(2π)·√(A_r/(L_eff·V_eff))` for β → 0 and to the quarter-wave `a/(4L_eff)` for β → ∞.

**Why `c₁`.** K is stated in the literature for the lumped Helmholtz frequency. `N_t = 60·f₁/(K·c₁)` equals Engelman's
form exactly at β = 1 (assumption A7) and corrects the geometry dependence elsewhere. At K20 geometry, plain Helmholtz is
15 % too high; across the repo's runners the error is 11–21 % (design resolution, Q2).

**Inputs.**

| Input | Source | Notes |
|---|---|---|
| L | stage geometry (`runner_length_mm`, and the N-stage schema) | **acoustic length including the head port**; the authoring guide gets this definition |
| d = 2·r_r | stage geometry (new optional `runner_diameter_mm`) | default 0.40·bore (A5), flagged `default_intake_geometry` by the validator |
| δ | 0.8216 | flanged-pipe end correction (runner mouth in a plenum wall) |
| V_c, V_d | the bank's own geometry | already per bank |
| T_man | the bank's manifold gas temperature **from the previous step** | ambient on an NA engine (exact); intercooler or charge-pipe outlet under boost; initial value ambient; keeps `VE_dyn` out of the root find |

**Computation.**
- x(β) is solved once per stage when the configuration is built (bisection on (0, π/2); β is bounded by the validator's
  length, diameter and displacement ranges).
- Per step only `N_t = N_t(T_ref)·√(T_man/T_ref)` is evaluated. This is exact, because β and x do not depend on T.

**Assumptions (U2).**
- a uniform cross-section (the mean area of a tapered runner);
- rigid, isothermal walls, with the runner gas at T_man;
- the plenum is a pressure node (its volume is much larger than the cylinder's);
- plane waves, since `k·r_r ≈ 0.05 ≪ 1` at the tuned frequencies;
- no mean flow, and one mode;
- the cylinder volume frozen at its intake-stroke mean (A8), with the time-varying volume and valve losses absorbed in K.

**Expected error of the placement N_t** (none of these is fitted; the Phase 1 report shows the sensitivity to the
larger ones):

| Source | Size |
|---|---|
| Mean flow (frequency × (1 − M²)) | −1 to −2.5 % at runner Mach 0.1–0.15 |
| Runner gas warmer than T_man by ΔT | +ΔT/(2T): +1.7 % per 10 K |
| Area variation (taper, bends) against the mean area | not quantifiable without geometry; typically a few per cent |
| End correction (0.61–0.85·r) | ≤ ±1 % for production runners |
| K (secondary-literature spread) | ±5 % (band 2.0–2.2) |
| Lumped-to-distributed conversion (A7, β_ref from 0.5 to 2) | +7 / −12 % |
| No intake-closing coupling (U6, deferred) | up to ±10–15 % for cams far from typical closing |
| **Combined, as a scale** | **±10–15 %**: a documented limitation, class B |

### 2.4 Response (locked)
- The response is a quadrature resonance curve. With `r = N/N_t`:
  - `R(r) = 4ζ²r / ((1 − r²)² + 4ζ²r²)`;
  - `R̃(r) = R(r·r_p) / R(r_p)`, where `r_p = argmax R`, a constant for each ζ, precomputed.
- R̃ is 1 exactly at the tuned speed and positive everywhere, and it falls off on both sides.
- It is the forced-oscillator response in phase with the flow. This is the Phase 0 formula with its reference phase
  corrected to π/2 (design resolution, Q7).
- Between two stages' tuned speeds there is exactly one crossover.

### 2.5 Amplitude (locked form; its coefficient is a parameter)
`A(N) = min(A_max, κ·M_r)`, with:
- `M_r = ū_r / a`;
- `ū_r = V_d,cyl·(N/120) / (A_r·f_event)`;
- `f_event` = the existing intake-event fraction, (duration@1 mm + 50°)/720.

The linear-acoustics form (a relative pressure amplitude proportional to the Mach number) is derived. κ is not; see
section 3.

### 2.6 Gain (locked)
- `G_wave = 1 + A·R̃(N/N_t)`, bounded to `[1, 1 + A_max]`.
- **No cam input.** Off-tune losses (G below 1) are deferred.

### 2.7 Stages, banks, boost
- **Stages:** N stages with the existing ECU switching (switch speed, 150 rpm hysteresis). Today's
  `switched_runner_length_mm` reads as N = 2.
- **Banks:** each bank reads its own intake part. Shared parts enter each bank at its cylinder share, as today. Two alike
  banks equal one bank of all cylinders.
- **Boost:** the same equations. No turbo-specific term, and no plenum mode (section 3.5).

### 2.8 Cost
Per bank and step: one square root and one rational function, with no allocation and no new root find. The budgets are
the allocation guard and a step-time increase of at most 10 %.

---

## 3. Parameter provenance
| Parameter | Pre-registered value | Band (sensitivity) | Class | Provenance |
|---|---|---|---|---|
| γ, R | 1.4, 287 J/(kg·K) | — | physical | air |
| δ | 0.8216 | 0.61–0.85 | physical, sourced | Norris & Sheng 1989 (flanged pipe; the value was read in a search summary of J. Sound Vib., the paper was not opened); unflanged 0.6133, Levine & Schwinger 1948 |
| c₁ | 0.8603 | — | derived | the root of `x·tan x = 1` |
| β_ref | 1 | 0.5–2 | **assumption A7** | Engelman's test geometries are unknown |
| **K** | **2.1** | **2.0–2.2** | **empirical, shared** (U1) | section 3.1 |
| **ζ** | **0.35** | **0.33–0.45** | **derived floor + bounded** (U3) | section 3.2 |
| **κ** | **0.5** | **0.2–1.0** | **engineering estimate, shared** (U3) | section 3.3 |
| A_max | 0.15 | — | bound (A3) | keeps η_ve·G ≤ 1.02 × 1.15 = 1.17, well under the 1.35 guard |
| runner diameter default | 0.40·bore | 0.35–0.45·bore | assumption A5, flagged | typical ratio |
| v₀, VE ceiling | re-anchored | — | **fitted on the K20 stock only** | section 6 |
| k_d, side coefficients, floor, 112° | unchanged | — | as documented today | — |

### 3.1 K (U1)
**Final verification attempt (this gate).** These were denied by egress policy:
- the COBEM paper's repository (repositorio.ufsc.br);
- Crossref, Semantic Scholar and CORE;
- dokumen.tips (which holds a copy of the paper), studylib and fswiki.us.

Google Books' API was reachable, but its shared daily quota was exhausted (HTTP 429). With the hosts denied at the
design resolution (scribd, kupdf, archive.org, ASME, SAE, USPTO, Google Patents, Wikipedia and others), **no copy of
Engelman 1973 can be opened here. K and V_eff stay unverified against the primary text.**

**Evidence (all secondary, mutually consistent):**
1. **The circulated form of Engelman's equation.** Its constant 77 is `60·12·√2/(2π·K)` at **K = 2.1** (162/2.1 = 77.1,
   inch and ft/s units), and its `√((c−1)/(c+1))` term is exactly the mid-stroke volume
   `V_eff = (V_d/2)(c+1)/(c−1)` ([Eng-Tips search summary](https://www.eng-tips.com/threads/calculation-for-intake-runner-length.165529/)).
2. **An FSAE design script** read directly on GitHub
   ([script](https://github.com/mw95710/cubesat-propulsion-FSAE/blob/master/Intake_Manifold_Helmholtz_Resonance_Calculation.m)):
   `162·c/k` with **k = 2**, V1 "volume of cylinder at mid stroke".
3. **COBEM 2017 (UFSC)**, search summary: the ratio is "a constant around two", and V is "half the displacement plus the
   dead space".
4. **SAE 2007-01-0382 (Ohio State, Engelman's institution)**, search summary: "preliminary values like K = 2.1".

**Uncertainty.**
- The literature spread is 2.0–2.1. The band 2.0–2.2 adds the same margin above 2.1 as below, and moves every tuned
  speed by ±5 %.
- The model-form uncertainty is larger and is kept separate, not folded into K: a first-principles lumped model puts the
  optimum ratio at 1.2–1.8, depending on intake closing and damping. K absorbs what a one-mode model leaves out.

**Why an empirical parameter is acceptable here.**
- **It is dimensionless and acts only as a placement scale.** It does not enter the length, diameter, volume or
  temperature laws the acceptance tests check (tests 1–4 are K-independent).
- **Its value is external, not fitted.** It rests on four independent secondary sources that agree to within 5 %.
- **Its consequences are visible.** The K20 anchor and the held-out M54 report its effect, with sensitivity at the band
  edges.
- **Its effect is no larger than other content uncertainty.** An error in K is equivalent to a ≈ 10 % error in runner
  length, which is the typical uncertainty of an estimated runner in content anyway.

**Why shared, never per engine.**
- K is a property of the four-stroke induction process: the ratio between the runner–cylinder natural frequency and the
  rate at which intake events excite it.
- By similarity it depends on dimensionless quantities (the valve event, damping), not on engine size or identity.
- A per-engine K would absorb geometry estimates, content mistakes and every missing physical effect engine by engine.
  That is exactly the per-engine fitting the project forbids, and it would make each miss invisible.
- The known physical variation of the optimum ratio (with intake closing) belongs in a generic coupling (U6), not in
  per-engine constants.

K is not fitted to the M54 or to any dyno curve. Should the owner obtain Engelman 1973 (ASME 73-WA/DGP-2), the value and
the V_eff definition are checked against it before Phase 1 code, and this table is updated.

### 3.2 ζ, damping (U3)
**Provenance (derived floor).**
- The runner is coupled to the cylinder only while the intake valve is open. A steady resonance curve therefore cannot be
  narrower than the finite event allows.
- An exploratory nonlinear lumped model of one intake event (K20 geometry, 340 mm runner, real slider-crank volume, no
  physical losses) gives a gain-versus-speed curve whose half-maximum width equals the quadrature curve's at
  **ζ = 0.33–0.35** for events of 200–240° closing 30–50° ABDC.
- Adding physical losses (ζ_phys = 0.1–0.2) gives 0.34–0.40.
- Inputs and method are in the design resolution's scratch description; the model is not part of the simulation.

**Value.** 0.35 is the lossless floor at typical events; the band 0.33–0.45 extends to losses slightly beyond those
computed.

**Shared.** The event-duration dependence (a longer event gives a narrower curve) is cam coupling and is deferred with
U6.

**Consequence.** The old test 4 needed ζ ≲ 0.3 for its 2 % gain. That is below the physical floor, so the old threshold
had no physical support.

### 3.3 κ and A_max, amplitude (U3)
**No defensible source exists** in reach. The lossless lumped model gives trapped-mass gains of 24–83 %, far above
plausible values, because it has no valve restriction. The one secondary statement found ("15 percent more compression")
cannot be attributed, so it is not used.

Amplitude is therefore an **explicit shared parameter**:
- nominal κ = 0.5, band 0.2–1.0, which gives A = 0.02–0.10 at a typical runner Mach of 0.1;
- A_max = 0.15 is a hard bound (A3).

The band's lower edge is where the runner effect falls below the model's other level uncertainties; the upper edge is
where A reaches A_max at high-speed Mach numbers.

**How it is handled.**
- No acceptance test requires a gain of a given size.
- The K20 anchor and the M54 validation are evaluated at the nominal and reported at the band edges.
- A Phase 1 result that "needs" a different κ is a finding, not a retune.

### 3.4 v₀ and the VE ceiling (the only fitted constants)
- They are re-anchored so that the stock K20 (RON 95), whose configuration the old constants were fitted on, meets the
  physics level of the anchor (section 6).
- The fit uses only the anchor's air per cycle. No other configuration and no M54 data enter it.

### 3.5 U6: intake-closing coupling (deferred) and the plenum mode
**U6 is deferred.**
- **The only candidate** is the exploratory lumped model: the runner's best speed rises by about 0.5–0.7 % per degree of
  later closing.
- **It fails to reproduce K.** The same model gets the absolute placement wrong (n* = 1.2–1.8 against K ≈ 2), so its
  derivative is not validated.
- **No reachable source** quantifies the coupling. By the rule "no formula whose provenance remains uncertain", it is not
  implemented.
- **Expected size of the omission:** up to ±10–15 % in the placement of a runner's optimum for cams far from typical
  closing, and a missed shift of about 30–40 % across a 60° phaser range. The revised test 1 already admits a future
  coupling (ratio 0.70–1.05).

**The plenum or group mode is also deferred.** Its formula is derived (the two-mode system), but its excitation for
group pulses is unsourced, no engine in the repo has its geometry, and boosted engines lack the charge-air volumes.

---

## 4. The M54 DISA (U4, locked procedure)
**Re-check of A-D1** (the manufacturer switches at the full-load crossover of the two stages). It remains defensible:
- it is standard calibration practice, and the authoring guide's own rule for switch speeds;
- the sourced description is speed-based (MS43 energises the solenoid below ≈ 3,750 rpm and releases it above
  ≈ 4,100 rpm, with load, vacuum and temperature as modifiers);
- the reference curve's dip near the switch is what the envelope of two humps looks like at their crossover (a
  consistency check, not an input);
- in the locked model the crossover depends on nothing but the two tuned speeds and ζ, because both stages share the
  runner area and therefore the amplitude.

Residual risks, reported not hidden:
- the quoted band may not be the full-load band;
- the true crossover lies somewhere within the 350 rpm hysteresis band (±175 rpm);
- the real mechanism is a group-plenum resonance represented by an effective stage.

**Classified values.**

| Value | Used as | Class | Basis |
|---|---|---|---|
| Switching mechanism (vacuum flap, sprung open) | ECU drives DISA: `intake_runner_control: true` on `ecu.m54_oem` | sourced (secondary, corroborated) | Pelican Parts, BimmerFest, eEuroparts |
| Switch band 3,750 / 4,100 rpm | crossover target 3,925 rpm (centre), check band 3,750–4,100 | sourced (secondary, corroborated) | same |
| Topology: two groups of three, the flap joins them | why a stage is *effective* | sourced qualitatively; layout ambiguous | Pelican Parts, ASC, BMW training wording |
| Open-stage acoustic length | 380 mm | **estimated** (no source) | PARTS_DATABASE.md; band 300–450 mm |
| Runner diameter (both stages) | 33.6 mm (0.40 × 84 mm) | **estimated** (A5 default, flagged) | band 30–40 mm |
| End correction | 0.8216·r | sourced (physics) | section 3 |
| V_eff | 302.2 cc | derived | bore 84, stroke 89.6, CR 10.2 (sourced; CR derived by the model) |
| Firing order 1-5-3-6-2-4 | not used in Phase 1 | sourced (secondary); the schema's optional `firing_order` is not authored for the M54 and no physics uses it | E46 Fanatics, BimmerFest |
| Group plenum volumes, resonance tubes, flap bore | not used | **unknown** | — |
| Closed-stage tuned speed N_c and effective length | derived below | **derived** (A-D1 + model) | — |
| Tune `intake_runner_switch_rpm` | the driver's `runner_switch` step (the stage-held crossover) | calibrated | tune manifest |

**Derivation (done once, with the pre-registered parameters, before any M54 output).**
1. Compute `N_o` from the open-stage geometry (section 2.3), at T_man = 298.15 K (the reference ambient).
2. Solve `R̃(x/N_c) = R̃(x/N_o)` at x = 3,925 rpm for `N_c < x`.
   - Amplitude cancels, because both stages share the runner area and therefore the Mach number.
   - The solution is unique between the two tuned speeds.
3. Invert section 2.3 for the closed stage's effective length at the same diameter.
4. Write both stages into the M54 part with this table's classification (PARTS_DATABASE.md), and freeze them.

**Preview with the pre-registered values** (this is arithmetic on the locked formulas, not M54 output):
- **Nominal:** N_o = 4,208 rpm, N_c = **3,677 rpm**, closed effective length **469 mm**.
- **Across the switch band:** N_c from 3,379 to 3,997 rpm.
- **Sensitivity:**
  - L = 300 mm: N_c 3,282 rpm;
  - d = 40 mm: N_c 3,366 rpm;
  - K = 2.0 / 2.2: N_c 3,528 / 3,837 rpm;
  - ζ = 0.33–0.45: N_c 3,676–3,677 rpm.
- **Exclusions:** L = 450 mm or d = 30 mm put the open stage *below* the sourced switch band, so the sourced fact
  excludes those estimates.

**Frozen:** after step 4 no M54 value changes because of the M54's output. Test 5 then checks the relationships
(section 5) and reports the curve's shape as held-out validation. A miss is classified (A/B/C/D) and documented.

---

## 5. Acceptance tests (U5)
### 5.1 Pending acceptance tests
These run with `CARSIM_RUN_PENDING_ACCEPTANCE=1`. Each fails today, and Phase 1 turns them into plain facts.

| Test | Relationship and tolerance (derivation) | Today |
|---|---|---|
| `TheRunnerResponseIsNotCarriedByTheCamPhaser` (revised) | M54, 380 vs 250 mm test-only runners, intake cam held at 10° and 50°. The short-over-long crossover exists at both phases (1,500–6,400 rpm), and `x(50°)/x(10°)` lies in 0.70–1.05. **Why:** the Phase 1 model gives 1.00 exactly; the deferred closing coupling may lower it by ≈ 11–22 %; the valve-event optimum moves ≈ 85 %. | no crossover at 50° |
| `TunedSpeedScalesWithRunnerLengthAsTheWaveModelsPredict` (unchanged) | K20, 230/340/460 mm: exponent against the geometric-mean length in 0.45–1.1. The locked model gives **0.608**; plain Helmholtz would give 0.474; the end correction reaches 0.45 only at diameters of 80–87 mm. | 0.254 |
| `AHotterChargeRaisesTheTunedSpeedWithTheSpeedOfSound` (unchanged) | K20, ambient 263 → 323 K: crossover shift within 0.5–1.5 × (√(T_hot/T_cold) − 1) of the charge temperature. The locked model shifts by √ of T_man, about 10.8 % against the 9.3 % predicted from charge temperature, inside the band. It is kept on charge temperature so that the test does not encode the model's own choice of temperature. | 0 |
| `ASwitchedRunnerActsOnAPhasedEngine` (revised) | M54 with a test-only 380/250 mm two-stage intake, runner-control ECU and the shipped VANOS map. Four relationships: (1) stage ordering with a crossover, each stage ahead somewhere by more than 0.2 % (a non-vacuity floor, not a gain requirement); (2) **the phaser does not absorb the stage effect**: below 0.7 × the parked crossover, where the parked effect is at least a quarter of its maximum, the effect under the map has the same sign and at least half the size; (3) torque follows filling; (4) the switched curve is the upper envelope. Every threshold is relative, so the test is amplitude- and damping-independent. | fails (2): at 1,500 rpm, cam 42.5°, the stage effect is +0.20 % under the map against −2.75 % parked, so the phaser absorbs and inverts it |
| `TheM54DisaStagesFollowTheirProvenance` (revised) | DISA fitted and driven by the ECU (capability summary, schema-independent); the whole engine's stage crossover and the tune's switch speed lie in the sourced band 3,750–4,100 rpm; closed below and open above, with torque following filling; the switched curve is the upper envelope; bands 270–330 N·m and 144.5–195.5 kW. The curve's shape is **reported, not asserted** (held-out validation). | DISA not authored |

What changed and why:
- **Test 1:** the ±10 % band (not a physical bound) became the closing-coupling band.
- **Test 4:** the 2 % gain (a hidden requirement on κ and ζ) became relationships. An intermediate version that compared
  crossovers passed on today's model: its crossover, at 5,731 rpm, sits where the VANOS map is parked. It was replaced by
  the absorption check, which fails today for the right reason.
- **Test 5:** the "mid-range peak, ≥ 3 % rise" assertion (an M54-targeted outcome) became provenance and relationship
  checks plus reported validation.

### 5.2 Active guards
These hold now and after Phase 1:
- `IntakeGasDynamicsGuardTests`: bounded VE and λ within 4 % for every family, switch hysteresis, the phaser moving the
  valve-event optimum, and per-step allocation;
- **`K20AnchorGuardTests` (new, U7):** air-per-cycle physics tolerance; peak torque, peak power and 0–100 km/h
  calibration tolerance; frozen reference;
- the regression fingerprint (knife-edge sections excepted), the source audit, the architecture invariants and
  `M54ReferenceTests`.

### 5.3 Component tests Phase 1 writes against the new API
- The tuned speed has no cam input.
- It scales exactly with √(T_man).
- `x·tan x = β` meets the Helmholtz limit for β → 0 and the quarter-wave limit for β → ∞.
- R̃ equals 1 at r = 1 and is positive and unimodal.
- `1 ≤ G ≤ 1 + A_max` everywhere, including spec fuzz over the new fields.
- The two-stage crossover is unique.
- The DISA derivation reproduces 3,925 rpm from the frozen geometry.
- Mutation entries for "cam input to G", "tuned speed without √T" and "unnormalised R".

---

## 6. Regression rules (U7)
| Level | What | Tolerance | Why this tolerance |
|---|---|---|---|
| **1. Numerical** | Every fingerprint section where neither code nor content changed; pinned scalars | **Bit-identical**. Pinned scalars within 1e-9 relative, re-pinned only in a commit that names the cause | Deterministic code on IEEE doubles: any difference is a real change (tested parallel against serial, and locally against CI) |
| **2. Physics** | Stock K20, RON 95, full-load **air per cycle** at every 250 rpm point from 1,500 to 7,500 rpm, against the frozen reference (commit 91d3ffa) | **±3 % per point** | On this configuration the fuel map cannot move the air: the Q5 regeneration moved it by < 0.001 % (T28 0.014 %; only hardware schedules or boost couple the tune to air, and the anchor has neither). A failure is therefore physics. The size admits the documented generic correction (the new term reshapes filling by about half its amplitude, A ≈ 0.03–0.10, which v₀ and the ceiling absorb only in part) and stops anything larger. |
| **3. Calibration / content** | After the driver regenerates every tune under the new physics: stock K20 peak torque, peak power and 0–100 km/h; λ on target for every family | **±3 %** each; λ within 4 % (existing guard) | Outcomes may not move further than the physics level allows. A torque change larger than the air change explains is a calibration effect (λ, spark and knock, friction share), and the report must attribute it. |

How tune changes are kept from masquerading as physics:
1. **The physics level uses a tune-independent observable** on a configuration where independence was measured.
2. **The Phase 1 report runs `fingerprint-diff` twice.** First the physics on the pre-physics (Q5) tunes, then the tune
   regeneration with the physics fixed. Each output change is attributed to one of the two.
3. **Tune tables change only through the driver.** Its output is itself pinned (`k20_regen_stock_ve`), and hand-authored
   tables never change.
4. **The frozen anchor reference** in `K20AnchorGuardTests` is never re-pinned by the change it guards. Re-pinning it
   needs the owner's decision.

The remaining rules carry over from the design resolution:
- **The K20's other configurations** (short runner, NA build, T28, T35) are not anchored. Each move is reported with its
  physical cause.
- **The M54** is never an input to a shared constant. Its content changes are limited to section 4, the bands must hold,
  and its shape is validation.
- **The synthetic matrix** stays content-only, with regenerated tunes and the guards.
- **`TorqueCurveDiagnosisTests`** are retired or inverted deliberately.

**Knife-edge diagnostics.** `scenario_project_car/drive` (the worn project car's autopilot lap) is listed in
`FingerprintMatrix.KnifeEdgeSections`:
- its difference is printed as a diagnostic with the reason and does not fail the fingerprint (tested, and mutation-
  checked both ways);
- the reason: moving 11 VE cells by 0.001 (≤ 0.001 % torque) flipped it from 0 laps to a 59.56 s lap;
- the case's dyno section and every other drive case (stock K20, T28, M54, synthetic V6 swap) stay hard checks.

---

## 7. Explicit assumptions
| Id | Assumption | Status in Phase 1 |
|---|---|---|
| A1 | One mode per stage represents the runner response (no pipe harmonics, no plenum mode) | locked |
| A2 | The intake-closing phase coupling | **withdrawn** (U6 deferred) |
| A3 | A_max ≤ 0.15 | locked bound |
| A4 | "Plenum mode off under boost" | replaced: the plenum mode requires upstream geometry (not in Phase 1) |
| A5 | Runner diameter 0.40·bore when not authored | locked default, flagged by the validator |
| A6 | Plenum default volume | moot (no plenum mode) |
| A7 | The lumped-to-distributed conversion of K at β_ref = 1 | locked; sensitivity reported |
| A8 | The cylinder volume frozen at its intake-stroke mean | locked; residual absorbed in K |
| A-D1 | DISA switches at the full-load crossover of its stages | locked (section 4) |
| A9 | Runner gas at the manifold temperature (no wall heating along the runner) | locked; +1.7 % per 10 K is the error scale |
| A10 | The manifold temperature of the previous step drives the speed of sound | locked; exact on NA engines, a one-step lag under boost |

## 8. Remaining uncertainties
| Item | Class | Effect | What would resolve it |
|---|---|---|---|
| K unverified against Engelman 1973 | B (documented limitation) | ±5 % placement | the ASME paper (owner) |
| Amplitude κ unsourced | B | the size of every runner effect; the tests are independent of it | published VE-vs-runner data for a real engine with known geometry (a second real reference, U8) |
| M54 DISA geometry estimated, closed stage derived through A-D1 | B | the M54's shape validation | a measured manifold (protocol in the design resolution) |
| The intake-closing coupling omitted | B, D for Phase 2 | ±10–15 % placement for atypical cams; phaser shift missed | a sourced or validated formulation |
| Plenum and group resonance, pipe harmonics, off-tune losses | D | secondary humps and dips not modelled | Phase 2, with geometry data |
| Lumped conversion A7 | B | +7 / −12 % placement | verified K on a known geometry |
| Hand-authored K20 spark maps above the calibrator's ceiling (74 and 80 cells) | carried over; owner decision | light-load torque | keep, lower or convert (the driver audits them after Phase 1) |

## 9. What authorization would start
In order, each step validated before the next:
1. **Data:** `runner_diameter_mm` and the N-stage schema, validator ranges and defaults, and tests; bit-identical output.
2. **The model of section 2:** component tests (section 5.3), mutation entries, and the fingerprint diff on the Q5 tunes.
3. **Re-anchor v₀ and the ceiling** on the K20 stock (Level 2).
4. **Regenerate every tune** with the driver; Level 3; the second fingerprint diff.
5. **The M54 DISA** by the section 4 procedure, frozen; then test 5 and the reported validation across the bands.
6. **Pending tests become facts**; `TorqueCurveDiagnosisTests` retired or inverted; docs, bench (≤ +10 %), Godot smoke
   tests, CI; the project gate.

Parameters in section 3 are pre-registered by the authorization. Changing one before step 2 needs a written reason in
this document; changing one after it is not allowed.
