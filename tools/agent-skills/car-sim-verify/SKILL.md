---
name: car-sim-verify
description: Validate a change to the car-tuning-sim repository before committing, pushing or reporting it done — build, the full test suite, content validation with the synthetic engine matrix, CLI sweeps, the Godot headless smoke tests (real and synthetic engines) and CI. Use after any change to simulation, content, gameplay, UI, tools or CI in this repo, and whenever a report is about to claim that tests or checks pass.
---

# Verify a change (car-tuning-sim)

Validation is part of the work, never optional (AGENTS.md). Report the numbers you actually saw — test counts,
failures, script errors, CI conclusion — never "tests pass" from memory or from an earlier run.

## Model tiers (AGENTS.md, "Model routing")
Running the checks and reading clear results is **T1 (small model)** work: build, tests, CLI, Godot, CI status. Stop
and escalate instead of deciding when a result needs interpretation:
- a failing test whose cause is not obvious from its message → **T2**;
- any fingerprint difference, a surviving mutant, a physics or reference-band failure, or any idea of changing a
  tolerance, a test, a constant or the baseline → **T3** decides (fix, classify, or re-baseline as a documented generic
  correction).

A small model never re-baselines, skips, loosens or deletes a check to get green.

## When to use
- Before every commit or push that touches `src/`, `content/`, `game/`, `tools/`, `tests/` or `.github/`.
- Before writing a completion report, a PR description or a project-gate verdict.
- After a merge or rebase, before trusting the result.

## Steps
1. **Build everything** (the Godot C# project is part of the solution; warnings are errors):
   `dotnet build CarTuningSim.sln -c Release` — expect `0 Warning(s)`, `0 Error(s)`.
   After restoring files from a backup (mutation checks), use `--no-incremental` or `touch` the restored files:
   MSBuild skips recompiling sources older than the last build, so a mutant can survive in the binaries.
2. **Run the suite**: `dotnet test CarTuningSim.sln -c Release` — note the exact `Passed/Failed/Skipped/Total`. It includes
   the regression fingerprint (`Fingerprint*Tests`). Skipped tests are pending acceptance tests of a milestone
   (`PendingAcceptanceFact`); `CARSIM_RUN_PENDING_ACCEPTANCE=1` runs them.
3. **Validate content**, base and with the synthetic matrix:
   - `dotnet run --project tools/CarSim.Cli -c Release -- validate`
   - `dotnet run --project tools/CarSim.Cli -c Release -- validate --mods content/test`
4. **CLI smoke** (what CI runs): sweeps of `kestrel_k20`, `isar_m54 --fuel gasoline_98`, and with `--mods content/test`
   an `inspect syn_v8_ohv` and a `sweep syn_v6_tt --fuel gasoline_98`, and `check-engine` on `kestrel_k20 --strict 1`,
   `isar_m54` and `syn_v6_tt --mods content/test` (see `.github/workflows/ci.yml`). For content changes, run
   `check-engine` on every engine touched.
5. **Godot headless smoke tests** for engine, gameplay or UI changes (Godot 4.7.2 .NET; if `godot` is not installed,
   download the official build from the URL in `.github/workflows/ci.yml`, then `dotnet build game/CarTuningSim.csproj`
   and `godot --headless --path game --import` once):
   - `godot --headless --path game -- --smoke-test` and `-- --drive --smoke-test` (K20);
   - the same with `--scenario=isar_c30_six` (M54);
   - synthetic engines: `CARSIM_MODS_DIR=<repo>/content/test` with `--scenario=syn_v8_swap`, `syn_v6_tt_swap` (or any
     `<family>_swap` / `<family>_bench`).
   Pass = the `SMOKE TEST PASSED` / `DRIVE SMOKE TEST PASSED` line **and zero `ERROR` lines**: a script exception on
   every frame does not fail the smoke test by itself (a drive test hanging until its timeout is the symptom).
6. **Regression identity**: the fingerprint tests fail on any change to a K20, M54 or synthetic output and name the
   case, section and key numbers. For the full comparison: `carsim fingerprint --dump before/` in a worktree of the base
   commit, `--dump after/` on yours, `carsim fingerprint-diff before after`. If output moves on purpose, it is a
   documented generic correction: re-baseline with `carsim fingerprint --write tests/baselines/fingerprint.txt` and say
   what moved and why. Tunes: `carsim regenerate-tunes` checks them against their recipes. A new invariant gets a
   mutant in `tools/CarSim.MutationCheck/mutations.json` (`dotnet run --project tools/CarSim.MutationCheck -c Release --
   --only <id>`). See docs/VERIFICATION.md.
7. **CI** after pushing: read the run's jobs (core, CLI steps, Godot) and report the conclusion. A red run on a branch
   you own is work now: fix it or state exactly what fails and why.

## Pitfalls seen in this repo
- `! grep -q X file` never fails a `bash -e` step (negated commands are exempt from errexit): use
  `if grep -q X file; then exit 1; fi`.
- Never restore a mutated file with `git checkout` when the tree has uncommitted work — it discards that work too.
  Copy to a backup, mutate, restore from the backup, then `touch` it.
- The synthetic matrix is test content: load it with `--mods content/test` / `CARSIM_MODS_DIR`, never by the base game.
