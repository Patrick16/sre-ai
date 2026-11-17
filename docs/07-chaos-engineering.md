# 07. Chaos engineering

## What it is and why

Chaos engineering is **experiments** on the system to make sure it survives failures
the way we think it does. Not "breaking things for the sake of it", but testing hypotheses. In this project chaos has
two roles:

1. check the resilience of the infrastructure itself (restarts, probes, replicas, mesh retries);
2. be a **testing ground for the agent**: create problems in a controlled way and check whether
   the agent diagnoses and fixes them correctly.

## Method (step by step)

1. **Steady state** — describe "normal" in metrics: error rate < 1%, p99 < 300 ms, all pods Ready.
2. **Hypothesis** — "if one orders-api pod is killed, the error rate will not exceed 1%, because there are 3 replicas".
3. **Blast radius** — limit the scale: one namespace, one pod, limited time.
4. **Experiment** — inject the failure.
5. **Observation** — dashboards, alerts, the agent's behaviour.
6. **Conclusion** — was the hypothesis confirmed? If not, it is a weakness found, and we fix it.
7. **Rollback** — the experiment must stop automatically (`duration`) and manually.

## Tools

### Chaos Mesh (main)
Kubernetes-native: experiments are described as CRDs, installed with a Helm chart into the namespace `chaos-mesh`.

| Type | What it does | What we expect to see |
|---|---|---|
| `PodChaos` pod-kill | Kills a pod | Recreation by the Deployment, short-lived errors |
| `PodChaos` container-kill | Kills a container | A restart, growth of `restarts_total` |
| `NetworkChaos` delay/loss | Delay/packet loss | Rising latency, the SLO alert fires, mesh retries |
| `StressChaos` | Load on CPU/memory | Throttling, OOMKilled when limits are exceeded |
| `HTTPChaos` | Errors/delays at the HTTP level | Rising 5xx |
| `Schedule` | An experiment on a schedule | A regular "check" of the agent |

We keep experiment manifests in `infra/chaos/` and always limit them with the selector
`namespaces: [sandbox]`.

### Our own .NET tools (for learning)
To understand how chaos works "from the inside", the project has a simple .NET utility, `src/Chaos.Monkey`:
- kill a random pod selected by a label selector through the Kubernetes API (essentially a small Chaos Monkey);
- switch a service's "fault mode" — through the `faults:` values of the chart (a ConfigMap, no pod restart).

### Built-in fault modes in the sandbox services
The most controllable kind of chaos is bugs "behind a flag" (`src/Sandbox.Services/Shared/Faults.cs`):
- `latency`: add N ms to responses;
- `error-rate`: return 500 for X% of requests;
- `memory-leak`: accumulate memory until OOMKilled.

These three are implemented. `crash-on-start` (failing at startup) and `bad-config` (a "broken" image version to test
a rollback) are not implemented: without a rollback the agent has nothing to do with them except recommend.

## Scenarios for the agent (future evals)

| # | Failure | Expected agent conclusion | Expected action |
|---|---|---|---|
| 1 | pod-kill of one pod out of 3 | Self-healed, no intervention needed | None |
| 2 | memory-leak in orders-api | OOMKilled, memory growing | restart (temporary) + recommendation to fix limits/code |
| 3 | Rollout of a version with crash-on-start | CrashLoopBackOff after the rollout | rollback (approve) |
| 4 | Network delay to postgres | Latency rose because of the DB, not orders-api | No action, escalation with the exact cause |
| 5 | error-rate 30% in worker | Errors in the worker's logs | restart, effect check, escalation on failure |
| 6 | High load (load test) | CPU saturation | scale within the limit |

For each scenario we record: did it find the cause, was the action right, how many steps/tokens/minutes it took.

Implemented in Phase 9 ([18](18-phase-9-chaos-engineering.md)): scenarios 1, 2, 4, 5 (5 — in orders-api, not in worker) and
CPU stress — as data in `infra/chaos/scenarios.json`, run by `scripts/game-day.ps1`. Scenario 3 (rollback) and 6 (scale)
were not done: the agent has no such actions (they belong to Helm, see [17](17-phase-8-actions-guardrails.md)).
`crash-on-start` and `bad-config` are also not implemented.

## Load

For realistic metrics you need background traffic: the load generator is k6 in the chart `infra/helm/loadgen`
(about 5 rps through Traefik).

## Safety rules

- chaos **only** in the `sandbox` namespace;
- every experiment has a `duration`;
- before an experiment the steady state must be green; otherwise the result is unclear;
- never run chaos against `monitoring` as part of the agent scenarios —
  the agent must be able to see what is happening.
