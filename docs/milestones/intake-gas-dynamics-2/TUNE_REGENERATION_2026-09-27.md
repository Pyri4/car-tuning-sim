# Tune regeneration under the existing physics — before/after (2026-09-27)

Generated for the Intake Gas Dynamics 2.0 design-resolution pass ([design resolution](../INTAKE_GAS_DYNAMICS_2_DESIGN_RESOLUTION.md), Q5).
**No simulation code changed.** Every value below was written by `carsim regenerate-tunes --write 1`, which ran
from the checked-in tunes of `2402f44`. The driver applied the two rules in docs/VERIFICATION.md: the hardware
schedule comes first, and only values that settle are written.

- A second, check-only run reports **12 of 12 tunes reproduced**, so every tune is now a fixed point of its recipe.
- Hand-authored tables are byte-identical, as are the λ targets, the K20 factory spark maps, the boost targets and
  every part, engine and other tune: each tune was parsed before and after and compared field by field.
- Cells in a rounding 2-cycle kept their checked-in value, so they are not listed as changes.

## Summary

| Tune | Table | Changed | Max \|Δ\| | 2-cycle cells kept |
|---|---|---|---|---|
| k20.stock | `volumetric_efficiency` | 11 of 180 | 0.001 | 0 |
| k20.turbo_base | `volumetric_efficiency` | 86 of 180 | 0.026 | 0 |
| m54.stock | `intake_cam_advance_deg` | 0 of 120 | — | 0 |
| m54.stock | `volumetric_efficiency` | 0 of 120 | — | 0 |
| m54.stock | `ignition_advance_deg` | 0 of 120 | — | 3 |
| syn.s16.stock | `volumetric_efficiency` | 0 of 120 | — | 0 |
| syn.s16.stock | `ignition_advance_deg` | 1 of 120 | 0.5° | 2 |
| syn.t3.stock | `valve_lift_switch_rpm` | 1 of 1 | 4800 → 4700 rpm | 0 |
| syn.t3.stock | `volumetric_efficiency` | 0 of 195 | — | 0 |
| syn.t3.stock | `ignition_advance_deg` | 1 of 195 | 0.5° | 8 |
| syn.t20.stock | `intake_cam_advance_deg` | 44 of 208 | 6.5° | 0 |
| syn.t20.stock | `volumetric_efficiency` | 57 of 208 | 0.004 | 0 |
| syn.t20.stock | `ignition_advance_deg` | 0 of 208 | — | 10 |
| syn.r6.stock | `intake_runner_switch_rpm` | 1 of 1 | 4600 → 6400 rpm | 0 |
| syn.r6.stock | `volumetric_efficiency` | 24 of 128 | 0.021 | 0 |
| syn.r6.stock | `ignition_advance_deg` | 1 of 128 | 0.5° | 1 |
| syn.b20.stock | `volumetric_efficiency` | 0 of 208 | — | 0 |
| syn.b20.stock | `ignition_advance_deg` | 5 of 208 | 1° | 7 |
| syn.v6.stock | `volumetric_efficiency` | 0 of 120 | — | 0 |
| syn.v6.stock | `ignition_advance_deg` | 0 of 120 | — | 2 |
| syn.v6tt.stock | `intake_cam_advance_deg` | 35 of 208 | 10° | 0 |
| syn.v6tt.stock | `volumetric_efficiency` | 22 of 208 | 0.012 | 0 |
| syn.v6tt.stock | `ignition_advance_deg` | 0 of 208 | — | 2 |
| syn.v8.stock | `volumetric_efficiency` | 0 of 104 | — | 0 |
| syn.v8.stock | `ignition_advance_deg` | 1 of 104 | 0.5° | 2 |
| syn.v8d.stock | `intake_cam_advance_deg` | 3 of 136 | 0.5° | 0 |
| syn.v8d.stock | `volumetric_efficiency` | 1 of 136 | 0.003 | 0 |
| syn.v8d.stock | `ignition_advance_deg` | 2 of 136 | 0.5° | 2 |

Why they changed (Phase 0 finding: no shipped tune was a fixed point of its recipe):

- **K20 stock VE:** 11 cells at the 0.001 rounding step.
- **K20 turbo base VE:** measured before later turbo and air-path fixes; regenerated on the T28 build under the
  current model (λ effect in the table below).
- **Synthetic turbo cam maps:** made by an earlier script under an earlier model; the cam calibrator now reproduces
  them.
- **`syn_i6_vis` runner switch:** 4,600 rpm was never the stage-held crossover; the current model's crossover is
  6,400 rpm. Its VE is re-measured on that schedule.
