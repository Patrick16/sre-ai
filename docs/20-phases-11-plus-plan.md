# 20. After Phase 10: what to do next

`docs/01-roadmap.md` covers the path "from zero to a working sandbox" (Phases 0–10). This document is
about what comes **after**. New blocks are not added to `01-roadmap.md`.

The source is an analysis of the project's gaps along two axes (SRE and the agent harness) and two rounds of criticism of the plan.
The first version had 20 "modules" with a `module.json` contract; it was rejected as infrastructure
for the sake of infrastructure. What remained is below.

> **Status:** blocks 1–7 of this plan are **not yet implemented** in the repository (at the time of the last update
> of this document there is no recorder, and no `Llm:StructuredOutput` or `tracing.enabled` flags in the code). Below is the plan, not a
> description of the current code. When a block is done, this status must be updated.

## Recommendations in order

1. **The exercises of Phases 1–9** (the unticked `[ ]` in `01-roadmap.md`: deleting a pod, OOMKilled, drift
   via `kubectl scale`, silence, `AuthorizationPolicy`, `pg_dump`…) can be done **before, after, or interleaved**
   with the blocks below — see the next section. They teach more than any new code.
   These are your exercises; you do them.
2. **Then — blocks 1–7 in order**, where an order is given (on the agent axis it is real, see below).
3. After 2–3 blocks are closed, **review the list**: reality will show what from the "mandatory" turned out to be unimportant.

## The one rule for new things

**Off by default, the smoke test green.** Everything new is switched on by an explicit flag or a separate
release/namespace; with it switched off, `./scripts/smoke-test.ps1` passes as after Phase 10. No module
framework (`module.json`, Enable/Disable/Reset) — it is not needed and not planned.
The project's earlier conventions remain: values in `values.schema.json` and a bump of `Chart.yaml`, network
rules on both sides and port 4143, Pod Security `restricted`, Helm owns the replicas and the pod
template, new registries — into the pull-through cache.

## Exercises of Phases 0–9 in any order

So that the exercises can be done on a project with blocks 1–7 already implemented, and in any sequence, four things are fixed:

1. **The default behaviour is as after Phase 10.** Everything that changes what is described in the phase docs
   is off by default: structured output (`Llm:StructuredOutput=false`), tracing
   (`tracing.enabled=false`), the agent recorder, the "noisy" alerts (a separate release, installed by hand),
   the `loadgen` load of ~5 rps, quotas and PDBs. You switch them on yourself, for the duration of a block; the smoke test is green
   with them too.
2. **Rollback with one command** — `scripts/reset-exercise.ps1`: `Apps` (the default, ~2–3 min, data is kept),
   `Platform` (plus the platform layer), `Cluster -Yes` (from scratch, ~9 min, data is lost). It starts the stopped nodes, removes the leftovers of the exercises (the kill switch, the Chaos Mesh
   experiments, `AuthorizationPolicy`), repairs the mesh, restores the releases from the values files (including
   after a drift via `kubectl scale`), and runs the smoke test. It does **not** roll back your edits in git,
   only warns about them.
3. **In each phase doc (09–18), before the exercises** — a "Preconditions and rollback" block with the command
   suited to exactly this phase.
4. **The tag `phase-10-baseline`** — the exact state of the code and the docs before the plan is implemented:
   `git switch --detach phase-10-baseline` (to return — `git switch master`).
   If the figures in a phase doc do not match what you see, check against this state.

