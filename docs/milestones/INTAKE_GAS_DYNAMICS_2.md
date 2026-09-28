# Milestone: Intake Gas Dynamics 2.0

**Status: PHASE 1 IMPLEMENTED — AT THE PHASE 1 GATE, AWAITING THE OWNER'S REVIEW.** Not merged; Phase 2 is neither
defined nor authorized. The owner authorized Phase 1 on 2026-09-27 with the proposal below as the design contract; the
results, deviations and open risks are in
[intake-gas-dynamics-2/PHASE1_GATE_REPORT.md](intake-gas-dynamics-2/PHASE1_GATE_REPORT.md), and the frozen M54 DISA
derivation in [intake-gas-dynamics-2/M54_DISA_DERIVATION.md](intake-gas-dynamics-2/M54_DISA_DERIVATION.md).

History: defined by the project gate of 2026-09-27 (after the engine-architecture milestone). The owner
authorized Phase 0 (verification tooling, the pre-physics baseline, the model specification, sources and acceptance
tests), accepted it, and asked for a design-resolution pass before Phase 1.

That pass is in [INTAKE_GAS_DYNAMICS_2_DESIGN_RESOLUTION.md](INTAKE_GAS_DYNAMICS_2_DESIGN_RESOLUTION.md):
- Q1–Q5 decided or put to the owner;
- the literature's verification status;
- the generic model boundary and the calibration strategy;
- the acceptance-test rationale;
- the tunes regenerated under the existing physics, in their own commits.

U1–U7 are resolved in [INTAKE_GAS_DYNAMICS_2_PHASE1_PROPOSAL.md](INTAKE_GAS_DYNAMICS_2_PHASE1_PROPOSAL.md), the Phase 1
authorization proposal. It holds:
- the locked equations and the provenance of every parameter;
- the revised acceptance tests;
- the regression rules (the K20 anchor);
- the explicit assumptions and the remaining uncertainties.

Where it differs from earlier documents, it governs.

Phase 1 implemented the proposal's section 2 model (SIMULATION_SPEC.md, "Intake gas dynamics"). What follows is the
milestone's definition as written before it; the Phase 1 results section is at the end.

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
- Physical basis with sources (e.g. Heywood, *Internal Combustion Engine Fundamentals*: volumetric efficiency is §6.2
  of chapter 6 of the 1988 edition, and manifold flow phenomena are chapter 7, so both are relevant; Winterbone &
  Pearson, *Theory of Engine
  Manifold Design*; Blair, *Design and Simulation of Four-Stroke Engines*): quarter-wave runner tuning and/or Helmholtz
  resonance (f = a/2π·√(A/(L·V))), speed of sound a = √(γRT) from the charge temperature. The Phase 0 specification's
  sources and their verification status: SIMULATION_SPEC.md, "Intake gas dynamics 2.0 — proposed model".
- Every constant is classified in SIMULATION_SPEC.md as physical, derived or fitted. Fitted constants are shared and
  fitted across several engines at once (K20, M54, synthetic fixed- and variable-runner engines) — never per engine.
- Bounded by construction: VE, port pressure and tuning gain have stated physical bounds; no NaN, no negative pressure,
  no runaway, including under boost and at extreme part data (spec fuzz).
- Engines without the new data must get a documented, reviewed default (not silently the K20's numbers).
- The E10 prototype of the M54 investigation (a fixed gas-dynamic share *g*) is an input, not the design: its
  unsourced share is exactly what this milestone replaces with a physically derived amplitude.

## Phase 0 — prerequisites (before any physics change) — done, see "Phase 0 results"
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

Executable form (Phase 0): items 1–3 and 5 at system level are `IntakeGasDynamicsAcceptanceTests` (pending); items 4, 8, 9
and the allocation half of acceptance criterion 6 are `IntakeGasDynamicsGuardTests` (active); item 6 (interaction sign
and bounds) and the component-level halves of items 1–3 and 7 are written first thing in Phase 1 against the new model's
API, with the tolerances of the specification; item 10 is covered by the existing architecture invariants.

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

## Phase 0 results (2026-09-27)
Deliverables — details and usage in docs/VERIFICATION.md:
1. **Regression fingerprint** (`tools/CarSim.Verification`, `carsim fingerprint`, `tests/baselines/fingerprint.txt`, 38 tests):
   35 cases — K20 (stock RON 95/98, short runner, NA build, T28 on 98/95, T35, and the driver's regeneration of its
   factory VE recipe), M54 (stock 98/95, phaser parked), nine failure holds, both game scenarios, four autopilot drives,
   all nine synthetic families — each section digested at full precision (SHA-256 over every telemetry value), plus
   readable key numbers. Deterministic (parallel and serial runs identical); a 1e-10 relative change of one shared
   constant fails it (mutation `fingerprint-sensitivity`).
2. **Recalibration driver** (`carsim regenerate-tunes`, `tools/CarSim.Verification/tune-manifest.json`): one recipe per
   shipped and test tune, hand-authored tables listed and audited, `--write` that rewrites only calibrated tables and
   keeps each file's format (every tune file round-trips byte-identically).
3. **Mutation harness** (`tools/CarSim.MutationCheck`, 21 mutants from the validation pass, the second family, the
   engine architecture and this milestone; CI job on manual dispatch).
4. **Model specification** — SIMULATION_SPEC.md, "Intake gas dynamics 2.0 — proposed model (Phase 0 specification, NOT
   implemented)": structure, equations, constant classes, bounds, sources and their verification status, assumptions,
   what is not modelled, open questions Q1–Q5. For review before Phase 1 code.

