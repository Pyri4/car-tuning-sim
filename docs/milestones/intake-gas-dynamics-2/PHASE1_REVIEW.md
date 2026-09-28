# Intake Gas Dynamics 2.0 — Phase 1 review gate

**Status:** Phase 1 is provisionally accepted and not merged. This review changed nothing in the repository's model: no
parameters, tolerances, physics, calibration fits or M54-specific code.

**Question asked:** root cause and classification of three issues in the
[Phase 1 gate report](PHASE1_GATE_REPORT.md):
- the K20 anchor margin;
- the K20 T35's −7.1 %;
- the M54's remaining discrepancy.

Also asked: does any of them need an implementation change?

**Answer:** none of the three is an implementation defect, and no implementation change is required. Each has a
measured root cause (§1–§3); the owner's decisions are listed in §5.

## 0. Method
A scratch probe outside the repository was compiled twice:
- against a copy of the base commit `1c9c159`;
- against a copy of the Phase 1 head `ce7df56` in which the locked constants were settable, for diagnosis only.

The settable constants were K, ζ, κ, A_max, v₀, the ceiling, the end correction, a diameter scale and a length offset.
The copy also gained the retired `(0.300/L)^0.25` runner factor as a switch, a constant-gain override and an IVC
emulation hook. With default values it reproduces the head exactly: K20 anchor 2.869 %, T35 169.12 kW, M54 297.8 N·m
and 153.9 kW. Rigs:
- the anchor's own rig: 298.15 K, coolant 363.15 K, 1 s per point, RON 95;
- the fingerprint's WOT sweep for the K20 builds (RON 98);
- the M54 rig from the gate report (RON 98, 1,000–6,400 rpm in 100 rpm steps).

"Q5 tunes" means the pre-physics tunes from `1c9c159`.

Two checks rule out an implementation fault in the new code:
- **Equivalence.** The head code with κ = 0, v₀ = 15.0, ceiling 1.02, the old runner factor and the Q5 tunes
  reproduces the frozen anchor **exactly**: 0.000 % at all 25 points, peak torque and power identical. It also
  reproduces the base's NA, T28 and T35 WOT sweeps in every printed channel. Everything outside the gain is untouched.
- **Independent formula.** A re-implementation of proposal §2 from the part data alone agrees with the simulation. On
  the K20 and on both M54 stages, N_t matches to 4·10⁻¹⁶ relative. G matches to 1·10⁻⁷, which is the grid of the
  independent peak search.
  - Under boost, N_t scales with √T_man exactly.
  - The turbo build's reference N_t is 4,525 rpm, not the stock 4,583, because its low-compression pistons enlarge
    V_eff. That is consistent, not a bug.

## 1. K20 anchor margin (2.87 % against ±3 %)
**Where the error sits.** The anchor's reference is the **pre-Phase-1 model's own output** (frozen at 91d3ffa), not a
measurement. The error is the difference in shape between the old filling (one valve-event parabola) and the new one
(parabola × resonance gain). The table splits it into exact multiplicative parts, in ln %:

| rpm | error | VE_dyn | of which G | of which η_ve | port pressure | residual | charge temperature |
|---|---|---|---|---|---|---|---|
| 1,500 | −2.57 % | −2.63 | +0.43 | −3.06 | +0.02 | +0.01 | 0.00 |
| 4,750 (worst) | +2.87 % | +3.38 | +7.69 | −4.32 | −0.44 | −0.10 | −0.01 |
| 7,500 | −2.66 % | −3.83 | +3.05 | −6.88 | +0.94 | +0.20 | 0.00 |

The whole error is VE_dyn. The port pressure absorbs about 13 % of it, and cam timing plays no part (the K20's stock
tune does not phase the cam). The gain peaks at 4,750–5,000 rpm, a parabola cannot follow it, and the re-anchor splits
the difference.

**Fit budget.** The v₀ and ceiling fit was refitted afresh for each variant, on the Q5 tunes. "Least squares" is the
Phase 1 procedure, unrounded. "Floor" is the minimax error, the best that *any* v₀/ceiling pair can reach.