- **`syn_i3_turbo_vvl` lift switch:** 4,800 → 4,700 rpm, the crossover.
- **Spark maps:** cells where the knock or MBT limit moved across a 0.5° step once VE and the schedule are consistent.

## Effect on outputs (regression fingerprint, before → after)

From `tests/baselines/fingerprint.txt` before and after the re-baseline: every case and section, full precision.
A section is "unchanged" when its digest (every telemetry value) is bit-identical. The WOT columns use the
fingerprint's key points (every 500 rpm), so a peak between key points is not shown. Full-sweep peaks, from the dumps:
- **K20 stock (RON 95):** 110.97 kW / 189.20 N·m, unchanged at this precision.
- **K20 T28 (RON 98):** 177.51 → 177.39 kW, 292.51 → 292.49 N·m.
- **K20 T35:** 181.96 → 182.13 kW.
- **M54:** 152.96 kW / 303.94 N·m, bit-identical.

**Unchanged cases** (every section bit-identical): `m54_stock_98`, `m54_stock_95`, `m54_parked_98`, `fail_k20_overrev`, `fail_k20_detonation`, `fail_k20_oil_starvation`, `fail_k20_overheat`, `fail_m54_overrev`, `fail_m54_detonation`, `fail_m54_oil_starvation`, `fail_m54_overheat`, `scenario_isar_c30_six`, `drive_m54_stock`, `syn_h4_turbo`, `syn_v6_na`.

