# Milestone: Intake Gas Dynamics 2.0

**Status: PROPOSED — awaiting the owner's explicit authorization.** Defined by the project gate of 2026-09-27 (after
the engine-architecture milestone). Do not start implementation until the owner authorizes it; the owner may change
the scope below.

## Goal
Separate the two things the current volumetric-efficiency model fuses into one filling hump:

1. **Valve-event filling** — how well the cylinder traps charge given intake closing (IVC) relative to piston speed,
   overlap and the cam profile. Cam phasing, a second lift profile and the installed centrelines act here.
2. **Intake gas dynamics** — the pressure waves in the runners and plenum that raise (or lower) the pressure at the
   intake valve at certain engine speeds. Runner length and diameter, plenum volume, the charge's speed of sound and
   a switched runner or plenum state act here. A cam phaser must **not** move this whole response; it only changes
   where the valve event sits against it.

Today a phaser moves the whole hump, so a phased engine fills at its ceiling at every speed, runner length is inert
under a phaser, and a DISA-type intake has nothing to act on (SIMULATION_SPEC.md, "M54 torque-curve investigation").
The correlation's constants (15 m/s tuned piston speed at 220°, 0.15 m/s per degree, the 112° reference centreline,
the 0.300 m reference runner, the shape coefficients) were fitted on one engine, the K20.

The model must represent: fixed runners, runner length and diameter, plenum volume, two or more switched runner or
plenum states (generalising today's two-stage `switched_runner_length_mm`), individual throttle bodies (short runners,
no common plenum), throttle and manifold restriction (unchanged), and the interaction between valve timing and intake
dynamics — for NA and boosted engines, on every bank.

## Why this is next
- **It improves every engine**, real or fictional, not one: every engine has an intake, and the torque-curve *shape* is
  what players tune. The M54 benefits only because it uses the generic capability.
- It turns capabilities the architecture already has (cam phasing, variable valve lift, variable intake runners) from
  partly inert into meaningful, and makes intake manifolds, runners and plenums real parts with trade-offs (a future
  intake-manifold market, ITB conversions, plenum sizing).
- It replaces single-engine-fitted constants with physically derived ones, which is the prerequisite for the
  multi-family calibration pass and for adding many engines without re-fitting.
- Alternatives are narrower: another engine adds content, not capability; a supercharger or dry sump is one
  capability for a subset of engines; per-cylinder modelling is large and not yet needed.

## Model constraints
- Reduced-order and closed-form per bank per step: no CFD, no 1-D wave-action (GT-Power-style) solver, no per-cylinder
  pulse integration, no extra root finds in the air-path solve.
- Physical basis with sources (e.g. Heywood, *Internal Combustion Engine Fundamentals*, ch. 7; Winterbone & Pearson,
  *Theory of Engine Manifold Design*; Blair, *Design and Simulation of Four-Stroke Engines*): quarter-wave runner
  tuning and/or Helmholtz resonance (f = a/2π·√(A/(L·V))), speed of sound a = √(γRT) from the charge temperature.
- Every constant is classified in SIMULATION_SPEC.md as physical, derived or fitted. Fitted constants are shared and
  fitted across several engines at once (K20, M54, synthetic fixed- and variable-runner engines) — never per engine.
- Bounded by construction: VE, port pressure and tuning gain have stated physical bounds; no NaN, no negative pressure,
  no runaway, including under boost and at extreme part data (spec fuzz).
