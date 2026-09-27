---
name: car-sim-project-gate
description: Run the project gate that ends every car-tuning-sim milestone — re-verify the branches from scratch, review the open PRs, test the architecture against real engines, classify remaining debt (A/B/C/D), fix contradictions in the docs, recommend a merge order, and define (not start) the next milestone. Use when a milestone is complete, when asked for a gate, review, merge-order or readiness verdict, or before starting a new milestone.
---

# Project gate (car-tuning-sim)

Defined in AGENTS.md, "Choosing and running milestones". The gate decides whether the repository is genuinely ready
to move on — not whether the latest tests happen to pass. Make no feature changes during a gate; fix only clear
blockers (and doc contradictions, and missing tests for claims already made).

## Steps
1. **State of the repository**: `git status`, branches and their relationships (`git merge-base --is-ancestor`),
   commits since `main`, open PRs and their CI. Note stacked branches explicitly.
2. **Read the docs first**: GAME_VISION.md, AGENTS.md, README.md, GAME_DESIGN.md, ARCHITECTURE.md, ROADMAP.md,
   SIMULATION_SPEC.md, PARTS_DATABASE.md, ENGINE_AUTHORING_GUIDE.md, docs/ENGINE_ARCHITECTURE_AUDIT.md,
   docs/milestones/.
3. **Re-verify from scratch** (the `car-sim-verify` skill) on every branch under review — a clean build, tests,
   content validation, CLI, Godot smoke tests, CI — plus the regression-identity comparison where behaviour must not
   have moved. Do not trust earlier reports' numbers; say where a claim was wrong or untested.
4. **Review each PR**: the full diff, leftovers (prototype code, TODOs), identity branches
   (`git diff main... -- src | grep -E '\.Id ==|== "[a-z0-9_.]+"'`), stale docs, whether it met its stated purpose.
   Do not modify a PR just to make it easier to merge.
5. **Architecture review**: could a substantially different engine be added tomorrow as data? Test mentally against
   the synthetic matrix and real engines (e.g. RB26, 2JZ, LS3, B58, F20C, 4G63, EA888, K24, VR6, V10, V12,
   supercharged, direct-injection, dry-sump) — which are data today, which need a capability, which are out of scope.
6. **Debt scan** for hidden assumptions (ids, cylinder/bank/turbo/head counts, one air or exhaust path, DOHC,
   intake-only cams, port injection, wet sump, one ECU/fuel/cooling system, fixed arrays, single-engine-fitted
   constants). Classify each: **A** generic · **B** documented temporary limitation · **C** fix before the next
   milestone · **D** future work. Record findings in docs/ENGINE_ARCHITECTURE_AUDIT.md.
7. **Docs as project management**: the docs must not contradict each other or the code (grep for stale counts,
   "next task N" pointers, claims of features being rejected or missing). Fix contradictions.
8. **Merge order** by dependency, reviewability, clean history and revertability. For stacked branches: merge the base
   PR with a merge commit (or rebase-merge); if it is squashed, rebase the stacked branch
   (`git rebase --onto origin/main <old-base-tip> <branch>`) before its PR.
9. **Define the next milestone** in `docs/milestones/<NAME>.md`, marked *proposed — awaiting authorization*: goal,
   why it is next (long-term value, not ease), constraints, prerequisites, a test matrix with structural checks, and
   acceptance criteria including performance. Update ROADMAP.md "Next recommended tasks" to match.
10. **Report**: repository state, PR verdicts, verification actually run, strengths, weaknesses (A/B/C/D), doc
    consistency, merge order, the next milestone, why, what not to work on yet, acceptance criteria. Then stop — the
    owner authorizes the next milestone.