| Case | Sections that moved | WOT peak torque (N·m) | WOT peak power (kW) | Max \|Δ torque\| over WOT points | Max \|Δλ\| at WOT | Laps (s) | Other key numbers |
|---|---|---|---|---|---|---|---|
| `k20_stock_95` | wot, partload, cold, dyno_sweep, dyno_steady | 189.0 @ 4000 → 189.0 @ 4000 | 111.0 → 111.0 | 0.00062 % | 2.2e-05 | — | cold/k20.crankshaft.oem.fatigue 1.438e-12 → 1.449e-12; cold/coolant_c@40s 32.12 → 32.12; dyno_sweep/peak_power_kw 110.5 → 110.5 |
| `k20_stock_98` | wot, partload, dyno_sweep | 188.5 @ 4000 → 188.5 @ 4000 | 110.7 → 110.7 | 0.00062 % | 2.2e-05 | — | dyno_sweep/peak_power_kw 110.2 → 110.2; dyno_sweep/peak_torque_nm 188.7 → 188.7 |
| `k20_short_runner_95` | wot, partload, dyno_sweep | 185.8 @ 4000 → 185.8 @ 4000 | 114.0 → 114.0 | 9.5e-08 % | 1.4e-10 | — | dyno_sweep/peak_power_kw 113.4 → 113.4; dyno_sweep/peak_torque_nm 185.7 → 185.7 |
| `k20_na_built_98` | wot, partload, dyno_sweep | 182.4 @ 5500 → 182.4 @ 5500 | 122.6 → 122.6 | 0.00081 % | 3e-05 | — | dyno_sweep/peak_torque_nm 182.3 → 182.3; dyno_sweep/peak_power_kw 122.1 → 122.1 |
| `k20_t28_98` | wot, partload, cold, dyno_sweep, dyno_steady, abuse | 292.1 @ 4500 → 292.2 @ 4500 | 177.5 → 177.4 | 0.1 % | 0.0033 | — | cold/turbo.t28_ball.fatigue 3.98e-08 → 3.209e-08; abuse/turbo.t28_ball.fatigue 2.11e-05 → 1.922e-05; abuse/k20.head.oem.fatigue 0.08303 → 0.08186; … 12 more |
| `k20_t28_95` | wot, partload, dyno_sweep | 295.6 @ 4500 → 295.7 @ 4500 | 178.1 → 178.0 | 0.1 % | 0.0033 | — | dyno_sweep/peak_power_kw 177.5 → 177.4; dyno_sweep/peak_torque_nm 296.9 → 296.8 |
| `k20_t35_98` | wot, partload, dyno_sweep | 235.4 @ 7000 → 232.9 @ 7000 | 172.5 → 170.8 | 1.1 % | 0.01 | — | dyno_sweep/peak_torque_nm 223.8 → 221.2; dyno_sweep/peak_power_kw 172.8 → 170.9 |
| `k20_regen_stock_ve` | regen | — | — | — | — | — | regen/volumetric_efficiency.max_abs_difference 0.001 → 0; regen/volumetric_efficiency.differing_from_checked_in 11 → 0 |
| `fail_k20_turbo_lean` | hold | — | — | — | — | — | hold/turbo.t28_ball.fatigue 1.664e-07 → 1.664e-07; hold/k20.pistons.oem.wear 0.06343 → 0.06343; hold/k20.rod_bearings.oem.fatigue 0.15 → 0.15 |
| `scenario_project_car` | dyno_sweep, drive | — | — | — | — | 0 lap(s) [] → 1 [59.56] | drive/autopilot_recoveries 6 → 5; dyno_sweep/peak_power_kw 102.3 → 102.3 |
| `drive_k20_stock` | drive, wear | — | — | — | — | 2 lap(s) [51.83, 51.84] → 2 [51.83, 51.84] | wear/tires_front.wear 0.004492 → 0.004493; wear/brakes.wear 0.009266 → 0.009267; wear/clutch.wear 0.001755 → 0.001755 |
| `drive_k20_t28` | drive, wear | — | — | — | — | 2 lap(s) [50.37, 50.38] → 2 [50.37, 50.38] | wear/k20.pistons.forged_lc.fatigue 4.094e-05 → 4.048e-05; wear/turbo.t28_ball.fatigue 1.08e-05 → 1.072e-05; wear/clutch.wear 0.004496 → 0.00449; … 6 more |
| `syn_i4_sohc` | wot | 142.9 @ 3500 → 142.9 @ 3500 | 72.4 → 72.4 | 0.18 % | 1.5e-06 | — | — |
| `syn_i3_turbo_vvl` | wot, dyno_sweep | 175.5 @ 6000 → 175.5 @ 6000 | 110.3 → 110.3 | 0.011 % | 3.2e-05 | — | dyno_sweep/peak_torque_nm 175.7 → 175.7; dyno_sweep/peak_power_kw 116.1 → 116.1 |
| `syn_i4_turbo` | wot, partload, dyno_sweep | 332.8 @ 5000 → 332.8 @ 5000 | 203.9 → 203.9 | 0.25 % | 0.0017 | — | dyno_sweep/peak_power_kw 208.7 → 208.7; dyno_sweep/peak_torque_nm 339.3 → 339.3 |
| `syn_i6_vis` | wot, partload, dyno_sweep | 236.8 @ 4500 → 236.8 @ 4500 | 147.0 → 147.0 | 2.4 % | 0.00095 | — | dyno_sweep/peak_power_kw 151.9 → 151.9; dyno_sweep/peak_torque_nm 236.9 → 236.9 |
| `syn_v6_tt` | wot, partload, dyno_sweep | 532.6 @ 5000 → 532.6 @ 5000 | 319.5 → 320.0 | 0.17 % | 0.0034 | — | dyno_sweep/peak_power_kw 325.2 → 325; dyno_sweep/peak_torque_nm 526.5 → 526.5 |
| `syn_v8_ohv` | wot, partload, dyno_sweep | 348.7 @ 4000 → 348.7 @ 4000 | 187.4 → 187.4 | 0.0057 % | 7.8e-08 | — | dyno_sweep/peak_power_kw 192.1 → 192.1; dyno_sweep/peak_torque_nm 349 → 349 |
| `syn_v8_dohc_vvt` | wot, dyno_sweep | 425.4 @ 2500 → 425.4 @ 2500 | 254.7 → 254.7 | 0.0034 % | 8.6e-08 | — | dyno_sweep/peak_torque_nm 425.6 → 425.6; dyno_sweep/peak_power_kw 262 → 262 |
| `drive_syn_v6_tt_swap` | drive, wear | — | — | — | — | 1 lap(s) [50.95] → 1 [50.95] | wear/turbo.t25_small.fatigue 2.367e-05 → 2.445e-05; wear/turbo.t25_small.wear 0.005581 → 0.005566; wear/syn.v6tt.pistons.fatigue 3.064e-05 → 3.061e-05; … 5 more |

## Full-load fuelling against the λ target (from the full-precision dumps, WOT, every 250 rpm)
The purpose of a VE table is fuelling: the ECU's air estimate against the engine's. Mean and worst |λ/λ_target − 1|:

| Case | Before: mean / max | After: mean / max |
|---|---|---|
| k20_stock_95 | 0.11 % / 0.29 % | 0.11 % / 0.29 % |
| k20_t28_98 | 0.35 % / 1.11 % | 0.28 % / 1.39 % |
| k20_t28_95 | 0.60 % / 0.85 % | 0.52 % / 1.13 % |
| k20_t35_98 | 0.72 % / 1.95 % | 0.47 % / 0.73 % |
| syn_i6_vis | 0.35 % / 1.73 % | 0.29 % / 0.40 % |
| syn_i4_turbo | 0.53 % / 2.08 % | 0.52 % / 1.91 % |
| syn_v6_tt | 0.47 % / 1.18 % | 0.43 % / 1.14 % |