| Variant | least squares | floor | | Variant | least squares | floor |
|---|---|---|---|---|---|---|
| κ = 0 | 0.00 % (v₀ 14.52, C 1.020: the old model) | 0.00 % | | K 2.0 / 2.2 | 2.91 / 2.93 % | 2.63 / 2.71 % |
| κ 0.2 / 0.25 | 1.18 / 1.47 % | 1.11 / 1.38 % | | ζ 0.33 / 0.45 | 2.98 / 2.58 % | 2.74 / 2.43 % |
| **κ 0.5 (locked)** | **2.85 %** | **2.69 %** | | d 0.36 / 0.38 / 0.44 × bore | 3.55 / — / 2.49 % | 3.29 / 2.99 / 2.16 % |
| κ 0.55 / 0.6 | 3.11 / — | 2.94 / 3.20 % | | no end correction | 2.80 % | 2.67 % |
| κ 0.75 / 1.0 | 4.32 / 5.97 % | 4.07 / 5.29 % | | L −100 / +100 mm | 2.57 / 2.87 % | 2.14 / 2.61 % |

How the shipped 2.869 % builds up:

| Component | pp |
|---|---|
| structural floor at the locked parameters | 2.687 |
| least squares instead of minimax | +0.160 |
| rounding v₀/C to 0.01/0.001 | −0.036 |
| the regenerated tunes (charge cooling through λ) | +0.058 |
| **Total** | **2.869** |

**Sensitivity:**
- The floor grows linearly with κ, at about 5.4 pp per unit κ.
- The anchor fails at κ ≈ 0.53 under the project's least-squares procedure, and at κ ≈ 0.56 even with an ideal
  re-anchor.
- It fails if the default runner diameter (A5) is below about 0.39 × bore under least squares, or 0.38 × bore
  with an ideal re-anchor.
- K and ζ move it by ±0.3 pp inside their bands.

**Classification:** expected consequence of the new generic runner model under the locked parameters (κ and A5 decide
it). It is measured against a reference that the old model produced, so there is no implementation issue. Calibration
and content play a minor part: the tunes add 0.06 pp, and the K20 runner diameter is a model default. Class **B**
(documented limitation).

**Acceptable?** Yes as it stands: the ±3 % holds with 0.13 pp to spare, and the margin is deterministic, not noise.
However, only 0.31 pp exists even with a perfect re-anchor. Any later generic change that adds wave amplitude on the
K20 will break the anchor unless it comes with a re-anchor. That covers a group mode, IVC coupling, a larger κ, or a
measured K20 runner diameter below 0.38 × bore. The anchor therefore bounds κ at ≤ 0.53 in practice.

## 2. K20 T35: −7.1 % peak power
Every step below uses the same pre-physics (Q5) tune except the last.

| Step (same Q5 tune except E) | Peak power | Torque at 7,250 rpm |
|---|---|---|
| A: base (`1c9c159`) | 182.13 kW | 239.89 N·m |
| B: head code, κ 0, v₀ 15, C 1.02, old runner factor | 182.13 (identical) | 239.89 |
| B′: runner factor removed | 183.31 (+0.6 %) | 241.44 |
| C: re-anchor without the gain (not a physical state) | 143.73 (−21.6 %) | 189.31 |
| D: full Phase 1 physics | 165.14 (**−9.3 % vs A**) | 217.52 |
| E: regenerated tune (shipped) | 169.12 (**+2.4 % vs D; −7.1 % vs A**) | 222.75 |

v₀ and the ceiling were fitted *with* the gain present, so only B′→D as a whole is meaningful. Its parts (C, then D)
nearly cancel.

**A→D at 7,250 rpm** (the peak, one step below the limiter), in ln %: air falls 8.38 and torque per unit of air 1.41.

The air loss has four parts:
- **VE_dyn −2.96**: η_ve −6.59, G +3.63.
- **Port pressure −6.55.** Manifold pressure falls 7.05: the turbo slows from 82.6 to 77.2 krpm and PR drops from
  1.845 to 1.706. The wastegate goes from 0.021 to 0, so the 175 kPa target is no longer reached before the limiter.
- **Residual 0.00.**
- **Charge temperature +1.14**: less compression gives a cooler charge.

Torque per unit of air falls 1.41 for three reasons:
- **Fuelling:** λ goes from 0.764 to 0.752 against targets of 0.770 and 0.775. The T28-measured fuel map over-fuels
  the T35.