The pre-physics baseline: the fingerprint (every K20, M54 and synthetic output at full precision); the K20 is still
bit-identical to the engine-architecture merge and the M54 is its Phase 1 starting point. Reference numbers (fingerprint
key numbers, full-load sweep with 1 s settle): K20 stock (RON 95) 111.0 kW (148.8 hp) / 189.2 N·m at 3,750 rpm; T28
build (RON 98) 177.5 kW / 292.5 N·m; M54 (RON 98) 153.0 kW / 303.9 N·m at 2,000 rpm (plateau from 1,500 rpm); M54 laps
52.14 s in the Isar C30. Step cost
(`carsim bench`, this container, full load 5,000 rpm, 2 ms): K20 52.5 µs / 10,360 B, M54 51.7 µs / 10,360 B,
`syn_v6_tt` 156.5 µs / 28,032 B — the budget of acceptance criterion 6.

After the design resolution's pre-physics tune regeneration:
- K20 stock and M54: unchanged at this precision; the M54 is bit-identical.
- T28: 177.4 kW / 292.5 N·m.
- Details: [intake-gas-dynamics-2/TUNE_REGENERATION_2026-09-27.md](intake-gas-dynamics-2/TUNE_REGENERATION_2026-09-27.md).

Measured on the current model (the rig of the acceptance tests; SIMULATION_SPEC.md has the details): runner tuned speed
∝ L^−0.254; no charge-temperature effect; the runner's crossover moves with the cam phase (none at 50° advance); a
two-stage intake under the M54's VANOS map changes torque by < 2 % everywhere; `syn_i6_vis`'s switched stage loses to
its primary below ≈ 6,400 rpm, so its shipped 4,600 rpm switch speed costs up to ≈ 2.6 %.

Findings for the owner:
- **No shipped tune is an exact fixed point of its recipe** (VERIFICATION.md, table): the M54's cams and VE are; its
  spark and most others differ by 0.5° in a few cells; the K20 turbo VE by up to 0.026; the synthetic turbo cam maps by
  up to 10°. Regenerating them is a content change that moves the fingerprint (the K20 by 11 VE cells), so Phase 0 did
  not; Phase 1 should regenerate under the old physics first, in its own commit (Q5).
  **Resolved by the design-resolution pass.** Two driver rules were added:
  - the hardware schedule is set first; this also found that `syn_i6_vis`'s VE had been measured on the wrong stage;
  - only values that settle are written; 39 spark cells sat in a 0.5° rounding 2-cycle.

  All 12 tunes are now fixed points of their recipes, and every changed cell is listed in
  [intake-gas-dynamics-2/TUNE_REGENERATION_2026-09-27.md](intake-gas-dynamics-2/TUNE_REGENERATION_2026-09-27.md).
- **Hand-authored spark maps exceed the calibrator's ceiling** in many light-load cells (K20 stock 74, turbo base 80 of
  180, by up to 11.5°). The audit rule reports them; whether they stay is the owner's decision after Phase 1.
- `syn_i6_vis`'s switch speed was not set by the held-stage sweep the authoring guide prescribes (above). Resolved: it
  is now the model's crossover, 6,400 rpm, with its fuel map measured on that schedule.

### Acceptance tests (defined in Phase 0)
The design resolution checked each test against the physics (section "Acceptance tests"):
- tests 2 and 3 are correct as written;
- test 1's ±10 % band is not a physical bound;
- test 4's 2 % threshold is a requirement on unsourced constants;
- test 5's precondition encodes the old schema.

The authorization gate then revised tests 1, 4 and 5 into physical and model relationships (U5; the proposal, section 5,
has the derivations). Tests 2 and 3 are unchanged. None of the five requires a gain of a given size or a torque-curve
shape.

Pending — `IntakeGasDynamicsAcceptanceTests`, skipped unless `CARSIM_RUN_PENDING_ACCEPTANCE=1`; each fails today as shown:

| Test | Criterion (tolerance) | Today |
|---|---|---|
| `TheRunnerResponseIsNotCarriedByTheCamPhaser` | M54, 380 vs 250 mm (test-only), intake cam held at 10° and 50°: the short-over-long crossover exists at both phases (1,500–6,400 rpm), and `x(50°)/x(10°)` lies in 0.70–1.05 (the intake-closing band: 1.00 without the coupling, ≈ 0.78–0.89 with it) | no crossover at 50° |
| `TunedSpeedScalesWithRunnerLengthAsTheWaveModelsPredict` | K20 fixed cams, 230/340/460 mm: crossover scaling exponent against geometric-mean length in [0.45, 1.1] (Helmholtz ½ … quarter wave 1) | 0.254 |
| `AHotterChargeRaisesTheTunedSpeedWithTheSpeedOfSound` | K20, ambient 263 → 323 K: crossover shift within [0.5, 1.5] × (√(T_hot/T_cold) − 1) of the charge temperatures | 0 |
| `ASwitchedRunnerActsOnAPhasedEngine` | M54 + test-only 380/250 mm two-stage intake, shipped VANOS map: ordered stages with a crossover; **the phaser map does not absorb the stage effect** (below 0.7 × the parked crossover: same sign, at least half the size); torque follows filling; the switched curve is the upper envelope. Every threshold is relative. | at 1,500 rpm (cam 42.5°) the stage effect is +0.20 % under the map against −2.75 % parked |
| `TheM54DisaStagesFollowTheirProvenance` | M54 with DISA authored by the proposal's frozen procedure: the stage crossover and the tune's switch speed in the sourced band 3,750–4,100 rpm; closed below, open above; torque follows filling; upper envelope; bands 270–330 N·m and 144.5–195.5 kW. The shape is **reported** as held-out validation, not asserted. | DISA not authored |

Active guards — must hold now and after Phase 1:
- **`K20AnchorGuardTests`** (the U7 anchor, frozen reference): air per cycle within ±3 %; peak torque, peak power and
  0–100 km/h within ±3 %.
- `IntakeGasDynamicsGuardTests` (every family bounded, 0 < VE_dyn ≤ 1.35,
λ within 4 % of target at full load on its calibration fuel; stage switching with 150 rpm hysteresis; the phaser still
moves the valve-event optimum ≥ 20° between 1,500 and 5,500 rpm; per-step allocation ≤ Phase 0 + 2 %), the fingerprint
(K20 protection: any change is a measured, documented re-baseline), `TuneRegenerationTests`, the source audit
(`EngineAgnosticTests`), the architecture invariants and the M54 bands (`M54ReferenceTests`). Step time (+10 %) is
measured with `carsim bench` against the numbers above in the same container. `TorqueCurveDiagnosisTests` pin today's
limitation and are expected to fail in Phase 1: they are retired or inverted there, deliberately and documented.

## Out of scope
CFD or wave-action solvers; exhaust wave tuning beyond the existing scavenging term (a candidate follow-up, together
with an exhaust-opening term for exhaust cam phasing); per-cylinder pulses; intake sound; new real engines; matching
any engine's dyno curve exactly.

## Phase 1 results (2026-09-28)
Full report: [intake-gas-dynamics-2/PHASE1_GATE_REPORT.md](intake-gas-dynamics-2/PHASE1_GATE_REPORT.md). In short:
- The locked model is implemented generically: every bank, any number of stages, NA and boost, no identity branch. The
  runner factor is removed; v₀ 13.64 m/s and the ceiling 0.973 are the only fitted constants (the K20 anchor).
- The five acceptance tests pass and are facts: crossover ratio 1.000; length exponent 0.609; +10.8 % for +9.4 %
  predicted; the stage effect under the phaser equal to parked; the M54's DISA crossover 3,924 rpm, switch 3,900.
- K20 anchor: air per cycle within 2.87 % (±3 %); peak torque +2.2 %, peak power −2.5 %, 0–100 km/h +0.4 %.
- M54 (held-out): a mid-range peak of 297.8 N·m at 3,100 rpm replaces the plateau; no second hump; 153.9 kW (−9.5 %).
  Classified B/D, nothing fitted.
- Acceptance criteria:
  1 (M54): met in direction, within bands, with the shape miss documented;
  2 (multiple architectures): §9 and the test matrix;
  3 (K20): a documented correction, re-measured;
  4 (tunes): all 12 regenerated, fixed points;
  5 (no engine-specific code): source audit green;
  6 (performance): −5 % / −5 % / +2.4 %, allocation halved;
  7 (tests, CLI, Godot, CI): green;
  8 (docs): updated.