The mean error falls in every case. The T28 build's worst point rises by about 0.3 percentage points. The VE calibrator
measures at the table's axis points, and the sweep's 250 rpm points in between interpolate. Every value is within the
4 % guard (`IntakeGasDynamicsGuardTests`).

## Notes on individual cases
- **M54: every case is bit-identical.** The recipe reproduced its cam map and VE, and its 3 differing spark cells are a
  rounding 2-cycle, so `m54_stock.json` was not rewritten. The M54's Phase 1 starting point is unchanged.
- **`scenario_project_car`: the worn project car now completes a lap.** Before: 0 laps in 400 s, 6 autopilot
  recoveries. After: 1 lap in 59.56 s, 5 recoveries. The only change on that car is the K20 stock VE, 11 cells by 0.001
  (≤ 0.001 % torque, ≤ 2.2e-5 in λ).
  - The Phase 0 finding "never completes a lap" was therefore a knife-edge of the autopilot on a worn car, not a
    structural failure.
  - This fingerprint case will be noisy under any physics change; read its diffs with that in mind.
- **`syn_i6_vis`:** up to 2.4 % torque between 4,600 and 6,400 rpm, where the engine now stays on its long runner (the
  crossover). Its peak torque and power are unchanged.
- **`k20_t35_98`:** −1.1 % torque at the 7,000 rpm key point, and the dyno sweep's peak power falls from 172.8 to
  170.9 kW. The steady full-load sweep's own peak, at 7,250 rpm between key points, rises from 181.96 to 182.13 kW. The
  T35 runs the K20 turbo base tune, whose VE was regenerated on the T28 build, so its fuelling moves both ways (mean λ
  error 0.72 → 0.47 %).
- **Wear, fatigue and failure holds:** they move only where their fuelling moved (the turbo holds).

## Every changed cell

**k20.stock**, `volumetric_efficiency` (11):

<details><summary>cells</summary>

- 20 kPa / 4500 rpm: 0.773 → 0.774
- 30 kPa / 3500 rpm: 0.807 → 0.808
- 30 kPa / 4000 rpm: 0.823 → 0.824
- 30 kPa / 6000 rpm: 0.804 → 0.805
- 40 kPa / 3000 rpm: 0.814 → 0.815
- 40 kPa / 6500 rpm: 0.817 → 0.818
- 60 kPa / 3000 rpm: 0.854 → 0.855
- 80 kPa / 1500 rpm: 0.733 → 0.734
- 80 kPa / 2000 rpm: 0.793 → 0.794
- 80 kPa / 3500 rpm: 0.904 → 0.905
- 80 kPa / 5500 rpm: 0.907 → 0.908

</details>

**k20.turbo_base**, `volumetric_efficiency` (86):

<details><summary>cells</summary>

