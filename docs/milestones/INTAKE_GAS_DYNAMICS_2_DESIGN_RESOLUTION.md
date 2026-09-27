# Intake Gas Dynamics 2.0 — design resolution (before Phase 1)

> **Superseded where it differs:** U1–U7 below are resolved, and tests 1, 4 and 5 revised, in the Phase 1 authorization
> proposal, [INTAKE_GAS_DYNAMICS_2_PHASE1_PROPOSAL.md](INTAKE_GAS_DYNAMICS_2_PHASE1_PROPOSAL.md). This document is kept as
> the analysis record.

**Status: design-resolution gate, 2026-09-27. No intake gas-dynamics physics is implemented and no engine-simulation
code changed.** The owner accepted Phase 0 and asked for a design-resolution pass: resolve Q1–Q5 of the Phase 0
specification (SIMULATION_SPEC.md, "Intake gas dynamics 2.0 — proposed model"), verify the literature the specification
marked unverified, check the five pending acceptance tests, and prepare the pre-physics tune regeneration (Q5) as a
separate commit. Phase 1 waits for the owner's authorization, and several decisions below are the owner's to take (see
"Unresolved questions").

The only repository change besides documentation is the Q5 tune regeneration: two recalibration-driver rules and
regenerated tables under the **existing** physics, in their own commits (see Q5 below and
[intake-gas-dynamics-2/TUNE_REGENERATION_2026-09-27.md](intake-gas-dynamics-2/TUNE_REGENERATION_2026-09-27.md)).

## Decisions at a glance
| Item | Decision |
|---|---|
| **Q1** M54 DISA provenance | No dimension of the DISA manifold is published. The mechanism, the topology (two groups of three cylinders joined by a flap) and the switch band (closed below ≈ 3,750 rpm, open above ≈ 4,100 rpm) are sourced from secondary technical sources. DISA is represented as **two effective single-mode stages**. The open stage is the runner, an estimate flagged as such. The closed stage is **derived from the sourced switch band** under a stated assumption, A-D1: the manufacturer switches at the stages' full-load crossover. The derivation is done once, with the Phase 1 model's pre-registered constants, and frozen before any M54 torque curve is looked at. Nothing is taken from the dyno curve. A measured manifold (protocol below) would replace the estimate. |
| **Q2** Resonance scope | Phase 1 has **one runner–cylinder mode per active stage per bank**. Its fundamental frequency comes from Helmholtz with a distributed-runner correction. It does not depend on cam timing, and its response is a positive resonance curve peaking at the tuned speed. **Deferred:** the plenum and group resonance (the two-mode model and resonance-flap physics), higher pipe harmonics, intake-closing coupling of the runner's optimum, and off-tune losses. |
| **Q3** Calibration | The repository has **one real-engine reference, the M54, and it is held out**; the K20 is fictional. Constants therefore come from physics and pre-registered priors. Only the two valve-event level constants (v₀ and the VE ceiling) are re-anchored, on the K20 stock regression anchor. A 14-configuration validation set checks that the model transfers across engines, and it fits nothing. The K20 anchor tolerance and the M54 rules are written down before implementation. |
| **Q4** Boosted engines | The same model applies under boost, with no turbo-specific term. The speed of sound uses the bank's manifold (post-intercooler) temperature **from the previous step**, which keeps the resonance out of the flow root find. The relative amplitude is unchanged by pressure because it is Mach-based. The plenum mode, when it comes, needs the charge-air system's geometry; it is not simply "off". |
| **Q5** Tune regeneration | Done, in commits separate from any physics. Two driver rules make the output legitimate: the hardware schedule is set before the maps measured on it, and rounding 2-cycles keep their checked-in value. All 12 tunes are now fixed points of their recipes, and the fingerprint is re-baselined. Every change is listed. |
| **Q7** Literature | The primary texts cannot be opened from this environment (every host that holds them was denied). **K and the effective-volume definition stay unverified against Engelman 1973.** The formula forms are re-derived here from first principles. K ≈ 2.0–2.1 is supported only by secondary sources, so its value goes to the owner. Three factual corrections to the Phase 0 specification are listed. |
| **Acceptance tests** | All five are meaningful in intent. **Tests 2 and 3 are correct as written.** **Test 1:** its ±10 % tolerance is not a physical bound. **Test 4:** its 2 % threshold is a requirement on unsourced constants, and the owner should decide it as one. **Test 5:** its precondition encodes the old schema. None was changed in this pass. |

---

## Q7 — verification of the cited literature (first, because Q1–Q4 depend on it)

### What could be checked
Every host that holds the primary texts was denied by this environment's egress policy, through both the container
proxy and the fetch tool. The denied hosts were:
- document hosts: scribd.com, kupdf.net and archive.org;
- forums and personal pages: fsae.com, web.eecs.umich.edu and mae.osu.edu;
- patents: image-ppubs.uspto.gov and patents.google.com;
- publishers and references: asmedigitalcollection.asme.org, saemobilus.sae.org, sciencedirect.com, link.springer.com
  and en.wikipedia.org.

Only web-search result summaries and GitHub were reachable. Everything below is therefore classified by what those can
and cannot establish, and **nothing is marked "verified" unless the value was read in a source or derived here**.