- **Spark:** 13.98° → 14.94° from the tune's MAP axis.
- **Pumping:** PMEP drops from 36.9 to 34.5 kPa.

Cam timing plays no part (0° throughout, same VTEC state).

**D→E (+2.38 % torque):** λ goes from 0.752 to 0.763 and EGT from 1,059 to 1,067 K. The extra exhaust energy adds
2.0 % boost (air +1.76) and torque per unit of air rises 0.62.

**Root cause: a generic, anchor-compliant VE change, amplified by a spool-limited turbo at the rpm of its peak.**

Torque change from A to D (VE_dyn change in brackets):

| rpm | NA K20 | T28 | T35 |
|---|---|---|---|
| 5,000 | +2.3 % | +3.2 % | +4.7 % |
| 6,000 | +0.3 % | +0.8 % | +1.4 % |
| 7,250 | −2.6 % (VE_dyn −3.19) | −2.4 % (VE_dyn −2.89) | **−9.3 %** (VE_dyn −2.96) |

The VE change is the same on all three builds.

The elasticity d ln(torque)/d ln(ceiling) at 7,250 rpm shows why only the T35 loses so much:
- NA K20: 0.71.
- T28: 0.83, where the wastegate holds boost.
- **T35: 4.60.** It is 1.5 at 5,000 rpm and 3.9 at 7,000. The T35 spools with the wastegate shut all the way to the
  limiter, so any breathing change becomes a boost change.

**Classification:**
- **Mechanism:** physically expected (an exhaust-energy-limited turbo).
- **Magnitude:** the unanchored turbo model's own (R3, class **D**).
- **Content interaction (B):**
  - the case runs a tune measured on the T28;
  - its peak sits where the spool ramp meets the limiter, which makes it an ill-conditioned regression metric.
- **Not a Phase 1 implementation bug.**

## 3. M54: the remaining discrepancy
The model and the reference differ in three places:
- **Peak placement:** 297.8 N·m at 3,100 rpm in the model, against the reference's 300 N·m at 3,500.
- **Dip and second hump:** the reference shows both; the model has neither.
- **Top end:** 151.1 kW and 244.5 N·m at 5,900 rpm, against 170 kW and about 275 N·m. That is **+12.5 %** still needed
  at 5,900.

Method. Nothing was fitted: each row is a stated band, or an upper bound taken from the model's own limits. Unless noted,
the fuel map was re-measured for each variant (`VeCalibrator`, λ on target). The re-measured shipped variant reproduces
the shipped result exactly. For reference, a constant G gives the following at 5,900 rpm:

| G | 1.00 | 1.15 (the A3 cap) | 1.20 | 1.30 |
|---|---|---|---|---|
| Power at 5,900 rpm | 144.2 kW | 162.5 kW | 168.2 kW | 178.7 kW |

So 170 kW needs G ≈ 1.22 at 5,900 rpm with the flows as authored. 300 N·m at 3,500 rpm needs G ≈ 1.08; the model
has 1.070 there.

| Mechanism | How tested | Peak rpm | Dip and 2nd hump | P at 5,900 | Explains |
|---|---|---|---|---|---|
| Wave amplitude κ 0.2–1.0 | locked band | 2,300 → **3,500** | no | 147.0–152.8 | **shape only** |
| Amplitude bound A_max 0.25 (κ 1.0) | A3 relaxed | 3,500 | no | 157.6 (+4.3 %) | power, partly (outside A3) |
| K 2.0–2.2, ζ 0.33–0.45 | locked bands | 3,000–3,200 | no | 149.9–153.1 | neither |
| IVC coupling, N_t·(1 + c·IVC shift), c = 0.003–0.006 per ° | emulated on the shipped VANOS map (U6 has no validated form) | 3,000 | no | 152.9–154.7 (+1.2…2.4 %) | neither (small) |
| Runner geometry band: L 300–380 mm, d default or 40 mm; closed stage re-derived by A-D1 | gate report §8 | 2,900–3,100 | no | peak 154.3–156.9 | neither |
| Runner harmonics and pipe overtones | analytic (see below) | — | no | 0 | neither |
| **Group (plenum) resonance** (deferred) | gain at 5,900 raised to the A3 cap | — | **only this can** (see below) | ≤ 162.5 (+7.6 %) | **shape; ≤ 60 % of the power gap** |
| **Flow data** (every estimated flow ×0.9 / 1.1 / 1.2 / 1.3) | content variant | 3,000 / 3,300 / **3,500** / 3,500 | no | 144.9 / 156.0 / 159.9 / 163.2 | **shape and power, partly** |
| ↳ intake side only ×1.2, exhaust side only ×1.2 | | 3,400, 3,200 | no | 158.0, 152.9 | mostly the intake |
| Exhaust scavenging as authored | gain 0–0.05 | inert | no | ±0 | neither |