- 20 kPa / 800 rpm: 0.619 → 0.593
- 20 kPa / 5000 rpm: 0.746 → 0.747
- 20 kPa / 5500 rpm: 0.739 → 0.74
- 20 kPa / 6500 rpm: 0.715 → 0.716
- 20 kPa / 7000 rpm: 0.699 → 0.7
- 20 kPa / 7500 rpm: 0.699 → 0.7
- 20 kPa / 8000 rpm: 0.699 → 0.7
- 20 kPa / 8500 rpm: 0.699 → 0.7
- 20 kPa / 9000 rpm: 0.699 → 0.7
- 30 kPa / 800 rpm: 0.619 → 0.593
- 30 kPa / 4000 rpm: 0.804 → 0.805
- 30 kPa / 4500 rpm: 0.808 → 0.809
- 30 kPa / 5000 rpm: 0.805 → 0.806
- 30 kPa / 5500 rpm: 0.796 → 0.797
- 30 kPa / 6500 rpm: 0.768 → 0.769
- 30 kPa / 7000 rpm: 0.749 → 0.75
- 30 kPa / 7500 rpm: 0.749 → 0.75
- 30 kPa / 8000 rpm: 0.749 → 0.75
- 30 kPa / 8500 rpm: 0.749 → 0.75
- 30 kPa / 9000 rpm: 0.749 → 0.75
- 40 kPa / 800 rpm: 0.619 → 0.593
- 40 kPa / 3500 rpm: 0.823 → 0.824
- 40 kPa / 4500 rpm: 0.846 → 0.847
- 60 kPa / 800 rpm: 0.619 → 0.611
- 60 kPa / 4000 rpm: 0.881 → 0.882
- 60 kPa / 4500 rpm: 0.886 → 0.887
- 60 kPa / 5000 rpm: 0.883 → 0.884
- 60 kPa / 5500 rpm: 0.873 → 0.874
- 60 kPa / 6500 rpm: 0.84 → 0.838
- 60 kPa / 7000 rpm: 0.818 → 0.816
- 60 kPa / 7500 rpm: 0.818 → 0.816
- 60 kPa / 8000 rpm: 0.818 → 0.816
- 60 kPa / 8500 rpm: 0.818 → 0.816
- 60 kPa / 9000 rpm: 0.818 → 0.816
- 80 kPa / 1500 rpm: 0.73 → 0.731
- 80 kPa / 3000 rpm: 0.871 → 0.872
- 80 kPa / 4500 rpm: 0.914 → 0.913
- 80 kPa / 6000 rpm: 0.872 → 0.875
- 80 kPa / 6500 rpm: 0.852 → 0.855
- 80 kPa / 7000 rpm: 0.829 → 0.832
- 80 kPa / 7500 rpm: 0.829 → 0.832
- 80 kPa / 8000 rpm: 0.829 → 0.832
- 80 kPa / 8500 rpm: 0.829 → 0.832
- 80 kPa / 9000 rpm: 0.829 → 0.832
- 100 kPa / 5000 rpm: 0.923 → 0.924
- 100 kPa / 5500 rpm: 0.906 → 0.908
- 100 kPa / 6000 rpm: 0.882 → 0.892
- 100 kPa / 6500 rpm: 0.862 → 0.871
- 100 kPa / 7000 rpm: 0.839 → 0.849
- 100 kPa / 7500 rpm: 0.839 → 0.849
- 100 kPa / 8000 rpm: 0.839 → 0.849
- 100 kPa / 8500 rpm: 0.839 → 0.849
- 100 kPa / 9000 rpm: 0.839 → 0.849
- 120 kPa / 2500 rpm: 0.86 → 0.859
- 120 kPa / 5000 rpm: 0.939 → 0.938
- 120 kPa / 5500 rpm: 0.919 → 0.924
- 120 kPa / 6000 rpm: 0.892 → 0.907
- 120 kPa / 6500 rpm: 0.872 → 0.886
- 120 kPa / 7000 rpm: 0.85 → 0.862
- 120 kPa / 7500 rpm: 0.85 → 0.862
- 120 kPa / 8000 rpm: 0.85 → 0.862
- 120 kPa / 8500 rpm: 0.85 → 0.862
- 120 kPa / 9000 rpm: 0.85 → 0.862
- 150 kPa / 2500 rpm: 0.86 → 0.859
- 150 kPa / 5000 rpm: 0.948 → 0.951
- 150 kPa / 5500 rpm: 0.936 → 0.937
- 150 kPa / 6000 rpm: 0.908 → 0.918
- 150 kPa / 6500 rpm: 0.888 → 0.897
- 150 kPa / 7000 rpm: 0.865 → 0.872
- 150 kPa / 7500 rpm: 0.865 → 0.872
- 150 kPa / 8000 rpm: 0.865 → 0.872
- 150 kPa / 8500 rpm: 0.865 → 0.872
- 150 kPa / 9000 rpm: 0.865 → 0.872
- 200 kPa / 2500 rpm: 0.86 → 0.859
- 200 kPa / 4500 rpm: 0.968 → 0.967
- 200 kPa / 5000 rpm: 0.962 → 0.961
- 200 kPa / 6000 rpm: 0.931 → 0.929
- 200 kPa / 6500 rpm: 0.91 → 0.908
- 200 kPa / 7000 rpm: 0.885 → 0.883
- 200 kPa / 7500 rpm: 0.885 → 0.883
- 200 kPa / 8000 rpm: 0.885 → 0.883
- 200 kPa / 8500 rpm: 0.885 → 0.883
- 200 kPa / 9000 rpm: 0.885 → 0.883
- 250 kPa / 2500 rpm: 0.86 → 0.859
- 250 kPa / 4500 rpm: 0.968 → 0.967
- 250 kPa / 5000 rpm: 0.972 → 0.973

</details>

**syn.s16.stock**, `ignition_advance_deg` (1):

<details><summary>cells</summary>

- 100 kPa / 1500 rpm: 14.5 → 14

</details>

**syn.t3.stock**, `valve_lift_switch_rpm` (1):

<details><summary>cells</summary>

- 4800 → 4700 rpm

</details>

**syn.t3.stock**, `ignition_advance_deg` (1):

<details><summary>cells</summary>

- 100 kPa / 700 rpm: 2.5 → 3

</details>

**syn.t20.stock**, `intake_cam_advance_deg` (44):

