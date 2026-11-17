# 08. Reports and audit log

The agent produces two artifacts with different purposes:

| Artifact | For whom | Question | Format |
|---|---|---|---|
| **Report** | A human | "What happened and what did the agent do?" | Markdown |
| **Audit log** | A human + a machine (analysis, evals) | "Which decisions, on what basis, with what outcome?" | JSON Lines, append-only |

## Incident report

One report per agent run (`AgentRun`). File name:
`reports/YYYY-MM-DD/HHmmss-<trigger>-<short-slug>.md` (inside the cluster — `/data/reports/` on the agent's PVC).

Structure (modelled on a blameless postmortem):

```markdown
# [RESOLVED] OrdersApiHighErrorRate — orders-api

- Run: 2026-11-07 14:32:10Z, trigger: alertmanager webhook
- Ralph loop iterations: 2 of 5, outcome: completion check passed (error rate < 1% for 5 min)
- Duration: 6 min 12 s, LLM steps: 11, tool calls: 14, model: qwen/qwen3.5-9b (LM Studio)
- Action mode: approve

## Summary
One or two sentences: what broke, why (with confidence), what was done, the current state.

## Observations
- The alert and its labels
- Key metrics (before/during) — a table
- Pod state and events
- An excerpt from the logs (limited)

## Analysis
The hypothesis of the cause, which facts it is based on, confidence (low/medium/high),
alternative hypotheses that were discarded and why.

## Actions
| # | Action | Target | Guardrails | Mode | Result | Effect |
|---|---|---|---|---|---|---|
| 1 | restart | deployment/orders-api | passed | approved by human | ok | improved |

## Iterations
One block per Ralph loop iteration: the hypothesis, the action, the effect, what was written to RunState.

## Timeline
The chronology: alert → iteration 1 (observations → analysis → action → check) → iteration 2 → … → completion check.

## Recommendations
What the agent cannot or must not do by itself: fix limits, fix code, add an alert.
```

The deterministic sections (observations, actions, timeline) are assembled by code from `AgentRun`.
The model writes only "Summary", "Analysis" and "Recommendations" — so the report cannot "invent"
actions that did not happen.

### Report language and markup
The report headings follow `Agent:Language`: "Russian" — in Russian, any other value — in English (earlier
the headings were always in Russian, and the language setting changed only the model's text). The "Observations" block is
fenced with backticks one longer than the longest run of backticks inside the text, so a log line with three backticks
no longer breaks the layout. The report file name is never empty (for an alert name that is not Latin — `report`).

## Periodic health report

**Not implemented.** The idea: on a timer (for example, once a day): an SLO and error-budget summary, the top restarts, alerts over the period,
the agent's actions over the period and their effectiveness. Today the timer (`Agent:ScheduleMinutes`, off by default)
starts an ordinary run with `trigger: schedule`, not a summary.

## Audit log

One JSON record per event, in the file `audit/YYYY-MM-DD.jsonl` in the agent's data directory (inside the cluster — `/data/audit/`).

```json
{"ts":"2026-11-07T14:32:51Z","runId":"7f3c...","event":"action.proposed","action":{"type":"restart","target":"deployment/orders-api","ns":"sandbox"},"reason":"OOMKilled x3 in 10 min, memory growing linearly"}
{"ts":"2026-11-07T14:32:51Z","runId":"7f3c...","event":"guardrails.evaluated","checks":{"allowlist":"pass","policy":"pass","rateLimit":"pass"},"mode":"approve"}
{"ts":"2026-11-07T14:34:02Z","runId":"7f3c...","event":"action.approved","by":"human"}
{"ts":"2026-11-07T14:34:03Z","runId":"7f3c...","event":"action.executed","result":"ok"}
{"ts":"2026-11-07T14:36:03Z","runId":"7f3c...","event":"action.verified","effect":"improved","signals":{"errorRate":{"before":0.31,"after":0.004}}}
```

Event types: `run.started`, `iteration.started`, `observation.collected`, `llm.step`, `tool.called`,
`action.proposed`, `guardrails.evaluated`, `action.approved|rejected|expired`,
`action.executed`, `action.verified`, `iteration.finished`, `completion.checked`,
`run.escalated`, `run.finished`. Every record carries `iteration` — the Ralph loop iteration number.

Properties:
- **append-only**: records are never changed, only added;
- the `action.proposed` record is written **before** execution — if the agent crashes midway, the trace remains;
- secrets and full prompts are not written; full dialogues with the LLM are kept separately, in debug mode.

## Storage

- Local agent run: the `reports/` folder in the repository (in `.gitignore`, except examples).
- Inside the cluster: a PVC mounted into the agent's pod (`/data`: `reports/`, `audit/`, `state/`, `runs/`); plus logs in Loki.
- The agent's console (`/console`) shows the list of runs, the reports and the iteration traces; the run history lives in `/data/runs`
  (kept for `Agent:HistoryRetentionDays` days).

## Using it for evals

The audit log + the scenarios from [07-chaos-engineering.md](07-chaos-engineering.md) = data for assessing the agent:
for each scenario compare the cause found and the actions taken with the expected ones, and count steps/tokens/time.
This makes it possible to compare models (cloud vs local) and versions of prompts.
