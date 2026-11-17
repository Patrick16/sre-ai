# 05. Agent architecture: Ralph loop + RALF

The most interesting part of the project. This document describes **how to think** about the agent's design,
not only what to write.

> The implementation (Phase 7) — [16-phase-7-agent-mvp.md](16-phase-7-agent-mvp.md): Microsoft Agent Framework instead of a "bare"
> HttpClient (section "LLM client" below), the completion check — "steady state of the namespace", one run per namespace.

## What an agent harness is

An LLM by itself can do only one thing: take text and return text. Everything else is the **harness** (the scaffolding), which we write:

- decides **when** to start the agent (triggers);
- collects the **context** and packs it into the prompt;
- gives the model **tools** (functions it can "call") and actually executes them;
- runs the **loop** "model → tool call → result → model…" and decides when to stop;
- **constrains** the model: what it may do, how many steps, how much money/tokens;
- **records** everything that happened and forms the result.

A good harness makes even a weak/local model behave predictably, and a strong one unable to do harm.
The quality of the agent = the quality of the model × the quality of the harness.

## Two loops: the Ralph loop outside, RALF inside

The agent is built as **two nested loops**, each with its own task:

| Loop | Question it answers | Who decides what to do next |
|---|---|---|
| **Ralph loop** (outer) | "Is the task solved? If not — try again" | Deterministic code (the completion check) |
| **RALF** (one iteration) | "What is happening now and what is the next step?" | The LLM proposes, the guardrails decide |

### Ralph loop — "keep going until solved"

The Ralph loop is a known technique from the world of coding agents: the same **task** is given to the agent
again and again until a completion criterion is met. Three key ideas we take:

1. **Every iteration starts with a fresh LLM context.** We don't continue an endless dialogue,
   we start over. Why:
   - the context does not swell (critical for local models with a small window);
   - the model does not "get stuck" on its earlier wrong hypothesis;
   - the system has changed during the iteration — it is better to look at it anew than to trust old observations.
2. **Memory lives outside the model — in a progress file (`RunState`).** Between iterations only a
   compressed summary is kept: which hypotheses were checked and discarded, which actions were taken and with what effect,
   what remained unclear. The next iteration reads it first. This way the agent does not repeat what did not help.
3. **"Done" is decided by code, not by the model.** LLMs tend to declare success too early. The completion criterion is
   a deterministic check: "the alert is resolved **and** the SLI has been within bounds for N minutes in a row". The model may
   *think* everything is fixed, but the loop stops only when the metrics confirm it.

For an SRE agent the "task" sounds like: **"return the system to steady state"**. A trigger (an alert or a scheduled check)
formulates it, and it does not change between iterations.

### RALF — what happens inside one iteration

```
 Trigger (Alertmanager webhook | timer | manual)
    │
    ▼
 TaskSpec: goal + completion criterion + limits          RunState (progress file)
    │                                                   ▲            │
    ▼                                                   │            │
 ┌─────────────── Ralph loop (iteration i) ─────────────┼────────────┼───┐
 │  fresh LLM context = TaskSpec + RunState ◄───────────┼────────────┘   │
 │                                                      │                │
 │  R — Retrieve  deterministically collect: the alert, pods, events,    │
 │                key metrics, fresh errors in the logs                   │
 │  A — Analyze   the LLM reasons, calling read-only tools when needed   │
 │                (internal ReAct, ≤ MaxSteps) → findings,               │
 │                a hypothesis of the cause, (optionally) ActionProposal │
 │  L — Lead      ActionProposal → Guardrails → dry-run | approve | auto │
 │  F — Feedback  wait T, check the effect (improved / unchanged /       │
 │                degraded), write the audit, update RunState ───────────┘
 └───────────────────────────────┬──────────────────────────────────────┘
                                 ▼
                    Completion check (code, not the LLM)
          ┌──────────────┬───────┴────────┬──────────────────────┐
          ▼              ▼                ▼                      ▼
     steady state    not solved,      no progress /         limits exhausted
     → report, stop  limits remain    degraded              → escalation, report
                     → backoff,       → escalation,
                       iteration i+1    report
```

### Why the loop is split exactly this way

- **Retrieve is separate from Analyze.** The base context is collected by deterministic code,
  not by the LLM. This is cheaper, faster and more reliable: the model immediately sees the essentials and does not spend steps
  on obvious queries. The LLM fetches details through tools only when needed.
- **Lead is separate from Analyze.** The model only **proposes** an action as structured data. The decision
  "execute or not" is made by deterministic code (the guardrails). The model physically has no tool "run an arbitrary command".
- **Feedback closes the iteration.** Without a check of the effect, the agent is a script that "did it and forgot". The
  before/after comparison is recorded in RunState and becomes the input of the next iteration.