<details><summary>cells</summary>

- 20 kPa / 6000 rpm: 10 → 11
- 30 kPa / 1000 rpm: 22 → 21.5
- 30 kPa / 1500 rpm: 19.5 → 19
- 30 kPa / 6000 rpm: 11 → 15
- 40 kPa / 3500 rpm: 20 → 20.5
- 40 kPa / 6000 rpm: 14 → 15
- 40 kPa / 6500 rpm: 14.5 → 14
- 40 kPa / 7000 rpm: 14.5 → 14
- 40 kPa / 7500 rpm: 14.5 → 14
- 40 kPa / 8000 rpm: 14.5 → 14
- 60 kPa / 1000 rpm: 25 → 24
- 60 kPa / 3500 rpm: 20.5 → 25
- 60 kPa / 4500 rpm: 16 → 16.5
- 80 kPa / 3500 rpm: 26 → 28
- 80 kPa / 4500 rpm: 18.5 → 19
- 100 kPa / 5500 rpm: 12 → 13.5
- 100 kPa / 6000 rpm: 12 → 11
- 100 kPa / 6500 rpm: 12 → 10.5
- 100 kPa / 7000 rpm: 12 → 10.5
- 100 kPa / 7500 rpm: 12 → 10.5
- 100 kPa / 8000 rpm: 12 → 10.5
- 110 kPa / 5500 rpm: 9 → 12.5
- 110 kPa / 6000 rpm: 8 → 7.5
- 110 kPa / 6500 rpm: 8.5 → 7
- 110 kPa / 7000 rpm: 8.5 → 7
- 110 kPa / 7500 rpm: 8.5 → 7
- 110 kPa / 8000 rpm: 8.5 → 7
- 140 kPa / 5500 rpm: 3.5 → 10
- 140 kPa / 6000 rpm: 2.5 → 3
- 140 kPa / 6500 rpm: 2.5 → 0
- 140 kPa / 7000 rpm: 2.5 → 0
- 140 kPa / 7500 rpm: 2.5 → 0
- 140 kPa / 8000 rpm: 2.5 → 0
- 170 kPa / 4500 rpm: 20 → 23
- 170 kPa / 6000 rpm: 3 → 5
- 200 kPa / 4000 rpm: 30 → 25
- 200 kPa / 4500 rpm: 20 → 25
- 200 kPa / 6000 rpm: 0 → 5
- 230 kPa / 4000 rpm: 30 → 25
- 230 kPa / 4500 rpm: 20 → 25
- 230 kPa / 6000 rpm: 0 → 5
- 260 kPa / 4000 rpm: 30 → 25
- 260 kPa / 4500 rpm: 20 → 25
- 260 kPa / 6000 rpm: 0 → 5

</details>

**syn.t20.stock**, `volumetric_efficiency` (57):

<details><summary>cells</summary>

