# Verification tools

The tools that make a simulation change measurable, reproducible and reviewable. They live in the repository
(Intake Gas Dynamics 2.0, Phase 0; before it the milestone gates relied on probes outside the repo — audit finding G3).
None of them is used by the game.

| Tool | What it answers | Run |
|---|---|---|
| Regression fingerprint | Did any output of the K20, the M54 or a synthetic engine move, anywhere? | `dotnet test` (always); `carsim fingerprint` |
| Tune regeneration driver | Are the shipped tables what their recipes produce? Regenerate them after a physics change. | `carsim regenerate-tunes` |
| Mutation harness | Does each guarding test fail on the bug it guards? | `dotnet run --project tools/CarSim.MutationCheck -c Release` |
| Acceptance tests (pending) | The behaviour the next physics change must deliver, written before the code. | `CARSIM_RUN_PENDING_ACCEPTANCE=1 dotnet test --filter Milestone=IntakeGasDynamics2` |

Code: `tools/CarSim.Verification/` (fingerprint, driver; used by the CLI and the tests), `tools/CarSim.MutationCheck/`,
`tests/CarSim.Core.Tests/Verification/`, `tests/CarSim.Core.Tests/Acceptance/`.

## Regression fingerprint
**Matrix** (`FingerprintMatrix`, 35 cases; `carsim fingerprint --list 1`):
- K20: stock on RON 95 and 98, the short-runner intake, an NA build (race cams, springs, ported head, 4-2-1, 76 mm), the
  T28 turbo build on RON 98 and 95, the T35 build; the driver's regeneration of `k20.stock`.
- M54: stock on RON 98 and 95, and with its phaser parked (no cam table).
- Sections per case: `geometry`, `wot` (1,000 rpm to the rev limit + 500, 250 rpm steps, 1 s settle), `partload`
  (1,000 rpm steps × six throttles, one continuous session), `cold` (crank from ambient, idle on the radiator, a half-
  and a full-throttle blip, 40 s), `dyno_sweep` and `dyno_steady` (`DynoRunner` defaults), `abuse` (WOT 6,400 rpm on the
  radiator at 1.5 m/s air, 60 s or until a failure). Stock and T28 builds run all of them; the others the sweeps.