- **The Ralph loop closes the task.** One iteration may fail: the first action helped partially, or the hypothesis was wrong.
  The outer loop gives the agent another attempt — now taking into account what did not work.
  It is the same idea as the **reconcile loop** in Kubernetes controllers: again and again compare "want" with "have".

### Dangers of the Ralph loop and protection from them

A "keep going until solved" loop without limits is a way to restart pods endlessly.
So the outer loop has strict bounds:

| Risk | Protection |
|---|---|
| An endless loop | `MaxIterations` (for example, 10) and a wall-time limit |
| Repeating the same action | RunState records the actions taken; repeating the same action with effect `unchanged` is forbidden by policy |
| The agent makes things worse | Effect `degraded` → immediate stop and escalation, no new actions |
| No progress | Two iterations in a row without new facts and without improvement → escalation |
| The system has not had time to react | Backoff between iterations (Kubernetes needs time for a rollout, metrics need the `rate()` window) |
| A storm of actions | A total limit of actions per run and per window (see [06](06-safe-actions-and-guardrails.md)) |

### Example: how it looks in practice

The alert `OrdersApiHighErrorRate`. Goal: the error rate < 1% for 5 minutes.

1. **Iteration 1.** Retrieve: 2 of 3 pods are `OOMKilled`. Analyze: hypothesis — a memory leak.
   Lead: `restart deployment/orders-api` (auto). Feedback: the error rate fell from 31% to 4% — `improved`, but the goal is not reached.
   RunState: "the restart helped partially; memory is growing again".
2. **Iteration 2** (fresh context + RunState). Retrieve: memory grows linearly, restarts continue.
   Analyze: the restart treats the symptom; the logs after the last rollout show a new image version.
   Lead: there is no rollback in the agent's set of actions (the release is rolled back by Helm, not by the agent) → escalation to a human with the report.
3. **Completion check:** the error rate is out of bounds, so the loop stopped with an escalation, not at steady state.
   The report describes both iterations and recommends a rollback to the human.

### How this maps to code

```
RalphLoop.RunAsync(TaskSpec task)
  state = RunState.Load(task.Id)
  for i in 1..task.MaxIterations:
      iteration = new RalfIteration(freshLlmContext(task, state))
      result = await iteration.RunAsync()      // R → A → L → F
      state.Append(result)                     // saved to disk after every iteration
      if completionCheck.IsDone(task) → return Finished(state)
      if result.Effect == Degraded or state.NoProgress → return Escalate(state)
      await backoff(i)
  return Escalate(state, "limits exhausted")
```

RunState is saved to disk after every iteration. But a run is **not resumed** after a crash or restart:
the agent does not continue from the middle. RunState files (`/data/state`) are kept for analysing a run;
old ones are deleted after `Agent:StateRetentionDays`.

## Components (the `SreAgent.Core` project)

| Component | Responsibility |
|---|---|
| `TriggerEvent` | The start of a run: an Alertmanager webhook, a timer, a manual `POST /runs` |
| `TaskSpec` | The task for the Ralph loop: goal, completion criterion, limits (`AgentLimits`: `MaxIterations`, backoff) |
| `RalphLoop` | The outer loop: a fresh context per iteration, the completion check, stop/escalation |
| `RunState` / `IRunStateStore` / `FileRunStateStore` | The progress file between iterations: checked hypotheses, actions and their effect, open questions |
| `ICompletionCheck` (implementation `AlertsResolvedCompletionCheck`) | A deterministic "task solved" check by alerts, SLIs, pod readiness |
| `IObservationCollector` | Deterministic collection of the base context (Retrieve) |
| `Prompts` / `AnalysisParser` | Building the prompt and parsing the model's answer |
| `IChatClientFactory` | Creates an LLM client for a provider and model (`Microsoft.Extensions.AI`) |
| `IAgentToolset` / `GuardedTool` / `ToolBudget` | The set of read-only tools; a wrapper with a call budget, output truncation and audit |
| `RalfIteration` | One iteration R → A → L → F; the inner loop with tools is bounded by a step budget |
| `ActionLead` / `Guardrails` / `ActionCatalog` | Action proposal → ordered checks → mode (see [06](06-safe-actions-and-guardrails.md)) |
| `IActionExecutor` / `IActionLedger` | Executing an approved action; the action ledger across runs |
| `EffectJudge` | Assessing the effect of an action (Feedback): improved / unchanged / degraded |
| `RunSynthesizer` | The final report synthesis across all iterations and the "established facts" |
| `IAuditLog` / `IRunObserver` | The audit log and observation of a run's progress for the console (see [08](08-reporting-and-audit.md)) |
| `AgentRun` | The full history of a run (all iterations) — the source for the report; RunState is its compressed summary for the LLM |

The key principle: `Core` does not depend on Kubernetes or a concrete LLM. Loop tests
are written with fake tools and a "scripted" LLM that returns predefined answers.

## Tools