- 20 kPa / 1000 rpm: 0.539 → 0.54
- 20 kPa / 6000 rpm: 0.737 → 0.736
- 30 kPa / 1000 rpm: 0.585 → 0.586
- 30 kPa / 1500 rpm: 0.643 → 0.644
- 30 kPa / 6000 rpm: 0.789 → 0.786
- 40 kPa / 1500 rpm: 0.692 → 0.693
- 40 kPa / 3500 rpm: 0.844 → 0.843
- 40 kPa / 6000 rpm: 0.818 → 0.817
- 40 kPa / 6500 rpm: 0.799 → 0.8
- 40 kPa / 7000 rpm: 0.772 → 0.774
- 40 kPa / 7500 rpm: 0.772 → 0.774
- 40 kPa / 8000 rpm: 0.772 → 0.774
- 60 kPa / 1000 rpm: 0.668 → 0.666
- 60 kPa / 3500 rpm: 0.895 → 0.893
- 60 kPa / 6000 rpm: 0.849 → 0.848
- 80 kPa / 3500 rpm: 0.934 → 0.933
- 80 kPa / 4500 rpm: 0.92 → 0.919
- 80 kPa / 7000 rpm: 0.826 → 0.827
- 80 kPa / 7500 rpm: 0.826 → 0.827
- 80 kPa / 8000 rpm: 0.826 → 0.827
- 100 kPa / 5500 rpm: 0.906 → 0.907
- 100 kPa / 7000 rpm: 0.847 → 0.85
- 100 kPa / 7500 rpm: 0.847 → 0.85
- 100 kPa / 8000 rpm: 0.847 → 0.85
- 110 kPa / 4500 rpm: 0.944 → 0.943
- 110 kPa / 5500 rpm: 0.912 → 0.914
- 110 kPa / 6000 rpm: 0.896 → 0.897
- 110 kPa / 7000 rpm: 0.847 → 0.85
- 110 kPa / 7500 rpm: 0.847 → 0.85
- 110 kPa / 8000 rpm: 0.847 → 0.85
- 140 kPa / 5500 rpm: 0.927 → 0.931
- 140 kPa / 7000 rpm: 0.847 → 0.85
- 140 kPa / 7500 rpm: 0.847 → 0.85
- 140 kPa / 8000 rpm: 0.847 → 0.85
- 170 kPa / 4500 rpm: 0.969 → 0.968
- 170 kPa / 5500 rpm: 0.935 → 0.937
- 170 kPa / 6000 rpm: 0.922 → 0.923
- 170 kPa / 7000 rpm: 0.847 → 0.85
- 170 kPa / 7500 rpm: 0.847 → 0.85
- 170 kPa / 8000 rpm: 0.847 → 0.85
- 200 kPa / 4500 rpm: 0.972 → 0.97
- 200 kPa / 5500 rpm: 0.938 → 0.94
- 200 kPa / 6000 rpm: 0.928 → 0.929
- 200 kPa / 7000 rpm: 0.847 → 0.85
- 200 kPa / 7500 rpm: 0.847 → 0.85
- 200 kPa / 8000 rpm: 0.847 → 0.85
- 230 kPa / 4500 rpm: 0.972 → 0.97
- 230 kPa / 5500 rpm: 0.942 → 0.943
- 230 kPa / 6000 rpm: 0.933 → 0.936
- 230 kPa / 7000 rpm: 0.847 → 0.85
- 230 kPa / 7500 rpm: 0.847 → 0.85
- 230 kPa / 8000 rpm: 0.847 → 0.85
- 260 kPa / 4500 rpm: 0.972 → 0.97
- 260 kPa / 6000 rpm: 0.939 → 0.942
- 260 kPa / 7000 rpm: 0.847 → 0.85
- 260 kPa / 7500 rpm: 0.847 → 0.85
- 260 kPa / 8000 rpm: 0.847 → 0.85

</details>

**syn.r6.stock**, `intake_runner_switch_rpm` (1):

<details><summary>cells</summary>

- 4600 → 6400 rpm

</details>

**syn.r6.stock**, `volumetric_efficiency` (24):

<details><summary>cells</summary>

- 10 kPa / 5000 rpm: 0.594 → 0.608
- 10 kPa / 5500 rpm: 0.602 → 0.611
- 10 kPa / 6000 rpm: 0.606 → 0.61
- 20 kPa / 5000 rpm: 0.73 → 0.747
- 20 kPa / 5500 rpm: 0.736 → 0.747
- 20 kPa / 6000 rpm: 0.738 → 0.742
- 30 kPa / 5000 rpm: 0.793 → 0.812
- 30 kPa / 5500 rpm: 0.799 → 0.811
- 30 kPa / 6000 rpm: 0.8 → 0.805
- 40 kPa / 5000 rpm: 0.836 → 0.855
- 40 kPa / 5500 rpm: 0.84 → 0.852
- 40 kPa / 6000 rpm: 0.839 → 0.844
- 60 kPa / 5000 rpm: 0.88 → 0.901
- 60 kPa / 5500 rpm: 0.885 → 0.897
- 60 kPa / 6000 rpm: 0.883 → 0.888
- 80 kPa / 5000 rpm: 0.906 → 0.926
- 80 kPa / 5500 rpm: 0.911 → 0.924
- 80 kPa / 6000 rpm: 0.909 → 0.914
- 100 kPa / 5000 rpm: 0.924 → 0.944
- 100 kPa / 5500 rpm: 0.927 → 0.941
- 100 kPa / 6000 rpm: 0.925 → 0.93
- 110 kPa / 5000 rpm: 0.924 → 0.944
- 110 kPa / 5500 rpm: 0.927 → 0.941
- 110 kPa / 6000 rpm: 0.925 → 0.93

</details>

**syn.r6.stock**, `ignition_advance_deg` (1):

<details><summary>cells</summary>

- 110 kPa / 2500 rpm: 19.5 → 20

</details>

**syn.b20.stock**, `ignition_advance_deg` (5):

<details><summary>cells</summary>

- 10 kPa / 700 rpm: 11 → 10
- 20 kPa / 700 rpm: 11 → 10
- 30 kPa / 700 rpm: 11 → 10
- 40 kPa / 700 rpm: 11 → 10
- 60 kPa / 700 rpm: 11 → 10

</details>

**syn.v6tt.stock**, `intake_cam_advance_deg` (35):

<details><summary>cells</summary>