- Failure holds on both families (the damage tests' recipes): over-rev, detonation on RON 91, a turbo on the stock
  ECU (K20), oil starvation, overheating. Each ends with the failure reports, warnings and every part's wear, fatigue
  and inspection text.
- The game's scenarios (`project_car`, `isar_c30_six`: dyno pull and a lap) and autopilot laps: Kestrel S2 with the
  stock K20 and the T28 build, Isar C30 with the M54, the twin-turbo V6 swapped into the Isar C30.
- Every synthetic family on its tune's calibration fuel: geometry, WOT, part load, dyno pull.

**What is recorded.** Every sample is flattened to `path = value` for every public property and field of the telemetry,
recursively, doubles in round-trip form — equal text is equal bits. Per case and section the baseline stores a
SHA-256 over those values (**digest**), the sample and channel counts, and readable **key numbers** at full precision
(torque, power, VE, λ every 500 rpm, peaks, lap times, failure texts, wear). A **schema** lists the channels digested;
a channel added by later code is reported and left out of the digest, so adding telemetry does not break the baseline
while any change to an existing value does. Size ≈ 0.2 MB.

**Using it.**
- `dotnet test` runs every case against `tests/baselines/fingerprint.txt` (`Fingerprint*Tests`, split over classes to run
  in parallel; ≈ 80 CPU-s). A failure names the case, the section and the key numbers that moved.
- `carsim fingerprint` does the same from the command line (exit code 4 on a difference); `--case k20_` runs a subset;
  `--jobs 1` runs serially (results are identical — checked).
- Deep diagnosis: `carsim fingerprint --dump before/` on the base commit, `--dump after/` on the change, then
  `carsim fingerprint-diff before after` compares every value key by key and lists channels only one side has. This is
  how the engine-architecture milestone proved the K20 and M54 bit-identical (1.6 million values, then outside the repo).
- A deliberate change: `carsim fingerprint --write tests/baselines/fingerprint.txt` (always the whole matrix), and the
  PR reports the diff and why. The K20 and M54 must not move unless the change is a documented generic correction.
- Platform: the baseline assumes linux-x64 and .NET 8 (IEEE-754 doubles; `Math.Exp`/`Pow` from the OS libm, identical
  on Ubuntu 24.04 locally and in CI). A different libm could change last bits; the diff tool then shows where.

## Tune regeneration driver
`tools/CarSim.Verification/tune-manifest.json` holds one **recipe** per shipped and test tune (12; a test fails if a
tune has none): engine, build, fuel, and the calibrator steps in order, e.g. the M54's
`cams (2.5° step) → ve → spark → ve` on RON 98. Each step runs on a fresh engine with the tables the earlier steps left,
passed through the tune file's number format exactly as pasting `carsim calibrate-*` output does. Switch speeds of
variable-lift and variable-intake stages are a step too: the full-load torque crossover of the two stages held (100 rpm
grid, rounded to 100 rpm).

Two rules make a regenerated table legitimate (added by the 2026-09-27 design-resolution pass):
- **Hardware schedule first.** Cam phase and switch speeds come before the fuel and spark maps measured on them
  (`cams`/`runner_switch`/`lift_switch` → `ve` → `spark` → `ve`; `TuneRegenerationTests` checks every recipe). A switch
  speed moved after the fuel map leaves VE cells measured on the other stage between the old and the new speed.
- **Only values that settle are written.** Where a recipe changes anything, the driver runs it a second time on its own
  output. A cell that goes back to its checked-in value is a rounding 2-cycle (spark ↔ VE across a 0.5° step, two
  recipe-consistent states) and keeps the checked-in value. A cell that moves to a third value is written with the first
  pass's value and reported as unsettled. The report says how many cells of each table are in a 2-cycle.

**Hand-authored tables** (the K20's factory spark map and λ targets; λ targets everywhere; the turbo tunes' boost
targets) are listed under `hand_authored`. The driver never writes them. After a physics change it audits a
hand-authored spark map against what the spark calibrator would write on the tune's fuel and reports every cell above
that knock/MBT ceiling; a human decides (keep, lower by hand, or convert to calibrated), and the decision is recorded in
the tune's comment and the manifest.

**Using it.** `carsim regenerate-tunes` (all 12 in ≈ 2.5 min on 4 cores, ≈ 5.5 min when every tune needs its second pass; `--tune id,…`) reports, table by table, whether
the checked-in values are reproduced; `--write 1` rewrites only the calibrated tables in place, keeping the file's
layout and number style (a test proves every tune file round-trips byte-identically through the writer).

**Current state (2026-09-27, after the pre-physics regeneration): every tune is a fixed point of its recipe.**
`carsim regenerate-tunes` reports 12 of 12 reproduced. 39 spark cells in 10 tunes are rounding 2-cycles and keep their
values. The hand-authored spark audit is unchanged: 74 (K20 stock) and 80 (turbo base) cells above the ceiling.

The regeneration ran under the existing physics, with the two rules above, in its own commit, and re-baselined the
fingerprint. Every changed cell and its effect on outputs are listed in
[milestones/intake-gas-dynamics-2/TUNE_REGENERATION_2026-09-27.md](milestones/intake-gas-dynamics-2/TUNE_REGENERATION_2026-09-27.md).
The M54 is bit-identical, and the K20 stock moved by 11 VE cells of 0.001.

**Phase 0 state (the pre-change calibration baseline, before that regeneration).** No shipped tune was an exact fixed
point of its recipe: the tables were calibrated under earlier states of the model (the turbo tables before later physics
fixes; the synthetic maps by an earlier script):

| Tune | Result of its recipe against the checked-in tables |
|---|---|
| k20.stock | VE: 11 of 180 values differ by 0.001; hand spark map: 74 cells above the calibrator's ceiling on RON 95, mostly light-load cells (e.g. 20 kPa/3,000 rpm 40° against 28.5°) |
| k20.turbo_base | VE: 86 of 180 differ (max 0.026; longer holds do not reproduce it either); hand spark map: 80 cells above the ceiling on RON 98 |
| m54.stock | cams and VE reproduced exactly; spark: 3 of 120 values differ by 0.5° |
| syn.s16 / syn.v6 / syn.v8 | VE reproduced; spark: 2–3 values differ by 0.5° |
| syn.b20 | VE reproduced; spark: 12 of 208 differ (max 1°) |
| syn.t3 (VVL) | VE reproduced; spark: 9 differ by 0.5°; lift switch 4,700 against 4,800 rpm |
| syn.r6 (VIS) | VE reproduced; spark: 2 differ by 0.5°; runner switch ≈ 6,400 against 4,600 rpm (the model's crossover; see SIMULATION_SPEC.md, "Intake gas dynamics 2.0") |
| syn.t20, syn.v6tt (turbo, phased) | cams: 44 and 35 of 208 differ (max 6.5° and 10°); VE max 0.004 and 0.012; spark ≤ 0.5° |
| syn.v8d (phased) | cams 3, VE 1, spark 4 values differ (≤ 0.5°, 0.003) |

The driver itself is deterministic, and its output on the K20 factory recipe is pinned by the fingerprint case
`k20_regen_stock_ve`. Regenerating these tables is a content change with fingerprint consequences (the K20 moves by
11 VE cells), so Phase 0 did not do it; the design resolution did, separately from any physics (Q5).

The Phase 0 table overstated some differences, for two reasons:
- **Rounding 2-cycles.** Most of its 0.5° spark differences are rounding 2-cycles, which return to the checked-in value
  on a second pass.
- **Recipe order.** `syn.r6`'s VE looked reproduced only because the recipe measured it before moving the switch speed.

## Mutation harness
`tools/CarSim.MutationCheck/mutations.json` lists known bugs as exact text replacements and the tests that must fail
on each (21 entries: the validation pass's ECU and boost oracles, the choke-collapse and anti-windup bugs, a raised VE
floor, identity and cylinder-count branches, the dropped cam table, a phaser without effect, the engine-architecture
hacks — first bank's air or geometry for all, unshared shared elements, four hard-coded cylinders, interfaces from any
bank, one turbo state — and, for Intake Gas Dynamics 2.0, an inert runner, a runner that never switches, no switch
hysteresis, a 1e-10 relative change of one filling constant, and a change in the VE calibrator's rounding).

For each entry the harness checks the `find` text still occurs exactly once (a stale entry is an error), proves the
guarding tests pass unmutated, injects the mutant, rebuilds, requires at least one failure, and restores the file with
a fresh timestamp (MSBuild would otherwise keep the mutant's binaries); it rebuilds clean at the end, and restores on
Ctrl-C. `--only id,id`, `--list`, `--full` (the whole suite per mutant, counting failures). ≈ 20 minutes for all.
In CI it is a manual job (`workflow_dispatch`).

## Acceptance tests defined before the physics (pending)
`PendingAcceptanceFact` tests are the executable acceptance criteria of the next physics change. They are skipped unless
`CARSIM_RUN_PENDING_ACCEPTANCE=1` and each is recorded failing on today's model when written; the milestone turns them
into plain facts. Guards that must hold before and after are ordinary tests (`IntakeGasDynamicsGuardTests`). The list,
tolerances and today's failures are in docs/milestones/INTAKE_GAS_DYNAMICS_2.md.
