# 18. Phase 9: chaos engineering and the game day

The method — [07-chaos-engineering.md](07-chaos-engineering.md). Here — what was built, what the experiments showed, and how the agent
went through the game day.

## What we got

```
three sources of failures                           game day (scripts/game-day.ps1, scenarios in infra/chaos/scenarios.json)
  ├─ "bad modes" in the services  ─┐                 for each scenario:
  │   latency / 500 / leak        │                   1. steady state? (all 5m SLIs ≤ 1%, all pods ready) — otherwise skip
  │   (helm --set-json faults.*)  ├──► failure ──►    2. inject the failure
  ├─ Chaos Mesh (CRDs)            │                   3. "users are complaining" → POST /runs {"incident": true, "note": ...}
  │   pod-kill / delay / stress   │                   4. after faultMinutes — remove the failure (otherwise steady state is unreachable)
  └─ Chaos.Monkey (.NET)         ─┘                   5. wait for the run, take the report, evaluate against the expectations
      kill a random pod                              → .local/game-day/<time>/scorecard.md + the reports
```

## 1. "Bad modes" in the services

[Shared/Faults.cs](../src/Sandbox.Services/Shared/Faults.cs) — one file, included in all three services
(`<Compile Include="../Shared/*.cs">`; that is why the build context of the images is now `src/Sandbox.Services`).

| Value (`faults:` in the values) | What it does |
|---|---|
| `latencyMs`, `latencyRatio` | a delay for a share of the requests |
| `errorRatio` | a share of 500 responses + a line `Unhandled exception … InvalidOperationException` in the log |
| `leakMbPerMinute` | a leak of native memory with a steady ramp → OOMKilled at the limit, and again after each restart (every 6 s — 1/10 of the per-minute norm; the fractional remainder carries over to the next tick: before, rounding up on each tick turned 5 MB/min into 10) |

Decisions worth understanding:

- **It is switched on without a restart.** The chart renders the values into a separate ConfigMap `<name>-faults`, mounted as a
  **directory** (not `subPath` — only then does the kubelet update the file in a running pod, in ~40–70 s), and it is **not** part of the
  pod template's checksum. A restart would itself "cure" the leak and hide the effect — a real bug does not behave that way.
- **Through Helm, not `kubectl edit`.** `helm upgrade orders-api … --set-json faults.errorRatio=0.3`, rollback —
  `deploy-apps.ps1 -Services orders-api`. `--set-json`, not `--set`: `--set` makes `0.3` a string, and the strict chart schema rejects it.
- **The fault is placed after routing**, so the 500s and delays get the `http_route` label and count in the SLI like real ones. The `/health` probes and the metrics port are not touched: the fault is in the service's work,
  not in how Kubernetes sees it.
- **The service does not confess.** The logs do not contain the word "fault" — the agent has to find the cause from the symptoms.

Check: `errorRatio=0.5` → 57% 5xx over a minute, the pods did not restart; after the rollback — 0%.

## 2. Chaos Mesh

Chaos Mesh 2.8.7 ([values](../infra/helm/values/chaos-mesh.yaml)), installed as the last step of `deploy-infra.ps1`;
its images come from ghcr.io — for them one more pull-through cache was added in `registry-cache.ps1`.

- **Blast radius in the controller**: `enableFilterNamespace: true` — experiments are allowed only in a namespace with the
  annotation `chaos-mesh.org/inject: enabled`, and only `sandbox` has it. A typo in a selector will not hit
  `monitoring`.
- `chaos-daemon` — a privileged DaemonSet: it enters the network namespace and the cgroup of another pod, in order to add
  a delay (`tc`) or run `stress-ng`. That is why its namespace is Pod Security `privileged`, like the monitoring one.
- `runtime: containerd` — the kind nodes run containerd, not Docker.

The experiments are in [infra/chaos/](../infra/chaos/). Each has a `duration` or is one-shot: a forgotten experiment
ends by itself.

### What the experiments showed

| Experiment | Hypothesis | Result |
|---|---|---|
| pod-kill 1 of 3 orders-api | users will not notice | ✅ **confirmed**: 5xx 0%, after 30 s 3 ready again, the frontend SLI did not grow |
| +300 ms on the postgres responses | the orders-api latency SLO (< 250 ms) will burn | ✅ **confirmed**: after 60 s 100% of requests slower than 250 ms, the frontend SLI 0.1% → 11% |
| CPU stress in orders-api | throttling at the 500m limit → higher latency | ❌ **refuted**: throttling in 100% of the CFS periods, slow requests — 0% |