- Engines without the new data must get a documented, reviewed default (not silently the K20's numbers).
- The E10 prototype of the M54 investigation (a fixed gas-dynamic share *g*) is an input, not the design: its
  unsourced share is exactly what this milestone replaces with a physically derived amplitude.

## Phase 0 — prerequisites (before any physics change)
The last milestone's verification relied on tools that live outside the repository. This milestone changes K20 and
M54 output on purpose and invalidates every VE table, so the tools must be in the repo first:

1. **Regression fingerprint** — a CLI command (e.g. `carsim fingerprint`) that prints full-precision outputs for a
   fixed build matrix (K20 builds incl. the T28 turbo, M54, representative synthetic engines: WOT and part-load sweeps,
   cold start, dyno run, a driven lap) and a checked-in baseline with a test. Output changes become measured diffs
   that are reported and deliberately re-baselined, not eyeballed.
2. **Recalibration driver** — regenerates the calibrated tables (cams where a phaser exists, VE, spark, VE) of every
   shipped and test tune with the existing dev calibrators, reproducibly. Hand-authored tables (the K20 factory spark
   map) are listed and handled by an explicit rule.
3. **Mutation harness** — the injected-hack list and the script that proves each is caught, so "mutation-checked"
   stays reproducible (restore sources with fresh timestamps so incremental builds do not keep a mutant).
4. **Model specification** written into SIMULATION_SPEC.md (equations, sources, constant classes, bounds, what is not
   modelled) and reviewed before code.

## Test matrix (define with tolerances before implementation)
Structural behaviour, not just "power went up":
1. **Fixed runner** — the gas-dynamic term produces a filling peak at the speed the runner, sound speed and valve event
   predict.
2. **Short vs long runner** — a longer runner moves the tuned speed down (inversely with length); amplitude bounded.
3. **Variable runner (N states)** — each state peaks where its geometry predicts; the ECU's switch selects the envelope.
4. **Switched runner** — switching moves the operating region at the tune's switch speed, with hysteresis, no chatter
   and no step beyond the physical difference between states; without ECU control the primary state stays.
5. **Cam timing** — across the full phaser range the valve-event optimum moves, while the gas-dynamic peak speed stays
   within a stated tolerance (the phaser no longer carries the runner response with it).
6. **Runner × valve timing interaction** — the realised gain depends on where the valve event sits against the wave
   (sign and bounds tested), and a longer valve event changes the gain as the model states.
7. **Charge temperature** — a hotter charge raises the tuned speed ∝ √T (relevant with boost and intercooling).
8. **NA engines** — K20, M54, `syn_i6_vis` (variable runner, fixed cams), `syn_i4_sohc`, `syn_v8_dohc_vvt` (phaser +
   runner): finite, bounded, λ on target after recalibration.
9. **Turbo engines** — `syn_i4_turbo`, `syn_v6_tt`, the K20 T28 build: closed-loop boost stable, existing turbo
   invariants (first/second law, no reversals, anti-windup) green.
10. **Multiple banks** — two alike banks still equal one bank of all cylinders; per-bank runners follow their own data;
    a bank-specific intake change affects only its bank.
Plus the existing suites: energy balance, ECU observability, spec fuzz (over the new fields), clamp activation,
timestep convergence, allocation budget, the architecture invariants and the source audit.

## Acceptance criteria
1. **M54 / DISA:** with generic code and M54 *data* only (DISA's effective geometry documented as estimated, with its
   provenance), the modelled M54 torque curve is no longer a plateau from 1,500 rpm: it rises into a mid-range peak, and
   switching the DISA state visibly changes the curve's shape in the direction the two states predict. The exact
   historical curve need not match; M54 peak torque and power stay within their documented bands (±10 % / ±15 %) or the
   miss is classified and documented — never fitted per engine.
2. **Multiple architectures benefit**, shown by the test matrix — not only the M54.
3. **K20:** changes are a documented generic correction; its reference numbers (stock ≈ 149 hp / 189 N·m, T28 build,
   0–100 km/h) are re-measured and reported with the fingerprint diff; pinned tests re-baselined deliberately.
4. **Every shipped and test tune** regenerated with the in-repo driver; fuel maps on target for every family.
5. **No engine-specific code** (source audit), no per-engine constants; the synthetic matrix stays content-only.
6. **Performance:** engine step within +10 % of today (K20 ≈ 55 µs, M54 ≈ 55 µs, `syn_v6_tt` ≈ 152 µs on the
   reference container) and no additional per-step allocation; scaling with banks unchanged.
7. **All tests, CLI checks, Godot smoke tests (real and synthetic engines) and CI green.**
8. **Docs:** SIMULATION_SPEC (model, sources, constants, bounds), ENGINE_AUTHORING_GUIDE §5 and §6 (intake data and how
   to author it), PARTS_DATABASE (schema), ARCHITECTURE decision log, ROADMAP, the audit.

## Out of scope
CFD or wave-action solvers; exhaust wave tuning beyond the existing scavenging term (a candidate follow-up, together
with an exhaust-opening term for exhaust cam phasing); per-cylinder pulses; intake sound; new real engines; matching
any engine's dyno curve exactly.