Notes on three rows:
- **Harmonics and overtones.** The higher-order ram peaks sit *below* each stage's N_t, at N_t·2.1/3 and N_t·2.1/4:
  2,570 and 1,930 rpm on the closed stage, and 2,950 rpm on the open stage, which never runs there. The pipe overtones
  are at x₂/x₁ = 3.6–3.8 times N_t, above 13,000 rpm.
- **Group resonance.** A dip and a second hump need a mode about 40 % above the stage crossover. The two runner stages
  are only 14.5 % apart (3,676 and 4,209 rpm).
- **Exhaust scavenging.** The 300 mm primaries tune it to 17,300 rpm, so it has no effect in the rev range.

Combining mechanisms gives an envelope, not a fit:
- flows ×1.2 + κ 1.0 + IVC 0.0045: 165.9 kW at 5,900 rpm, with the peak at 3,500 but 7.6 % too high (322.7 N·m);
- flows ×1.2 + G at the A3 cap: 173.3 kW.

**What explains what:**
- **Torque shape.** Peak placement: either the upper κ band or about 20 % more flow than estimated. The M54 curve
  cannot tell them apart, but κ ≈ 1.0 is incompatible with the K20 anchor (§1, κ ≤ 0.53). Of the two, flow data is the
  explanation consistent with both engines, but it is unmeasured. Dip and second hump: only the deferred group mode.
- **Absolute power.** No single deferred mechanism closes the +12.5 %. Wave physics within A3 gives at most +7.6 % and
  better flow data (+20 %) +5.8 %; reaching 170 kW takes both.
- **Neither.** K, ζ, IVC coupling as a tuned-speed shift, the runner-geometry band, harmonics, overtones, and exhaust
  scavenging as authored.

**Classification:**
- Peak placement: **B** (κ and the flows are confounded).
- Dip and second hump: **B → D** (the Phase 2 group mode).
- Top end: **B → D**. It needs the group mode *and* better flow data; neither alone suffices.
- **Unresolved:** whether the M54's estimated flows are low by about 20 %. Only measured flow-bench data can settle it.

## 4. Review gate
| Issue | Root cause | Class | Implementation change required? |
|---|---|---|---|
| K20 anchor margin 2.87 % | The locked κ and A5 gain shape, which no v₀/ceiling pair can absorb against the old model's reference (floor 2.69 %) | B | **No** |
| K20 T35 −7.1 % | The generic VE change at 7,250 rpm (−3 %, as on the NA engine) amplified ×4.6 by a spool-limited turbo; plus an off-recipe tune | expected mechanism; magnitude D; content B | **No** |
| M54 discrepancy | Shape: κ and flow data (confounded) plus the missing group mode. Power: needs the group mode and better flow data; wave physics alone is bounded at +7.6 % by A3 | B → D | **No** |

Implementation defects found: none. The equivalence check is bit-exact and the formula check agrees to machine
precision.

## 5. Decisions for the owner (not taken here)
1. **Anchor margin.** Accept it as class B, and record that the anchor limits κ to about 0.53 and the default diameter to
   at least 0.38 × bore. Phase 2 will need to plan a re-anchor, or a decision on the reference, before adding any wave
   amplitude on the K20.
2. **T35.** Accept it. Optionally, report the T35 fingerprint case with its sensitivity (elasticity 4.6), or give it a
   tune recipe of its own. Both are content or verification changes, not model changes.
3. **M54.** Before any Phase 2 decision on κ or the group mode, get measured flow data for the M54's intake path, since
   κ and the flows are confounded. The top end is out of reach of wave physics alone under A3.
4. **Merge** remains the owner's call. Nothing was merged, and no further milestone was started.