- 40 kPa / 2000 rpm: 25 → 24.5
- 60 kPa / 5000 rpm: 18.5 → 15
- 60 kPa / 5500 rpm: 18.5 → 15
- 80 kPa / 4500 rpm: 20.5 → 21
- 80 kPa / 5000 rpm: 20 → 16
- 80 kPa / 5500 rpm: 19 → 15
- 100 kPa / 3000 rpm: 39 → 39.5
- 100 kPa / 3500 rpm: 33.5 → 34.5
- 100 kPa / 4000 rpm: 28.5 → 29
- 100 kPa / 4500 rpm: 23 → 23.5
- 100 kPa / 5000 rpm: 20 → 18
- 100 kPa / 5500 rpm: 17 → 15
- 110 kPa / 4000 rpm: 29.5 → 30
- 110 kPa / 4500 rpm: 24 → 25
- 110 kPa / 5000 rpm: 20 → 19
- 110 kPa / 5500 rpm: 16 → 15
- 110 kPa / 6000 rpm: 12 → 11.5
- 110 kPa / 6500 rpm: 6.5 → 6
- 110 kPa / 7000 rpm: 6.5 → 6
- 110 kPa / 7500 rpm: 6.5 → 6
- 110 kPa / 8000 rpm: 6.5 → 6
- 140 kPa / 6500 rpm: 5 → 4.5
- 140 kPa / 7000 rpm: 5 → 4.5
- 140 kPa / 7500 rpm: 5 → 4.5
- 140 kPa / 8000 rpm: 5 → 4.5
- 170 kPa / 6500 rpm: 0 → 2
- 170 kPa / 7000 rpm: 0 → 2
- 170 kPa / 7500 rpm: 0 → 2
- 170 kPa / 8000 rpm: 0 → 2
- 200 kPa / 6500 rpm: 0 → 5
- 200 kPa / 7000 rpm: 0 → 5
- 200 kPa / 7500 rpm: 0 → 5
- 200 kPa / 8000 rpm: 0 → 5
- 230 kPa / 6000 rpm: 10 → 0
- 260 kPa / 6000 rpm: 10 → 0

</details>

**syn.v6tt.stock**, `volumetric_efficiency` (22):

<details><summary>cells</summary>

- 30 kPa / 2000 rpm: 0.668 → 0.669
- 40 kPa / 2000 rpm: 0.707 → 0.708
- 40 kPa / 5000 rpm: 0.843 → 0.844
- 40 kPa / 5500 rpm: 0.833 → 0.834
- 60 kPa / 2000 rpm: 0.822 → 0.821
- 60 kPa / 5000 rpm: 0.873 → 0.881
- 60 kPa / 5500 rpm: 0.859 → 0.871
- 80 kPa / 5000 rpm: 0.897 → 0.904
- 80 kPa / 5500 rpm: 0.881 → 0.89
- 100 kPa / 3000 rpm: 0.975 → 0.974
- 100 kPa / 3500 rpm: 0.966 → 0.965
- 100 kPa / 5000 rpm: 0.919 → 0.921
- 100 kPa / 5500 rpm: 0.903 → 0.907
- 110 kPa / 5000 rpm: 0.928 → 0.929
- 110 kPa / 5500 rpm: 0.913 → 0.915
- 170 kPa / 6000 rpm: 0.922 → 0.92
- 200 kPa / 6000 rpm: 0.927 → 0.923
- 200 kPa / 6500 rpm: 0.91 → 0.911
- 230 kPa / 6000 rpm: 0.932 → 0.926
- 230 kPa / 6500 rpm: 0.914 → 0.915
- 260 kPa / 6000 rpm: 0.933 → 0.925
- 260 kPa / 6500 rpm: 0.914 → 0.915

</details>

**syn.v8.stock**, `ignition_advance_deg` (1):

<details><summary>cells</summary>

- 110 kPa / 2000 rpm: 19.5 → 20

</details>

**syn.v8d.stock**, `intake_cam_advance_deg` (3):

<details><summary>cells</summary>

- 30 kPa / 1500 rpm: 13.5 → 13
- 40 kPa / 1500 rpm: 43.5 → 43
- 80 kPa / 4500 rpm: 33.5 → 34

</details>

**syn.v8d.stock**, `volumetric_efficiency` (1):

<details><summary>cells</summary>

- 30 kPa / 1500 rpm: 0.584 → 0.587

</details>

**syn.v8d.stock**, `ignition_advance_deg` (2):

<details><summary>cells</summary>

- 80 kPa / 2500 rpm: 22.5 → 23
- 110 kPa / 2500 rpm: 21.5 → 21

</details>
