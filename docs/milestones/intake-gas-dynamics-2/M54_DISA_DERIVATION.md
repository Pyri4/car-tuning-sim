# M54 DISA — the frozen A-D1 derivation (Intake Gas Dynamics 2.0, Phase 1 step 5)

**Frozen 2026-09-27T20:14:30Z, before any M54 torque output of the Phase 1 model was inspected.** This record is committed on its own,
ahead of the content change that authors it, so the order is visible in the history. The procedure is the locked one of
[the Phase 1 proposal](../INTAKE_GAS_DYNAMICS_2_PHASE1_PROPOSAL.md), section 4. Nothing below was run on the engine: it is
arithmetic on the model's gain functions (`RunnerStageDerivation`, `carsim derive-stage`) with the pre-registered
parameters K = 2.1, ζ = 0.35, the runner gas at 298.15 K.

What had been computed with the Phase 1 model before this derivation, and what was looked at:
- the M54's open-stage tuned speed from its geometry (`carsim inspect isar_m54`: 4,209 rpm), which is arithmetic;
- the fingerprint dump of the physics-only state and the full test suite, whose M54 values and failure messages exist in
  their logs but were **not inspected**;
- the tune driver's log of the M54's regenerated tables (cam, VE and spark cell counts; no torque).

## Inputs
| Input | Value | Class |
|---|---|---|
| Open stage (flap open): acoustic length | 380 mm | estimated (no source; PARTS_DATABASE.md) |
| Runner diameter, both stages | 33.6 mm = 0.40 × 84 mm bore | estimated (assumption A5 default, flagged by the validator) |
| Cylinder volume at mid-stroke V_eff | 302.2 cc (V_c + V_d/2; bore 84, stroke 89.6, CR 10.2) | derived |
| Switch speed (the stages' crossover under A-D1) | 3,925 rpm, the centre of the sourced band 3,750–4,100 rpm | sourced (secondary) + assumption A-D1 |

## Result (`carsim derive-stage isar_m54 --switch 3925`)
```
Isar M54 3.0L DOHC I6: upper stage 380.0 mm × Ø33.6 mm (default 0.40 × bore), switch 3925 rpm, K 2.1, ζ 0.35, runner gas 298.15 K
  upper stage tuned        4208.5 rpm
  lower stage tuned        3676.2 rpm
  lower stage length       469.09 mm
  gain crossover (check)   3925.0 rpm

Sensitivity (each input moved alone):
  switch 3,750 rpm (band low) upper   4209 rpm  lower   3379 rpm  length 532.5 mm
  switch 4,100 rpm (band high) upper   4209 rpm  lower   3997 rpm  length 412.3 mm
  K 2.0                      upper   4419 rpm  lower   3528 rpm  length 537.1 mm
  K 2.2                      upper   4017 rpm  lower   3837 rpm  length 408.6 mm
  ζ 0.33                     upper   4209 rpm  lower   3676 rpm  length 469.1 mm
  ζ 0.45                     upper   4209 rpm  lower   3675 rpm  length 469.2 mm
  upper 300 mm               upper   4865 rpm  lower   3281 rpm  length 556.1 mm
  upper 450 mm               excluded: the upper stage then tunes below the switch speed
  diameter 30 mm             excluded: the upper stage then tunes below the switch speed
  diameter 40 mm             upper   4697 rpm  lower   3365 rpm  length 611.1 mm
```

**Frozen values:**
- **closed stage (flap closed, stage 0 below the switch): effective acoustic length 469.1 mm** (derived; 0.1 mm precision,
  which moves the crossover by about 1 rpm), diameter as the open stage;
- **open stage (flap open, stage 1 above the switch): 380 mm** (the runner estimate, unchanged).

These match the proposal's preview (N_o 4,208 rpm, N_c 3,677 rpm, 469 mm; the 0.4 rpm difference in N_o is the gas
constant, 287.05 J/(kg·K) in the model against the 287 used for the preview). The two exclusions hold: a 450 mm open stage or a
30 mm runner would tune the open stage below the sourced switch band, so the sourced fact rules those estimates out.

After this commit no M54 value changes because of the M54's output. Its tune's switch speed comes from the tune driver
(`runner_switch`: the stage-held full-load torque crossover), and acceptance test 5 checks the relationships and
reports the curve's shape as held-out validation.