What the rollback does not restore: the Postgres data (you need `Cluster`), the Alertmanager silences (end them in the UI),
the files in `.local/` (the baseline and evals results live on the host and survive `cluster-down`, while the agent's reports on its PVC do not). The only thing that stays "spoiled knowledge": if you have already done
the backup drill (block 6), item 6 from [13](13-phase-4-storage.md) will go without a surprise — to get the surprise, do the exercise before block 6.

## Order and time

"By hand" — how much of your time; "Calendar" — how much time actually passes (9B runs occupy the machine).

| # | Block | By hand | Calendar | Order |
|---|---|---|---|---|
| 1 | Recorder + baseline | 1 evening | + a night | first on the agent axis |
| 2 | Replay | ½ evening | — | after 1 |
| 3 | Judge | 2 evenings | — | after 1 |
| 4 | Structured output | 1 evening | + a night (a repeat baseline) | **only after 1** |
| 5 | SRE processes | 1 evening | + a day of watching the alerts | free |
| 6 | Backup drill + tracing | 1–2 evenings | — | free |
| 7 | Capacity | 1 evening | — | free, can be cut |

In total ≈ 8 evenings by hand + nights of machine time. Blocks 5–7 do not depend on the agent axis nor on each other; blocks 1–4 form a chain, because without a baseline there is nothing to measure the agent's changes with.
Allow a reserve in each block.

---

## Block 1. Recorder + baseline

**The first line is code.** You need a switchable recorder over `GuardedTool` (≈1 hour): it writes JSONL
`(run, iteration, tool, args, result)` into `.local/`. Without it the first baseline cannot be replayed, and repeating 3×5 runs on 9B is expensive.
First the recorder, then the runs.

**Baseline:** ≥3 runs for each scenario from `infra/chaos/scenarios.json` (5 scenarios),
under identical conditions (the model, `-QuietMinutes`, the image version). The 9B model is non-deterministic (in
[18](18-phase-9-chaos-engineering.md): 0/3 → 2/3 on one scenario), so the result is a **distribution** (the median and the spread), not a point.
What we keep: the score, tokens, wall time, which tools and how many times they were called. Without the cost, "improved" may
mean "more accurate, but three times more expensive".

**The version of the harness** for comparison = the git sha + the hash of the prompt + the hash of the tool descriptions.
**Done when:** there is `.local/evals/<version>/` with a distribution for each scenario.
**If it runs into** the context of LM Studio or the machine's memory — that is a result; write it into `19` ("what I would do differently").

## Block 2. Replay

A player takes the recorded tool responses and runs the new harness without the cluster and without
changing the input data; the reports are compared.

**The purpose is only parsing, the prompt, and the guardrails.** It removes the variability of the cluster, but not of the model:
the 9B model will still answer differently on the same responses, so even with replay N runs are needed.
Replay is not suitable for choosing tools: the model may call a tool that is not in the recording.

## Block 3. Judge

- The rubric is 3–5 **boolean** fields in JSON, not free text (for example: named the cause; the action is justified or there was none; the confidence is reasonable).
- **First the judge is you.** Label 10–15 reports by hand against the rubric. This is both the calibration and a check of
  the rubric itself: if the labelling takes more than an hour, or you cannot answer unambiguously — the rubric is bad, not the reports. Analysing the disagreements is a separate half of the work.
- An automatic judge (another, stronger model, not the 9B that writes the reports: otherwise it agrees with any text) — only if the manual labelling becomes the bottleneck, for example when
  comparing 4 versions of the harness. Then check it against your labelling; agreement < 80% — the rubric is bad.
- The game-day regexes stay as a cheap zero layer. Compute confidence intervals, not a single number.

## Block 4. Structured output

`response_format: json_schema` for Analyze (the schema from the C# type). First check that LM Studio with the chosen model really
observes the schema; if not — record the result and leave the flag `Llm:StructuredOutput=false`. Run the same baseline and compare. If `Parsed=false` has disappeared —
a win; if not — the parsing was not the problem. Keep the defensive check in the code.

## Block 5. SRE processes

One evening, without infrastructure:
- **An error budget policy** — one page in `docs/policies/`: burn-rate thresholds from your real
  alerts → actions (freezing non-critical changes, the priority of reliability, who decides, how we get out). You write the policy; I can prepare a draft with your numbers.
- **A blameless postmortem** of one chaos scenario by hand, **without looking at the agent's report** (the chronology, the
  impact, the trigger / cause / conditions, the action items). Then compare it with the agent's report: what it sees and what it does not see.
- **Alert hygiene:** add 3–4 noisy alerts ("CPU > 80%", "p99 > 1 s" without an SLO), watch them for a day, justify which to remove, move to a warning, or keep. The agent's de-duplication
  (`AlertSuppressionMinutes`) is a separate layer, and tuning does not replace it.
- **A toil reflection (30 minutes):** for each script — how often you run it and how long it would take by hand. Some of them will turn out to be unnecessary.

## Block 6. Backup drill + tracing

- **Backup drill:** `pg_dump` → delete the PVC → restore → measure the **RTO** (the time until the service works again) and the **RPO** (the lost records). Destructive: make a fresh dump in advance,
  requires a confirmation. The agent's rights are not extended.
- **Tracing:** Tempo + OpenTelemetry in the services and in the agent (one backend, two sources).
  Switched on by a flag (`tracing.enabled`, off by default; the NetworkPolicy rule to Tempo only
  when enabled). The agent: a run = a trace, an iteration = a span, a tool/LLM call = a span (attributes: the iteration number, the tool, the size of the result, tokens, latency). Done when, on the scenario "database latency",
  the specific slow span is visible.

## Block 7. Capacity (can be cut)

`loadgen` takes the rps from the values; a stepped load 5 → 200 rps, to find the "knee" where p99
goes beyond the SLO. The calculation of replicas and cost in the cloud **on paper, on top of the measured**, not instead of it.
kind shares the host's CPU: the knee will also show the limits of your machine — this is a conclusion too.

---

## Optional exercises (each ≤ 1 evening)

- **Canary** through a Linkerd traffic split (the weights of an `HTTPRoute`), manual: 5% → watch for 10 minutes. Without
  Argo Rollouts; this is an exercise for one step, not an automatic gate.
- **Resource governance:** a Quota + LimitRange + PDB + `kubectl drain` of a node. Remove the quota in `sandbox`
  after the exercise.
- **Memory between runs:** SQLite + FTS5 over the past reports, a `find_similar_incidents` tool.
  Deliberately last: the corpus is tiny, the value is deferred. The result is a hint, not the truth.
- **MCP** — only if you need to reuse the tools outside the project.

## Deliberately not done

A module framework; parallel tool calls as a phase; MCP in the main routes; separate phases for cost, tool selection, alert hygiene and toil (these are metrics and exercises inside the blocks); Argo Rollouts;
a vector store for memory; on-call and paging; managed cloud; more services, types of chaos and layers of
guardrails. We do not touch what works: failure handling, guardrails in depth,
`Llm:AllowedModels`.