| Item (Phase 0 spec) | Status after this pass | Evidence | Usable in Phase 1 as |
|---|---|---|---|
| Helmholtz frequency `f_H = (a/2π)·√(A/(L·V))` | **Derived** (below) | first-principles lumped acoustics | physical |
| Effective volume `V_eff = V_c + V_d/2 = V_d(CR+1)/(2(CR−1))` | **Derived** as the intake-stroke mean of the cylinder volume, under the assumption that the volume is frozen at its stroke mean (A8). **Not verified** as Engelman's definition. | two independent secondary sources agree with it. [COBEM 2017 (UFSC)](https://repositorio.ufsc.br/bitstream/handle/123456789/195030/Alves_Cancino-COBEM-2017-1693.pdf?sequence=1&isAllowed=y), search summary: "V is half the displacement plus the dead space of a cylinder". An [FSAE MATLAB script](https://github.com/mw95710/cubesat-propulsion-FSAE/blob/master/Intake_Manifold_Helmholtz_Resonance_Calculation.m), read directly: `V1 = (V/2)*((R+1)/(R-1))`, "volume of cylinder at mid stroke" | derived, assumption A8 |
| Tuned speed `N_t = 60·f/K` | **Form derived**: the forced system is linear, so the optimum speed is proportional to f. | the script's constant 162 = 60·12·√2/(2π) is exactly `60·f_H/K` with V_eff above, in inch and ft/s units | physical form |
| **K ≈ 2.1** ("Engelman's design rule") | **Unverified.** Secondary sources give 2.0–2.1. The first-principles lumped model below does **not** reproduce it: it gives n* = 1.2–1.8, depending on intake closing and damping. | COBEM 2017 summary: "a constant around two" (the same summary gives 1953 and "two-stroke", so it is not reliable in detail). The FSAE script uses `k = 2`. A search summary of [SAE 2007-01-0382](https://mae.osu.edu/sites/default/files/2021-11/J72.pdf) mentions "preliminary values like K = 2.1"; the paper itself was not opened. | **owner decision U1**; not a sourced constant |
| Two-mode (runner + plenum) formula | **Derived** (below): `αβx² − (αβ+α+1)x + 1 = 0`, x = (ω/ω₁)², α = L₂A₁/(L₁A₂), β = V₂/V₁ | same as the FSAE script's formula; source attributed to Thompson & Engelman (unverified) | physical; deferred (Q2) |
| End correction δ = 0.85 (Rayleigh, flanged) | **Corrected.** A flanged circular pipe has 0.8216·r (Norris & Sheng 1989). 0.85 ≈ 8/(3π) is the baffled rigid piston, a different boundary. Unflanged 0.6133·r (Levine & Schwinger 1948) is standard, but the number was not visible in any reachable source. | search summary of J. Sound Vib. (1989): "0·82159… radii for a flanged pipe" | physical, δ = 0.82 for a runner mouth in a plenum wall. Effect on N_t ≤ 2 %. |
| Speed of sound, forced damped oscillator | standard physics / mathematics | — | physical |
| Thompson & Engelman 1969 (S3), "two types of resonance" | **Title verified**: "The two types of resonance in intake tuning", ASME 69-DGP-11 | search summaries | citation for the concept only |
| Ohata & Ishida 1982 (S5), "VE follows inlet pressure at IVC" | **Title verified**: "Dynamic inlet pressure and volumetric efficiency of four cycle four cylinder engine", SAE 820407. The claim itself is consistent with the title but not read. | search summaries | citation for the concept only |
| Heywood 1988 citation | **Partly corrected.** VE is §6.2 of ch. 6, "Gas exchange processes". Ch. 7 is "SI engine fuel metering and **manifold phenomena**". Phase 0 replaced "ch. 7" with "§6.2", but both chapters are relevant; manifold flow phenomena are ch. 7. | search summaries of the table of contents | cite both, §6.2 and ch. 7 |
| Winterbone & Pearson 2000 (S7) | bibliographic record only | — | citation for linear acoustics only |

### Derivations (so Phase 1 does not depend on unverifiable texts for the formula forms)
**Runner–cylinder resonance (lumped).**
- The runner is a gas slug of mass `ρ·A·L_eff` between a plenum at constant pressure and a cylinder of volume V.
- Momentum: `ρ·L_eff·du/dt = −p′`. Continuity in the cylinder: `(V/(ρa²))·dp′/dt = A·u`.
- Together: `p̈′ + ω²p′ = 0`, with `ω² = a²·A/(L_eff·V)`.

**Runner–cylinder resonance (distributed).** Take a uniform runner of length L_eff with a pressure node at the plenum
and the cylinder compliance `V/(ρa²)` at the valve. The 1-D wave equation then gives the fundamental from
`kL·tan(kL) = β`, with `β = A·L_eff/V` (the runner's volume over the cylinder's).
- For β → 0 it tends to Helmholtz, `kL = √β`.
- As the cylinder volume becomes small against the runner's, it tends to the quarter-wave limit, `kL → π/2`.
- The ratio `c(β) = kL/√β` is 0.95 at β = 0.3, 0.85 at β = 1.1 and 0.76 at β = 2.
- The roots are computed once per stage when the engine is built, never per step.

**Two coupled resonators** (runner 1 with cylinder volume V₁; feeder or resonance tube 2 with plenum volume V₂).
- Inertances are `M_i = ρ·L_i/A_i` and compliances `C_i = V_i/(ρa²)`.
- Normal modes give `αβx² − (αβ + α + 1)x + 1 = 0`, where x = ω²/ω₁², α = M₂/M₁ and β = C₂/C₁.

### Exploratory check of K (not part of the model)
A nonlinear lumped simulation of one intake event was built in scratch, not in the repository:
- an incompressible runner slug and an isentropic cylinder, with the real slider-crank volume;
- K20 geometry, the plenum at constant pressure, a quiescent start at intake opening, and linear damping ζ.

The trapped-mass optimum sits at `n* = f_H/f_rev` of 1.39, 1.29 and 1.19 with no damping, for intake closing 30, 45 and
60° after BDC. With ζ = 0.3 it moves to 1.76, 1.59 and 1.44. The gains (+13 to +95 %) are far above real ones: the model
has no valve restriction and no carry-over of the previous cycle's wave.

Two conclusions:
- **K is not derivable from first principles at this level.** It absorbs losses and timing the lumped model lacks, so its
  value must come from data or a verified source.
- **Intake closing moves the runner's optimum speed** by about +0.5 to +0.7 % per degree of later closing. This is the
  physical form of the cam ↔ runner interaction, and it matters for acceptance test 1.

### A defect in the Phase 0 response formula
The Phase 0 specification wrote `G_wave = 1 + A·[M(r)/M(1)]·cos(φ(r) − φ_ivc)`, with φ_ivc "zero at the reference
closing". At resonance φ(1) = π/2, so with φ_ivc = 0 **the gain is zero at the tuned speed**. The gain then peaks at
r ≈ 0.8 and turns negative above r = 1. Consequences:
- **K loses its meaning.** K is defined by where the gain peaks.
- **Test 2's rig can never pass.** Checked numerically: for every ζ in [0.1, 0.5] and φ_ivc in ±0.6 rad, the rig finds no
  "for good" crossover, because the shorter runner's negative lobe hands the high end back to the longer one.

The consistent form uses the reference phase φ_ivc = π/2. The response is then the quadrature (absorption) curve
`R(r) = 4ζ²r / ((1 − r²)² + 4ζ²r²)`, positive everywhere, peaking near r = 1 and falling off on both sides. Q2 adopts it,
normalised so that its maximum sits exactly at r = 1.

---

## Q1 — M54 DISA provenance

### Evidence and classification
Classes used:
- **sourced**: a published figure, corroborated by several independent sources where the development container could
  only reach search summaries (the M54 convention in PARTS_DATABASE.md);
- **derived**: computed from sourced values;
- **estimate**: an engineering judgement with no source;
- **unknown**: nothing found.

| Quantity | Value | Class | Source / basis |
|---|---|---|---|
| Switching mechanism | Vacuum-actuated resonance flap (solenoid valve and vacuum accumulator), sprung open. Closed = energised. | sourced (secondary, corroborated) | Pelican Parts technical articles ([X3 M54 DISA](https://www.pelicanparts.com/techarticles/BMW-X3/26-FUEL-M54_6_Cylinder_DISA_Valve_Replacement/26-FUEL-M54_6_Cylinder_DISA_Valve_Replacement.htm), [E60 M54](https://www.pelicanparts.com/techarticles/BMW-E60/26-FUEL-M54_6_Cylinder_DISA_Valve_Replacement/26-FUEL-M54_6_Cylinder_DISA_Valve_Replacement.htm)); [BimmerFest "How the DISA works"](https://www.bimmerfest.com/threads/how-the-disa-works.531558/); [eEuroparts](https://eeuroparts.com/blog/disa-valve-rundown-symptoms-replacement). The wording traces to BMW training material. |
| Switch speeds | Closed below ≈ 3,750 rpm, open above ≈ 4,100 rpm ("varies slightly, temperature influenced"). The ECM map uses engine speed, load, vacuum and ambient temperature. | sourced (secondary, corroborated; already in PARTS_DATABASE.md) | same sources |
| Topology | Six runners in **two groups of three**; the flap separates or joins the groups. Closed: "air through one resonance tube"; open: "flow through both resonance tubes". | sourced **qualitatively** (secondary). The exact duct layout is **ambiguous** between sources. | Pelican Parts ("two sets of three runners"), [ASC "Evolution of BMW intake designs"](https://www.ascfabrics.com/post/intake) ("split the intake manifold into two sections of three cylinders"), the BMW training wording above |
| Group excitation | Each group (1-2-3, 4-5-6) fires every 240°: 1.5 intake pulses per revolution | derived | firing order 1-5-3-6-2-4, sourced ([E46 Fanatics](https://www.e46fanatics.com/threads/m54-engine-cylinder-firing-order.879486/), [BimmerFest](https://www.bimmerfest.com/threads/m54-cylinder-number.512787/), several agreeing results). The schema has an optional descriptive `firing_order`, which the M54 does not author and no physics uses; the order
matters only for a group-plenum model. |
| DISA size | Three DISA sizes exist; the largest is on the M54B30 | sourced (enthusiast, single source) | [ZRoadster "M54B30 intake manifold"](https://zroadster.org/threads/m54b30-intake-manifold.25812/) |
| Runner length relative to the M52TU | M54 runners ≈ 10 mm shorter; "D"-shaped ports matching the head | sourced (enthusiast, single source, relative only) | ZRoadster thread (pages 3–4) |
| **Runner length (plenum mouth to valve, including the head port)** | not published; content has 380 mm | **estimate** (no source; PARTS_DATABASE.md already says so) | — |
| **Runner cross-section** | not published | **unknown**; the model default 0.40·bore = 33.6 mm diameter is assumption A5 | — |
| **Group plenum volumes** | not published | **unknown** | — |
| **Resonance tube lengths and diameters** | not published | **unknown** | — |
| **Flap bore** | not published | **unknown** | — |
| Throttle | 68 mm | sourced (listing; in content) | PARTS_DATABASE.md |
| Displacement per cylinder, CR | 496.5 cc, 10.2 | sourced, and the CR is derived by the model | PARTS_DATABASE.md |
| V_eff per cylinder | 302 cc | derived (V_c + V_d/2) | — |

**No value was, or may be, fitted to the M54 torque curve.** The reference torque shape comes from dyno-thread search
summaries: a hump at 3,500 rpm, a dip at ≈ 4,000–4,200 rpm and a second hump. It remains a held-out validation target
(Q3).

### Representation options
| Option | Unknown dimensions to estimate | Physics | Verdict |
|---|---|---|---|
| (a) Physical group-plenum model: runners + two group plenums + resonance tubes + flap (the two-mode model per group, joined when the flap is open) | 5 (runner length and area, group volume, tube length and area), plus an ambiguous topology | right | **Rejected for Phase 1**: the most assumption-dependent option, and it needs the deferred plenum mode (Q2) |
| (b) Two free effective stages, both lengths estimated | 2 lengths, 1 diameter | effective | Rejected: the closed-stage length is a free knob, and choosing it is exactly the fitting the review forbids |
| **(c) Two effective stages; the open stage is the runner estimate, the closed stage is derived from the sourced switch band** | 1 length and 1 diameter (open stage), plus assumption A-D1 | effective | **Chosen.** It is the least assumption-dependent: one estimate and one stated assumption. It is agnostic to the ambiguous duct topology, and the closed stage is pinned by a sourced ECU fact, not a torque number. |
| (d) Measured manifold (owner) | 0 | physical (enables (a) later) | **Preferred upgrade**; protocol below |

**Assumption A-D1.** At full load, the manufacturer switches DISA at the torque crossover of its two stages. This is the
same rule the authoring guide applies to switch speeds. It is plausible for a production calibration, and it is
consistent with the reference curve's dip at the switch: the envelope of two humps has its minimum at their crossover.
A-D1 is an assumption for the owner to accept or reject (U4). The sourced band also contains a ±175 rpm hysteresis, and
it is not stated to be the full-load band; both are part of the uncertainty.

**Procedure** for Phase 1, recorded before the first M54 run:
1. Pre-register the Phase 1 shared constants (Q3).
2. Open stage: the runner, L = 380 mm (estimate) with diameter 0.40·bore (A5). Record the band L ∈ [300, 450] mm and
   d ∈ [30, 40] mm.
3. The sourced switch band excludes open-stage geometries whose tuned speed falls below it. With K = 2.1, L = 450 mm and
   d = 30 mm puts the open stage at ≈ 3,500–3,550 rpm, below 3,750 rpm, so that combination is inconsistent with the
   sourced fact.
4. Closed stage: the effective length whose stage-held full-load crossover with the open stage, in the Phase 1 model on
   the stock M54 tune, falls at the centre of the sourced band, 3,925 rpm. This is computed once, documented in
   PARTS_DATABASE.md as "derived from the sourced switch band under A-D1", and frozen.
5. Only then evaluate the M54's full-load curve and acceptance test 5. Report it at the nominal geometry **and** across the
   open-stage band (a sensitivity table). A result that passes only somewhere in the band is reported as not
   demonstrated, and no value is moved toward the pass.

Illustration only, not a result. It uses the candidate response of Q2 with ζ = 0.3 and K = 2.1. For the nominal runner
the open stage sits at ≈ 4,200–4,300 rpm and the derived closed stage near the switch band. The crossover of two
quadrature responses sits below the geometric mean of their tuned speeds, by 1–17 % for ζ 0.15–0.5 and stage spacings of
1.1–2, and at ζ ≥ 0.3 with close stages it can fall below the lower tuned speed. The closed stage's final value
therefore depends on ζ. That is why step 4 uses the model with its pre-registered constants rather than a formula now.

**Measurement protocol** (option (d); it would make the open stage and the plenum data "measured"). A used M54B30
manifold (part numbers differ by DISA size) is enough:
- runner centreline length from the plenum mouth to the head flange, plus the head's intake port length to the valve seat
  (from a head, or published port data);
- runner cross-section at the mouth, mid-length and flange (D-shape: area from width × height with a shape factor, or a
  moulding compound cast);
- each group plenum's volume by water fill with the flap closed, and the joint volume with it open;
- the resonance tubes' centreline lengths and bores;
- the flap bore.

Photographs and a scale belong in the PR. Each value then enters PARTS_DATABASE.md as "measured".

---

## Q2 — resonance scope and the generic model boundary

### Evaluation
| Mode | What it is | Needed for the game's fidelity? | Phase 1 |
|---|---|---|---|
| **Runner–cylinder resonance** | The runner's gas column against the cylinder volume behind the open valve. Excited once per cycle per cylinder, so its tuned speed follows runner length, area, cylinder volume and the speed of sound. | **Yes.** Every engine has runners, runner and intake swaps are a primary player trade-off, and it is what acceptance tests 1–4 exercise. | **In** |
| Plenum or group resonance | A plenum (or a DISA-type group plenum) with its feeder or resonance tube as the neck, excited at the group's pulse order: 1.5 per revolution for an I6 group of three, n_cyl/2 for a single common plenum | For resonance-flap intakes only (BMW DISA, some V6 resonance valves). A large common plenum largely cancels its pulses. | **Deferred** (Phase 2, "resonance-flap intakes"). It needs geometry no engine in the repo has, and its formula is derived but its excitation and K for group pulses are unsourced. |
| Pipe modes of long runners | Higher harmonics of the runner (the secondary "organ-pipe" peaks at a fraction of the fundamental's speed) | Second-order: weaker secondary bumps | **Deferred.** The **fundamental's** distributed correction is in; see below. |
| Intake-closing coupling of the optimum | Moving intake closing moves the runner's best speed (≈ +0.5–0.7 %/° later closing in the exploratory model) | Refines the cam × runner trade-off | **Deferred** until its coefficient has a reviewed provenance (U6). In Phase 1 the cam acts through η_ve only. |
| Off-tune losses | A negative gain when the wave arrives out of phase | Refinement | **Deferred.** Phase 1's gain is ≥ 1 (bounded, conservative). |
| Inter-cylinder interference, exhaust tuning, part-load individual-throttle-body acoustics | — | No | out of scope (as in Phase 0) |

### Why the distributed correction of the fundamental is in
Across the repo's runners β = A·L_eff/V_eff ranges from 0.75 (K20 230 mm) to 1.63 (`syn_i6_vis` 450 mm), with default
diameters. At K20 geometry the lumped Helmholtz formula overestimates the fundamental by about 15 %. Across engines the
error varies from 11 to 21 %, which is more than every other placement uncertainty (K's secondary spread is ±2.5 %). It
would also distort the length and diameter trade-offs players make. The exact fundamental of the same single mode costs
nothing per step (a root solved at build time), and its derivation is standard, as shown above.

To keep K meaningful in the terms of the literature formula it was stated for, the correction is **normalised at
β_ref = 1**: `f = f_H · c(β)/c(1)`. This is assumption A7, since Engelman's test geometries are unknown. The owner may
prefer plain Helmholtz (U2); the pending tests accept both (test 2 expects exponent 0.474 with plain Helmholtz and 0.608
with the correction).

Tuned speeds the candidate formula implies (default diameter 0.40·bore, δ = 0.82, 320 K; a plausibility check, not a
fit):

| Engine, runner | V_eff | β | f₁/f_H | N_t, Helmholtz, K = 2.1 | K = 2.0 | Rev limit |
|---|---|---|---|---|---|---|
| K20 340 mm | 302 cc | 1.09 | 0.850 | 4,804 | 5,044 | 7,600 |
| K20 230 mm | 302 cc | 0.75 | 0.891 | 5,786 | 6,075 | 7,600 |
| M54 380 mm (estimate) | 302 cc | 1.16 | 0.843 | 4,450 | 4,673 | 6,500 |
| syn_i4_sohc 380 mm | 246 cc | 1.25 | 0.832 | 4,642 | 4,874 | 6,500 |
| syn_i3_turbo_vvl 300 mm | 247 cc | 0.97 | 0.864 | 5,132 | 5,389 | 6,500 |
| syn_i4_turbo 280 mm | 310 cc | 0.88 | 0.874 | 5,206 | 5,466 | 7,000 |
| syn_i6_vis 450 / 250 mm | 252 cc | 1.63 / 0.93 | 0.794 / 0.869 | 4,495 / 5,960 | 4,720 / 6,258 | 7,000 |
| syn_h4_turbo 250 mm | 313 cc | 0.90 | 0.872 | 5,836 | 6,127 | 7,000 |
| syn_v6_na 400 mm | 355 cc | 1.27 | 0.830 | 4,428 | 4,649 | 6,800 |
| syn_v6_tt 250 mm | 311 cc | 0.79 | 0.886 | 5,487 | 5,762 | 7,000 |
| syn_v8_ohv 300 mm | 305 cc | 1.15 | 0.844 | 5,539 | 5,816 | 5,800 |
| syn_v8_dohc_vvt 350 mm | 300 cc | 1.29 | 0.828 | 5,080 | 5,334 | 7,500 |

Every tuned speed falls inside its engine's rev range, in the mid-to-upper band where production intakes are tuned. The
OHV V8's sits near its limit, which is a useful edge case for the validation set.

### The Phase 1 model boundary (design; not implemented)
`VE_dyn = η_ve(N, cams) · G_wave(N, T_man, stage) · (1 + scavenging) · float`. One evaluation per bank per step, before
the air-path root find, exactly as VE_dyn is evaluated today.

1. **η_ve**, valve-event filling: today's parabola. v₀ and the VE ceiling are re-anchored (Q3); k_d, the side
   coefficients and the floor are unchanged. The cam phaser, VVL profile and centrelines act here and **only** here.
2. **Tuned speed** of the active stage:
   - `N_t = 60·f/K`, with `f = (a/2π)·√(A_r/(L_eff·V_eff))·c(β)/c(1)`;
   - `a = √(γ·R·T_man)`, `L_eff = L + 0.82·r_r` and `V_eff = V_c + V_d/2` of the bank's geometry;
   - the ratio c(β)/c(1) and `N_t(T_ref)` are computed per stage when the configuration is built; per step
     `N_t = N_t(T_ref)·√(T_man/T_ref)`.
3. **Response** `R̃(r)`, `r = N/N_t`: the quadrature curve `R(r) = 4ζ²r/((1 − r²)² + 4ζ²r²)`, rescaled so its maximum is 1
   at r = 1. The rescaling is a constant per ζ, and the gain peaks at N_t by construction.
4. **Amplitude** `A = min(A_max, κ·M_r)`, with `M_r = ū_r/a` and `ū_r = V_d,cyl·(N/120)/(A_r·f_event)` (unchanged from
   Phase 0: relative pressure amplitude ∝ Mach number).
5. **Gain** `G_wave = 1 + A·R̃(r)`, bounded to `[1, 1 + A_max]`. It does not depend on cam timing in Phase 1.
6. **Stages**: N-stage schema (`stages: [{runner_length_mm, runner_diameter_mm?}]`), with today's
   `switched_runner_length_mm` read as the N = 2 case. ECU switching and the 150 rpm hysteresis are unchanged. A
   DISA-type stage is authored as an effective geometry with provenance (Q1).
7. **Defaults**: runner diameter 0.40·bore (A5), flagged `default_intake_geometry` by the validator. There is no plenum
   default because there is no plenum mode.
8. **Removed**: the `(0.300 m/L)^0.25` factor and `ReferenceRunnerLength`. **Unchanged**: scavenging, valve float,
   residuals, the air-path solve.

Cost: per bank and step, one square root and one rational function, with no allocation and no new root find. The +10 %
step-time budget stands.

---

## Q3 — calibration strategy (multi-engine, not M54-specific)

### The constraint that shapes the strategy
The repository contains **one real-engine reference**: the M54's published figures and reference curve. The Kestrel
K20 is fictional; PARTS_DATABASE.md says its parts were not authored to the real K20A. The owner has ruled that the M54
may not be fitted. The synthetic engines are fictional by construction.

So no multi-engine *fit* to external data is possible with the current content. With only the K20's own (old-model)
curve as a target, K and v₀ are degenerate: both are placement constants, and one curve cannot separate a runner bump
from a valve-event hump that the old model fused. The E10 prototype's unsourced share *g* was the same degeneracy.

The strategy therefore **minimises fitted constants**:
- fix what physics or a source fixes;
- pre-register priors for what no reachable source fixes;
- re-anchor only the valve-event level on the K20 (regression, not reality);
- check transfer on a diverse validation set that is never fitted;
- hold the M54 out.

A future milestone could add a second real reference with published intake geometry and curves. That would turn priors
into fits (listed under "Unresolved").

### Constants
| Constant | Phase 1 value from | Fitted? |
|---|---|---|
| γ, R, `a = √(γRT)` | physics | no |
| f_H form, V_eff | derived (Q7; A8) | no |
| c(β)/c(1) | derived (Q7); β_ref = 1 is A7 | no |
| δ = 0.82 | Norris & Sheng 1989 (flanged pipe) | no |
| **K** | pre-registered prior from secondary literature, 2.0–2.1 (**U1**); primary unverified | no |
| **ζ** | pre-registered prior (**U3**); proposed 0.25, sensitivity [0.15, 0.5] | no |
| **κ, A_max** | pre-registered prior (**U3**); proposed A_max = 0.12 (A3: ≤ 0.15), κ such that A(5,000 rpm) ≈ 0.06–0.08 on the K20 | no |
| **v₀, VE ceiling** | re-anchored on the K20 stock (R2 below) | **yes, the only fit** |
| k_d, a_lo, high side, floor, 112° reference ICL | unchanged | no |

Pre-registered means that the values are written into the Phase 1 PR's specification **before** the first K20 or M54
run, and they are not changed afterwards to meet an acceptance test. If a test then fails, it is reported and classified.

### Validation set (checks transfer; fits nothing)
| # | Engine / configuration | What is diverse | Observable | Would expose |
|---|---|---|---|---|
| 1 | K20 stock, RON 95 | reference NA I4, fixed cams, 340 mm | full-load curve against the anchor tolerance (R2); λ on target after regeneration | v₀ and the ceiling anchor; K · ζ · A combined |
| 2 | K20 short runner (230 mm) | runner length only | crossover and length exponent (test 2: 0.47–0.61 expected) | the frequency formula (Helmholtz vs distributed) |
| 3 | K20 at 263 / 323 K ambient | charge temperature | crossover shift = √(T_hot/T_cold) − 1 (test 3) | the speed of sound source (T_man) |
| 4 | K20 NA build (race cams, ported head, 4-2-1) | cam duration | the VE peak moves with duration; the runner bump does not | k_d (unchanged), the cam independence of N_t |
| 5 | K20 T28 and T35 (RON 98) | boost, intercooled | boost loop stable; λ on target; the tuned speed shifts with charge temperature only | the boost treatment (Q4), A_max under boost |
| 6 | syn_i6_vis | fixed-cam I6, 450/250 mm VIS, β = 1.63 | each stage wins in its band; the switch speed re-derived by the driver | ζ and A (test 4 threshold), the distributed correction |
| 7 | syn_i4_sohc | long runner (380 mm), small bore, 2-valve | bounded; peak location plausible | the Mach amplitude at a small runner area |
| 8 | syn_v8_ohv | large bore, low rev limit (5,800), N_t near the limit | bounded; no artefact at the rev limit | edge placement |
| 9 | syn_v8_dohc_vvt | phaser, two banks | the phaser does not move N_t; the banks equal one bank of all cylinders | cam independence, per-bank |
| 10 | syn_v6_na | two banks, NA, 400 mm | bounded, λ | multi-bank |
| 11 | syn_i4_turbo, syn_v6_tt, syn_h4_turbo, syn_i3_turbo_vvl | boosted, phased turbo, a turbo shared by two banks, VVL | boost stability, turbo invariants, λ | boost, sharing |
| 12 | M54 stock (VANOS + DISA per Q1) | real engine, phased I6, two-stage | **held out**: shape criterion (test 5) and bands, across the Q1 band | everything, as an honest miss if it misses |
| 13 | M54, phaser parked | fixed-cam M54 | hump position against the K20's | placement transfer between families (diagnostic) |
| 14 | M54 + test-only 380/250 mm two-stage (test 4); M54 with the cam held at 10°/50° (test 1) | runner vs phaser | stage benefit; crossover under the phaser | ζ and A; cam independence |

### How K20 and M54 regression protection interacts with the generic calibration
- **R1, before physics:** tunes are regenerated under the old physics and the fingerprint is re-baselined (Q5, done).
  The Phase 1 diff then shows physics only.
- **R2, the K20 stock anchor (pre-registered tolerance)**, measured after Phase 1's regeneration: full-load torque
  within ±3 % at every 250 rpm point from 1,500 to 7,000 rpm; peak torque and peak power within ±2 %; peak-torque speed
  within ±500 rpm; 0–100 km/h within ±3 %. Only v₀ and the VE ceiling move to meet it. If it cannot be met, stop and
  report; do not add a constant. The K20 is the anchor because it is fictional: its current curve is its definition, and
  the old constants were fitted on this very configuration. Holding it pins the level without pretending it is reality.
- **R3, the K20's other configurations** (short runner, NA build, T28, T35) are **not** anchored. The old
  `(0.300/L)^0.25` factor was physically wrong (the tuned speed went as L^−0.254), so they are expected to move. Each case
  is reported from `carsim fingerprint-diff` with its physical explanation; the invariants and λ guards must hold.
- **R4, the M54 is never an input** to a shared constant.
  - Its Phase 1 content change is limited to the DISA stages, by the Q1 procedure, with values fixed before any M54
    torque output is looked at.
  - The `M54ReferenceTests` bands (±10 % torque, ±15 % power) must hold.
  - Test 5 is reported across the Q1 band. A miss is classified (A/B/C/D) and never closed by content.
- **R5, the synthetic matrix** stays content-only; its tunes are regenerated by the driver. The guards (bounded,
  λ ≤ 4 %, hysteresis, the phaser optimum, allocation) hold.
- **R6, commits:** physics, regenerated tables and the re-baseline land in one Phase 1 commit. That commit's report
  separates the two effects by running `fingerprint-diff` twice: the physics on the pre-physics tunes, then with the
  regenerated tunes.
- **R7:** `TorqueCurveDiagnosisTests` pin today's limitation. Phase 1 retires or inverts them deliberately, and each
  change is listed.

---

## Q4 — boosted engines

**Physics.**
- **No pressure dependence of the tuned speed.** The resonance frequency depends on a and the geometry, not on the mean
  pressure: in `ω² = a²A/(LV)` density cancels. Boost therefore moves the tuned speed only through the charge temperature.
- **No pressure dependence of the relative amplitude.** The relative pressure amplitude of a wave carried by velocity u
  is `Δp/p ≈ γ·u/a`. At a given engine speed a runner carries the same volume flow whatever the manifold density, so the
  relative gain is unchanged, and the absolute gain scales with the boosted charge it multiplies.
- **Neither boundary of the runner mode changes under boost.** The plenum is still a pressure node for it, because the
  plenum volume is large against the cylinder's and β is small at the plenum end. The cylinder is the other end.

**Decisions.**
1. The same G_wave on every engine, NA or boosted. **No turbo-specific resonance term**: the physics does not require
   one.
2. **Temperature source.**
   - The speed of sound uses the bank's manifold gas temperature: the intercooler or charge-pipe outlet, `tMan` in
     `AirPath.Evaluate`.
   - On a boosted engine `tMan` depends on the flow being solved for (compressor outlet and intercooler effectiveness).
     G_wave therefore uses **the previous step's value**, as a per-bank state initialised to ambient.
   - This keeps VE_dyn evaluated once before the root find, as today, with no extra root find and deterministic results.
   - The lag is one step (2–5 ms) of a quantity that changes over seconds. On an NA engine `tMan` is ambient, so the
     value is exact.
3. **Relative amplitude**: Mach-based as specified, so a hotter charge (a higher a) gives a slightly lower relative
   amplitude at the same speed. This is physical and needs no boost term.
4. **Bounds**: 1 ≤ G_wave ≤ 1 + A_max under boost too, and `IntakeGasDynamicsGuardTests` keeps VE_dyn ≤ 1.35. Closed-loop
   boost stability, the turbo invariants (first and second law, no reversal, anti-windup) and λ on target are
   validation-set rows 5 and 11.
5. **Plenum mode on boosted engines** (for Phase 2): its upstream boundary is the compliant charge-air system
   (intercooler, pipes and compressor), not a pressure node. The mode cannot be derived without those volumes, which are
   not part data. Assumption A4 ("off") is replaced by a data rule: the plenum mode requires upstream geometry, and the
   validator reports it as absent. This is moot in Phase 1, which has no plenum mode.

---

## Q5 — tune regeneration under the existing physics (done, separate commits)
Two commits, neither of which touches simulation code:
1. **Driver rules** (`tools/CarSim.Verification`, `tune-manifest.json`, tests, authoring guide):
   - **Hardware schedule first.** Cam phase and switch speeds are set before the fuel and spark maps measured on them.
     The old `syn.r6` and `syn.t3` recipes set the switch speed last, so moving `syn.r6`'s switch from 4,600 to 6,400 rpm
     left 24 VE cells (up to 0.021) measured on the other stage. This is not a legitimate table. The authoring guide's
     §6 had the same order; it is fixed, and `RecipesSetTheHardwareScheduleBeforeTheMapsMeasuredOnIt` enforces it.
   - **Rounding 2-cycles keep their checked-in value.** 39 calibrated spark cells flipped 0.5° on one pass and flipped
     back on the next: spark ↔ VE across a rounding step, with two recipe-consistent states. Where a regeneration changes
     anything, the driver now runs the recipe a second time. A cell that returns to its checked-in value keeps it, and a
     cell that moves to a third value is reported as unsettled. Test: `RoundingTwoCyclesKeepTheirCheckedInValue`.
2. **Regenerated tables** (`carsim regenerate-tunes --write 1` from the pre-regeneration tunes, then the fingerprint
   re-baselined):
   - only calibrated tables were written; hand-authored tables and every other value are byte-identical (checked by
     parsing every tune before and after);
   - all 12 recipes now reproduce their tunes.

The full before/after for every changed tune, with every changed cell and the effect on outputs, is in
[intake-gas-dynamics-2/TUNE_REGENERATION_2026-09-27.md](intake-gas-dynamics-2/TUNE_REGENERATION_2026-09-27.md).

---

## Acceptance tests — are they physically meaningful? (none changed in this pass)
Expected values below come from three places:
- the physics (derivations above);
- the candidate Phase 1 model of Q2 (scratch calculation);
- today's model (Phase 0 measurements).

### 1. `TheRunnerCrossoverDoesNotMoveWithTheCamPhaser`
M54, 380 vs 250 mm test-only runners, intake cam held at 10° and 50°. Criterion: the crossover exists in 1,500–6,400 rpm
at both phases, and the ratio x(50°)/x(10°) is in [0.90, 1.10].

- **Intent:** the runner's own response is not carried by the phaser. This is physically right and the milestone's core
  requirement.
- **Today:** the valve-event optimum moves with the phaser by the correlation's 95 rpm per degree. From 10° to 50° the
  tuned piston speed falls from 14.1 to 2.1 m/s (onto its guard), so there is no crossover at 50°.
- **Physics:** in a model with no intake-closing coupling the crossover is exactly invariant. In the candidate model it
  is 4,300–4,840 rpm for ζ 0.5–0.15, and the ratio is 1.00. The exploratory lumped model, which has the coupling, moves
  it to **lower** speed with earlier closing by 11–22 % for 40° at ζ 0.15–0.3, and at ζ = 0.45 it leaves the range.
- **Verdict:** meaningful. **The ±10 % band is not a physical bound**: a model with the real closing coupling could fail
  it while being right.
- **Proposed revision (U5):**
  - keep the existence requirement;
  - use the band `0.70 ≤ x(50°)/x(10°) ≤ 1.05`, in the physically predicted direction. It admits both the Phase 1 model
    and a future coupled one and still rejects today's;
  - add a component-level Phase 1 test that the tuned speed has no cam input at all.

### 2. `TunedSpeedScalesWithRunnerLengthAsTheWaveModelsPredict`
K20, 230/340/460 mm. Criterion: exponent in [0.45, 1.1] against the geometric-mean lengths.

- **Physics:** Helmholtz with end correction gives `e ≈ ½·L̄/(L̄ + δr)`, which is 0.474 here. The distributed fundamental
  gives 0.608. The quarter-wave limit gives 1. The candidate model reproduces 0.474 and 0.608 for every ζ in
  [0.15, 0.5], and the lumped model gives 0.473–0.477.
- **Today:** 0.254.
- **Verdict:** correct as written. The margin below Helmholtz is small (0.474 against 0.45). The end correction alone
  gives `e = ½·ln((460 + c)/(230 + c))/ln 2`, with c = 0.82·r, which reaches 0.45 only at a runner diameter of
  ≈ 80–87 mm, far outside production runners.
- **Caveat:** the rig's crossover is "the last sign change, for good". A response with a negative lobe above resonance
  defeats it, as the Phase 0 formula did. The positive response of Q2 does not.

### 3. `AHotterChargeRaisesTheTunedSpeedWithTheSpeedOfSound`
K20, ambient 263 → 323 K. Criterion: crossover shift in [0.5, 1.5] × (√(T_hot/T_cold) − 1), using the reported charge
temperature.

- **Physics:** every placement scales with a, and the Mach amplitude is invariant at a fixed r = N/N_t. The whole response
  therefore scales exactly with √T of the runner gas: the candidate model gives +0.0938 against +0.0938.
- **The temperature in the prediction:** the test's prediction uses the in-cylinder charge temperature (≈ 270 → 323 K,
  +9.3 %), while the runner gas in the model is at `T_man`, which is ambient on an NA engine (+10.8 %). Both lie well
  inside the band.
- **Verdict:** correct as written.
- **Optional refinement:** predict from `ManifoldTemperature`, the runner gas, to tighten the band later.

### 4. `ASwitchedRunnerActsOnAPhasedEngine`
M54 with a test-only 380/250 mm two-stage intake and the shipped VANOS map. Criterion: each stage wins by ≥ 2 % in its
own range, with the long stage lower.

- **Physics:** with the cam out of G_wave, two stages with tuned speeds 22 % apart each win around their own N_t. The
  ordering is guaranteed. The size of the win is `≈ A·(1 − R̃(N_t,short/N_t,long))`, and it depends entirely on ζ and A.
- **Candidate model:** the long stage wins by 3.1–5.8 % at ζ = 0.15, by 1.5–3.1 % at ζ = 0.3 and by 0.7–1.5 % at ζ = 0.5,
  with A(4,000 rpm) of 0.05–0.08. **The ≥ 2 % threshold is therefore a requirement on the unsourced ζ and A** (ζ ≲ 0.3
  with A ≳ 0.05).
- **Verdict:** the structure (both stages matter, in the right order) is meaningful. The number is a game-design
  requirement ("a variable intake must visibly matter"), not a physical law.
- **Proposed (U3/U5):** the owner either adopts ≥ 2 % as a design requirement that the pre-registered priors must
  satisfy (then ζ ≤ 0.3), or lowers it to what the priors imply. Deciding it after seeing Phase 1 output would be a hidden
  fit.

### 5. `TheM54RisesIntoAMidRangePeakWithItsDisaAuthored`
Criterion: the M54 with DISA authored peaks at 2,750–4,750 rpm, ≥ 3 % above its 1,500 rpm torque; peak torque 270–330
N·m, power 144.5–195.5 kW.

- **Physics:**
  - Under VANOS, η_ve stays near its ceiling at every speed, so the curve's shape is G_wave's.
  - With the closed stage near the switch band (Q1), the curve rises from 1,500 rpm toward the closed stage's N_t: by
    `A·(1 − R̃(1,500/N_t))`, about 0.8·A.
  - The ≥ 3 % rise therefore needs A ≳ 0.04 at the closed stage's tuned speed.
  - The bands are the published figures ±10 % / ±15 % (`M54ReferenceTests`).
- **Verdict:** meaningful as external, held-out validation.
- **Required revisions (U5):**
  1. The precondition `SwitchedRunnerLengthMm != null` encodes the two-length schema. Under Q1 and Q2 it becomes "the M54
     intake has two stages with provenance".
  2. The test must report across the Q1 uncertainty band, with the nominal pre-registered; add a sensitivity output.
  3. The 3 % rise and the 2,750–4,750 rpm window are shape tolerances around a reference read from dyno-thread
     summaries. No point value below 3,500 rpm was found. Keep them, labelled as tolerances.

---

## New or changed assumptions (to add to SIMULATION_SPEC.md when Phase 1 is authorized)
- **A2 (IVC phase coupling)**: withdrawn from Phase 1 (deferred, U6).
- **A3 (A_max ≤ 0.15)**: kept, as a bound on a pre-registered prior.
- **A4 (plenum off under boost)**: replaced by a data rule (Q4).
- **A5 (runner diameter 0.40·bore)**: kept, and flagged when used.
- **A6 (plenum default)**: moot in Phase 1.
- **A7**: the distributed correction is normalised at β_ref = 1.
- **A8**: the cylinder volume is frozen at its intake-stroke mean for the resonance.
- **A-D1**: DISA switches at its full-load stage crossover.

## Unresolved questions (owner decisions before Phase 1 code)
- **U1 — K.** Obtain Engelman 1973 (ASME 73-WA/DGP-2) and verify K and V_eff, or accept K ∈ [2.0, 2.1] as a
  secondary-literature prior with its ±2.5 % placement uncertainty. **Blocking for the constant table.**
- **U2 — frequency formula.** Distributed-corrected (recommended, A7) or plain Helmholtz (the literature's form; an
  11–21 % geometry-dependent placement error across the repo's runners).
- **U3 — amplitude and damping priors.** No reachable source fixes ζ, κ or A_max. Proposed: ζ = 0.25, A_max = 0.12, κ
  such that A(5,000 rpm) ≈ 0.06–0.08 on the K20, with sensitivity bands. Also: is test 4's ≥ 2 % a design requirement?
- **U4 — M54 DISA.** Accept A-D1 and the Q1 procedure, supply a measured manifold, or defer acceptance criterion 1 to
  Phase 2 with the plenum mode.
- **U5 — test revisions.** Test 1 (band), test 4 (threshold, see U3), test 5 (precondition, sensitivity). None applied
  yet.
- **U6 — intake-closing coupling.** Defer (recommended), or include in Phase 1 with the exploratory lumped model's
  coefficient (≈ 0.5–0.7 %/°; unverified, which by the owner's rule excludes it).
- **U7 — K20 anchor tolerance** (R2: ±3 % per point, ±2 % peaks, ±500 rpm, ±3 % 0–100). Confirm or change it before
  Phase 1.
- **U8 — second real reference.** A future engine with published intake geometry and curves would let priors become
  fits. Not needed for Phase 1.
- Carried over from Phase 0, unchanged by this pass:
  - the hand-authored K20 spark maps above the calibrator's ceiling (74 and 80 cells; keep, lower or convert);
  - the worn project car's autopilot lap. It is not a structural failure but a knife edge: the tune regeneration flipped
    it. It is reclassified as a fingerprint diagnostic in the Phase 1 proposal.

## Phase 1 entry conditions (for authorization; nothing started)
- U1, U3, U4, U5 and U7 answered.
- The constant table pre-registered in the Phase 1 specification.
- Q1's measurement if the owner chooses it.

Phase 1 then follows R1–R7 and the milestone's test matrix.