The third result is what chaos is done for. At ~5 rps the application has enough of the CPU slices that remain: the "throttled" metric burns,
users notice nothing. "The infrastructure metric is bad" ≠ "users have a problem" —
so the alerts are built on the SLI, not on the CPU. The scenario in the game day was reworked into a check for a **false alarm**: users
complain, but there is no impact — the agent must establish this and touch nothing.

## 3. Chaos.Monkey — the same thing by hand

[src/Chaos.Monkey](../src/Chaos.Monkey/Program.cs): ~80 lines that do what PodChaos pod-kill does —
a list of pods by label → a random one → `DELETE` with `gracePeriodSeconds: 0` (like a crash, without preStop). The point — to see that
there is no magic, and that all the value lies in the safety rules (these are the same ideas as the agent's guardrails):

```powershell
dotnet run --project src/Chaos.Monkey -- --app orders-api                          # dry run: whom it would kill
dotnet run --project src/Chaos.Monkey -- --app orders-api --kill --every 60 --for 10
```

- the namespace is only `sandbox` (a list in the code, not a flag); `postgres` is refused (one replica = this is an outage, not an experiment);
- never the last ready pods: at least `--keep` (2) must remain alive, otherwise the round is skipped;
- a dry run until `--kill` is given; a time limit `--for` and Ctrl+C.

## 4. Changes in the agent (0.5.0)

- **An incident declared by a human**: `POST /runs {"incident": true, "note": "users report errors"}`. It works like a run from an alert — up to
  the steady state. Why: the burn-rate alerts are slow on purpose (the 1 h window: at 30% errors the fast burn fires after ~15–30 min),
  and the game day needs a predictable start. In reality this is how it goes: the on-call person declares an incident from users' complaints.
- **The steady state is stricter**: to "alerts allowed + all 5m SLIs ≤ 1%" was added "all live pods are ready and not a single
  container restart over 5 min". Without this, a memory leak in which the SLIs are green while the pods fall over in turn with
  OOMKilled would be considered "solved" at the first iteration.

## 5. An out-of-plan finding: the mesh cold start

In the middle of the phase Docker Desktop restarted. The kind nodes came back, but:

- the pods with the mesh hung in `Unknown`: when a pod was recreated, the `linkerd-cni` plugin asked the API server about the pod, and it had not
  come up yet (`dial tcp 10.96.0.1:443: connection refused`) → the pod's network without a redirect to the proxy → the init container
  `linkerd-network-validator` fails forever;
- the Linkerd control plane itself turned out to be among the stuck ones → its injector did not work → the new pods (from my deploy)
  were created **without a proxy** (`1/1` instead of `2/2`; the webhook with `failurePolicy: Ignore` lets them through) → the default-deny
  NetworkPolicy allows only 4143 between pods → calls hung for 5 s and failed with 500.

This does not heal by itself. [scripts/recover-mesh.ps1](../scripts/recover-mesh.ps1): recreate the stuck pods (the Linkerd control plane first),
then restart the workloads whose pods run without a proxy. The lesson: the mesh has a **start order**, and "the cluster came up" ≠ "the services work" —
a check is needed, not hope.

### Continuation (2026-11-11): the host did not restart, it slept

Symptom: the site answers 502, all pods are `Running`/`Ready`, `recover-mesh.ps1` finds nothing. In the orders-api logs —
`Database unavailable: Exception while reading from stream`, and in the `postgres-0` proxy every 10 seconds
`Failed to obtain identity ... broken pipe`.

What happened (reconstructed from the logs in Loki):

| Time (UTC) | Event |
|---|---|
| 11-09 21:58 | the postgres proxy got a certificate for 24 hours (until 11-10 21:58) |
| 11-10 15:24 | the scheduled renewal (70% of the lifetime + 38 minutes that the host slept in the morning): `linkerd-identity` was not working at that moment → a refusal. The next attempt was scheduled at 70% of the remainder, that is, after 4.6 hours of **work** |
| 11-10 18:35 – 11-11 05:25 | the laptop sleeps (Loki has not a single line from any pod) |
| 11-10 21:58 | the certificate expired — in the sleep |
| 11-11 06:55 | the timer finally fired (exactly 4.6 hours of wakefulness after 15:24), but with the expired certificate the proxy can no longer renew it |

The root: the proxy renews its certificate by a timer that **does not count the host's sleep time**. Having slept more than 30% of the lifetime
(7.2 hours), the certificate expired before the timer fired. This was confirmed twice: the same way `linkerd-viz/web` broke
(the certificate expired 11 hours ago), and both numbers in the table (38 minutes and 4.6 hours) agree with the calculation.
Not proven: why a proxy with an expired certificate cannot get a new one — on the `linkerd-identity` side the logs show
nothing, only the broken connection is visible. On a real server this does not happen — servers do not sleep; this is a quirk
of a cluster on a laptop.

What was changed:

- `recover-mesh.ps1` — a third step: it asks Prometheus for `control_identity_cert_expiration_timestamp_seconds` and
  recreates the pods whose proxies have less than 7 hours left (a healthy proxy never has less than ~7.2). After a long
  sleep everything can be renewed at once: `./scripts/recover-mesh.ps1 -MinCertHours 25`.
- `smoke-test.ps1` — a `mesh certificates` check (now 25 checks): this time it would have failed and named the script.

The same lesson as above, but deeper: a pod's `Ready` means "the containers answer the probes", not "the pod can talk to its neighbours". The certificate's
expiry is a separate signal, and it must be measured.

## 6. The game day

`./scripts/game-day.ps1 -AgentMode Auto`: 5 scenarios in 85 minutes, agent 0.5.0, `qwen/qwen3.5-9b`. The evaluation — regular expressions
on the cause from the synthesis + "was there an action where there should not have been one" ([scenarios.json](../infra/chaos/scenarios.json)).

| Scenario | Evaluation | Action | Cause in the report | Analysis |
|---|---|---|---|---|
| pod-kill 1 of 3 | ✅ hypothesis confirmed | — | worst SLI 0.0% | the agent is not needed: the infrastructure coped by itself |
| 30% of responses 500 in orders-api | 3/3 | restart → **Unchanged** (26.6% → 21.5%) | "a logic error in orders-api (InvalidOperationException), the restart did not help — the problem is in the application, not in the infrastructure" | the best result: the cause is precise, the action honestly measured |
| +300 ms on postgres | **0/3** | 3 restart proposals, all **Refused** (current-symptom) | "a stalled processing loop in the worker" | a failure, see below |
| a leak of 60 MB/min | 1/1 (inflated) | restart → Refused | "the 256 MB limit is too small for the peaks" | OOMKilled found, but the "leak" (growth) not named; the regex `OOM\|памят` counted it |
| CPU stress (a false alarm) | 1/2 | restart → Refused | "OOMKilled at 08:06 because of the memory limit" | contamination: the OOMKill was from the **previous** scenario |

### What follows from this

**1. The agent did not see the latency.** The Retrieve snapshot showed the share of "bad" requests and the response codes — all 200 and 499.
The model looked for errors that were not there and found a false trail: the database's delay slowed the worker's loop, and its liveness
wrote "processing loop stalled". The failure was not the model's but the **data of the harness**: a "bad request" includes a slow one. In 0.5.1
a section "Latency per service, p50/p99" and the time of requests to the database from the client side
(`db_client_operation_duration_seconds` from the Npgsql metrics) were added to Retrieve. The repeat — below.

**2. current-symptom looks only at 5xx and unready pods.** In the latency scenario it refused correctly three times, but
for the wrong reason: it does not see latency either. If it were taught latency, the unnecessary restart **would have passed** —
only the model would have remained as the protection. This is an honest limitation: the guardrail answers "is there a problem now", not
"will a restart help".

**3. Contamination between scenarios.** The agent looks back 10–30 minutes (logs, events), and the false alarm of CPU stress was
explained by the OOMKills of the previous scenario. A **quiet period** (`-QuietMinutes 15`) was added to the game day: before a scenario
the steady state, including "no restarts", must hold for 15 minutes.

**4. Evaluation by regular expressions inflates.** "The limit is too small" instead of "the memory is leaking" passed as a correct answer. The regexes catch
gross misses (blaming the database instead of orders-api), but not nuances. The next step is an LLM judge with a rubric (another, stronger model
compares the report with the description of the failure), and until then a human reads the reports in `.local/game-day/`.

**5. The experiment is visible to the observer.** In the CPU stress scenario the synthesis wrote "the chaos experiment cpu-stress-orders-api
was applied later" — Chaos Mesh leaves Kubernetes events on the pods. For the agent's evaluation this is a hint; the built-in
service modes (`faults:`) leave no such trace.

**6. The guardrails worked.** During the game day the agent proposed a restart 6 times, and one was executed — where there was a current symptom
(30% 500s); its effect was measured and recorded as Unchanged. The other 5 were stopped by `current-symptom`.

### The repeat of the failed scenario (+300 ms on postgres)

| Agent | What changed | Evaluation | Cause in the report |
|---|---|---|---|
| 0.5.0 | — | 0/3 | "a stalled processing loop in the worker" |
| 0.5.1 | latency p50/p99 and the time of requests to the DB in Retrieve | 1/3 | "not enough memory, OOMKilled" — the DB is named, but as a consequence |
| 0.5.2 | the pod shows **when** it last stopped | 2/3 | the same; but iteration 2: "high database latency (p99 4863 ms) causes timeouts in orders-api" — correct |

In all the repeats actions were proposed (a restart of orders-api), and all were refused by `current-symptom`.

A new contamination — **through the pod's state**, not through the search windows: after the leak scenario the orders-api pods
kept "restarts 2 (last: OOMKilled)" — `lastState` lives as long as the pod does, and the quiet period is powerless against it.
With 0.5.2 the model sees "OOMKilled 160 min ago" — and still puts it into the cause in 2 of 3 iterations, while the synthesis picks
the majority version, not the best-justified one.

Conclusions:

- **The data of the harness matters more than the prompt.** From 0/3 to 2/3 — not by rewriting the instructions, but by showing the model
  the latency and the times of the events. It is the same lesson as "scaled to ZERO" in Phase 7.
- **The limit of a 9B model**: even with the correct data it clings to the loudest signal (OOMKilled sounds more serious
  than p99). The synthesis inherits this: it collates the iterations, but does not re-check them. A comparison with a stronger
  model — exercise 2 from [16](16-phase-7-agent-mvp.md).
- **Experiment hygiene**: after each scenario with a "bad mode" the game day now restarts the service —
  the next scenario starts from a clean slate. In production there is no such "reset", and that is exactly why old signals
  must carry their time.

## Exercises

**Project snapshot at this phase** — the tag `phase-9` (commit `e3026d3`). It was not re-run to verify —
it is the state the exercises were composed for. LM Studio must be running.

```powershell
git worktree add ../sre-ai-phase-9 phase-9   # a separate folder; your working copy stays unchanged
cd ../sre-ai-phase-9
./scripts/cluster-down.ps1                   # the cluster name is shared (sre-ai): delete the current one first
./scripts/cluster-up.ps1
./scripts/build-images.ps1
./scripts/deploy-infra.ps1
./scripts/deploy-apps.ps1
# ... exercises ...
cd ../sre-ai; git worktree remove ../sre-ai-phase-9 --force
./scripts/reset-exercise.ps1 -Level Cluster -Yes   # rebuild the current project from scratch
```

**Preconditions and rollback.** You need a green `./scripts/smoke-test.ps1` and LM Studio with the model; the `loadgen` load is
by default ~5 rps (exercise 2 raises it temporarily). The script rolls back the failure modes (`faults:`), deletes the
Chaos Mesh experiments, and starts the stopped nodes; at the end it always calls
`recover-mesh.ps1` (after a restart of Docker Desktop this is especially needed):

```powershell
git restore infra/
./scripts/reset-exercise.ps1
```

1. Repeat the game day in `DryRun` mode and compare the proposed actions with Auto — does the diagnosis change if the agent
   knows that its actions are executed?
2. Raise the loadgen load to 50 rps and repeat the CPU stress — at what load does throttling become visible to the SLO?
3. `Chaos.Monkey --kill --every 20 --for 5` against orders-api: does the SLI hold? And with `--keep 0` (remove the check) and
   `--every 5`?
4. Add the scenario `latencyMs: 400` to the frontend and check: will the agent find that the slow one is the frontend itself, not orders-api?
5. Make a Chaos Mesh `Schedule` that kills a pod every 10 minutes, and leave it overnight: what will the SLI show over a day?