A tool for the LLM is a function with a description and a JSON schema of parameters. The model returns
"I want to call `get_pod_logs` with `{pod: "orders-api-xyz", tail: 100}`", the harness executes it and
returns the result in the next message.

**Read-only tools (phase 7):**

| Tool | What it does |
|---|---|
| `list_pods` | Pods of a namespace with status, restarts, age |
| `describe_workload` | Deployment/StatefulSet: replicas, image, conditions, rollout history |
| `get_events` | Kubernetes events for an object/namespace over a period |
| `query_metrics` | PromQL instant/range (with a limit on the result size) |
| `query_logs` | LogQL with a line limit, by default only errors |
| `get_active_alerts` | Current alerts from Alertmanager |

**Actions (phase 8)** — the model has no tools for actions. It proposes at most one action with an `action` field
in its JSON answer (`ActionProposal`); execution happens only after the guardrails. There are two actions:
`restart_deployment` and `delete_pod` (see [17](17-phase-8-actions-guardrails.md)). Scaling and rollback are not among them:
replicas and the pod template belong to Helm.

**Rules for good tools:**
- narrow and clear models (better `query_logs(app, level, since)` than "run LogQL");
- the result is **compressed** and bounded in size — otherwise a local model drowns;
- errors are returned to the model as text ("pod not found") instead of breaking the loop;
- every tool has a timeout.

## LLM client: an OpenAI-compatible API and LM Studio

LM Studio runs a local server with the same protocol as OpenAI
(`POST /v1/chat/completions`), so **one** client with configurable
`BaseUrl`, `ApiKey` and `Model` is enough:

```yaml
# infra/helm/releases/sre-agent/sre-agent.yaml (configEnv → Llm__* variables)
Llm__BaseUrl: "http://host.docker.internal:1234/v1"   # LM Studio on the host
Llm__Model: "qwen/qwen3.5-9b"
Llm__AllowedModels__0: "qwen/qwen3.5-9b"              # what a run may choose
# an external provider: Llm__Providers__<name>__BaseUrl + a key (typed into the console or from a Secret)
```

Providers are configuration, not request data: a request selects a provider by name and cannot supply a URL.
Keys are write-only (never returned or logged). External providers are off by default.

Implementation options: the official `OpenAI` package for .NET or `Microsoft.Extensions.AI`
(the common `IChatClient` abstraction). Chosen in phase 7: `Microsoft.Extensions.AI`; for learning it is useful to first
write a minimal client on `HttpClient` to see the raw tool-calling protocol.

**Notes on local models:**
- not all of them support tool calling equally well — pick models with function-calling support
  (Qwen 2.5/3, Llama 3.1+, Mistral, etc.);
- a small context window → data compression in Retrieve matters even more;
- you need **fallback parsing**: if the model returned JSON as text instead of a tool call — try to parse it;
- validate answers against the schema; an invalid answer → one repeated message with the error, then stop.

## Limits and stopping

The agent **must** stop. The stop conditions of an iteration and of the whole run:
- the model returned a final answer without tool calls;
- `MaxSteps` (tool calls inside a RALF iteration) or `MaxIterations` (Ralph loop iterations) was reached;
- the token/time budget was exceeded;
- the guardrails forbade the action and there is no alternative → escalation to a human;
- the system returned to steady state (the alert resolved, metrics within bounds).

## State and memory

- **Inside an iteration** — the LLM context (messages and tool results). It is discarded after the iteration.
- **Between iterations** — `RunState`: a short summary that the next iteration sees.
  Writing it is also the harness's job: the code records facts (the action, its effect), and at the end of an iteration the LLM
  adds 2–3 lines "what I understood / what to check next".
- **Within a run** — `AgentRun`: the full history of all iterations. Everything is serialized into the report.
- **Between runs** — the incident history: "this alert happened yesterday too, a rollback helped". Start with simple
  files/SQLite; vector search only if it is really needed.
- **Runbooks**: Markdown files with instructions "what to do for alert X" — they can be put into the context by alert name.
  This is the simplest and most useful kind of "knowledge" for an agent.

## Observability of the agent itself

The agent is a service too, and it must be watched as well: metrics (the number of runs, steps, tool calls,
tokens, duration, proposed/executed/rejected actions), structured logs of every step, a dashboard in Grafana.

## How it was developed

1. Phase 7: first a read-only observer — RALF iterations without actions (Retrieve → Analyze → report), and the Ralph loop
   repeating until the alert is resolved, without risk. This is how RunState and the completion check were debugged.
2. Phase 8: proposals, guardrails, dry-run, then approve, then auto for the safest action only. From then on the Ralph loop
   really "fixes until it is fixed" — within limits.
3. Phase 9: the agent is evaluated on chaos scenarios: did it find the cause, the right action, how many
   steps/minutes did it take. This is in effect **evals** for the agent — a set of scenarios with expected results.
