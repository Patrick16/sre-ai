# 19. Phase 10: everything from scratch with one command, and the project's results

## One command

```powershell
./scripts/up.ps1          # an empty Docker → a verified, working sandbox
./scripts/smoke-test.ps1  # "is everything working?" — at any time, read-only
```

[up.ps1](../scripts/up.ps1) is not a new deployment — it is the order of the already familiar steps plus a check of the result:

```
prerequisites   versions of docker / kind / kubectl / helm 4 / dotnet, Docker is running
cluster-up      kind (3 nodes, 80/443 only on 127.0.0.1) + pull-through image caches (they survive cluster-down)
deploy-infra    namespaces, policies, monitoring → Traefik → cert-manager + CA → Linkerd → Loki/Alloy → Chaos Mesh → dashboards
build-images    4 .NET images, tags from the release values files, kind load
deploy-apps     secrets (generated once) → postgres → orders-api → worker → frontend → loadgen → sre-agent
smoke-test      read-only checks
```

The script is idempotent: on a live cluster it re-applies everything and breaks nothing — this is a property of Helm
(`upgrade --install`) and `kubectl apply`, not a separate logic.

**Why a smoke test, and not "the scripts finished without errors".** In Phase 9, after a Docker Desktop restart, all the scripts
were happy, while the site answered 500: the pods had started outside the mesh. [smoke-test.ps1](../scripts/smoke-test.ps1) checks
what the user sees and what the agent needs:

- nodes and pods are ready in all the platform and application namespaces; **every** pod in `sandbox`/`sre-agent` — with a `linkerd-proxy`;
- the site and the API through the Ingress over HTTPS (frontend → orders-api → postgres — the whole chain);
- Grafana, Alertmanager; the Prometheus targets in `sandbox` are `up`; the SLI rules are computed; logs reach Loki;
- the agent's admission policies are in place; the agent is ready and in `DryRun` mode (and was not left in Auto after a game day);
- LM Studio with the model — a warning, not an error: without a model the agent does not think, but the cluster is fine.

### Bring-up from scratch: what the check showed

`cluster-down` → `up.ps1` on warm image caches — **the first time, 8 min 32 s**, smoke test 24/24:

| Step | Time |
|---|---|
| prerequisites | 0:01 |
| the kind cluster + caches | 0:39 |
| the platform (monitoring, Traefik, cert-manager, Linkerd, Loki, Alloy, Chaos Mesh) | 5:07 |
| 4 .NET images + kind load | 0:53 |
| 6 Helm releases of the applications | 0:40 |
| smoke test (including 60 s for the first scrapes) | 1:10 |

The platform is 60% of the time: every chart is installed with `--wait`, and Linkerd and Prometheus start the longest. Without the caches
of images (a first run on a new machine) add 10–20 minutes for the downloads. The warnings `would violate PodSecurity
"baseline"` during the bring-up are expected: they come from the `warn` label of the privileged namespaces (node-exporter, Alloy,
chaos-daemon), which by their nature read the host.

The smoke test does not check whether the agent **thinks**. So after the bring-up — a manual health check (`POST /runs {}`) on the fresh
cluster: `Resolved` in one iteration, 256 s, 3 tool calls, ~8.5k tokens. The conclusion — "temporary readiness failures (502) of the frontend
pods at start, recovered by themselves", no actions proposed — exactly what happened: the frontend started before orders-api became ready.
The path agent → LM Studio through the default-deny egress works from scratch.

## GitOps (Argo CD): a description, without adoption

Right now the deployment is a **push**: a human runs `deploy-apps.ps1`, and Helm applies it. GitOps is a **pull**: a controller
(Argo CD, Flux) lives in the cluster and constantly compares the cluster with git and brings it to the git state. The same reconcile loop
as the Deployment controller and the agent's Ralph loop — only the "desired state" lies in a repository.

How it would fit the project:

| Now | With Argo CD |
|---|---|
| `deploy-apps.ps1` → `helm upgrade` for each values file | An `Application` for each release: `source.path: infra/helm/sandbox-service`, `helm.valueFiles: [../releases/sandbox/orders-api.yaml]` — or one `ApplicationSet` with a generator over the folders `releases/*/*` |
| the order postgres → orders-api → … in the script | `argocd.argoproj.io/sync-wave` in the Application (postgres — wave 0, clients — 1, the agent — 2) |
| "don't do `kubectl edit`, there will be a conflict with Helm" | Argo sees the drift and (with `selfHeal: true`) returns the git version — the rule becomes a mechanism |
| the script generates the secrets | secrets cannot be in git: External Secrets Operator / Sealed Secrets / SOPS |
| images `kind load` with a tag from the values | Argo does not build images: a registry is needed (a local `registry:2` on the `kind` network already exists — the caches) and a CI that pushes the image and changes the tag in the values with a commit |

Where GitOps **would collide** with what has been built:

- **The agent's actions.** `restart_deployment` writes the annotation `restartedAt`, which is not in git. Argo with server-side diff
  ignores the fields of other field managers, but with client-side diff it would see the drift and, with `selfHeal`, roll it back — it needs
  `ignoreDifferences` for this annotation. The same story as with Helm in Phase 8: each field must have one owner.
- **Chaos modes.** `helm upgrade --set-json faults.errorRatio=0.3` is a drift from git, and `selfHeal` would "fix" it within a minute.
  In GitOps the failure is switched on **with a commit** (and switched off with a revert) — the experiment becomes visible in the history,
  which is even better for a game day. Chaos Mesh CRDs are also an Application, but with `prune: false`, so that `kubectl delete` for
  stopping an experiment early would not fight Argo.
- **The agent stops being the only writer.** Two parties start "fixing": Argo (towards git) and the agent (towards the steady state).
  The right boundary: the agent changes only what is not in git (a restart), and everything that is in git (replicas, the image)
  it proposes as a **pull request** — the next logical mode after `Approve`.

Why it was not adopted: GitOps is about a team's process (review, history, rollbacks by commit). In a sandbox with one person and
without a registry it would add three components (Argo, a registry, CI) for the same result as `up.ps1`.

## Results: what was learned

**Kubernetes**

- Everything in k8s is a reconcile loop: you declare "I want", and the controller keeps comparing it with "what is". Deployment, ReplicaSet,
  cert-manager, Linkerd identity, Helm 4, Argo CD, the agent's Ralph loop — one and the same idea. Once you understand it,
  you understand most of the components.
- **Each field has one owner.** Server-side apply made this explicit: `managedFields`, the Helm conflicts with `kubectl
  scale`, the agent's restart annotation, the future GitOps. The most common reason for "why did it revert my change?".
- Probes are a policy, not a health check: a readiness probe that looks at the database turns a database outage into an outage of all the replicas (Phase 4).
- Security — in layers that do not trust each other: Pod Security, NetworkPolicy default-deny, mTLS with identity on the
  ServiceAccount, RBAC, an admission policy. RBAC cannot express "only this annotation" — for that there is CEL in the API server.
- The mesh is the start order and port 4143. The two most expensive mistakes of Phases 6 and 9 were not in the YAML, but in the sequence
  of events.

**SRE**

- Alert on the user's symptoms (SLI/burn rate), not on the causes (CPU): the CPU stress gave 100% throttling and 0%
  impact. The burn-rate alerts are slow **on purpose** — and because of this they are not fit as a criterion for "fixed".
- Chaos is valuable for its refuted hypotheses. The confirmed ones are pleasant — the refuted one (CPU) taught the most.
- An experiment requires hygiene: the steady state before, a quiet period, a clean state after. Otherwise the next experiment
  measures the tail of the previous one.

**The agent**

- **The harness matters more than the model, and the data matters more than the prompt.** All the big improvements were not in the wording: "scaled to ZERO" in the
  tool data (Phase 7), facts separate from hypotheses and the synthesis (Phase 7), latency and the age of events in Retrieve
  (Phase 9). From 0/3 to 2/3 — without a single edit of the system prompt.
- **"Done" is decided by code.** The model readily declares success; the completion check by metrics does not. But it, too, had to be refined three times:
  alerts → alerts + SLI → + pods and restarts.
- **The model only proposes.** The `action` field in JSON, not a tool; guardrails in the code; RBAC and the admission policy in the
  cluster, which do not trust the code. During the game day: 9 restart proposals, 1 executed. The most useful guardrail
  (`current-symptom`) appeared not from the design, but from an e2e run in which the agent restarted a healthy service.
- **Before/after ≠ cause.** "Improved" after a restart may be a natural recovery. The measurement is a fact, the causality is not; the report must
  distinguish the two.
- A 9B model: it retells the explicit well, infers the implicit poorly, and clings to the loudest signal. This is a limitation the harness is designed around,
  not a bug that is fixed with a prompt.

## What I would do differently

1. **A smoke test from the first phase**, not from the last. Each phase was checked by hand, and the cold start of the mesh was found by chance,
   not by a test.
2. **Evaluate the agent earlier and more strictly.** The game day appeared in Phase 9, but it could have appeared in Phase 7: scenarios + a scorecard from the first day of the agent would have given numbers for every change of the harness. And an LLM judge at once instead of the regexes that counted
   "the limit is too small" as "a leak".
3. **A registry of images instead of `kind load`.** The pull-through caches already existed — one more `registry:2` for our own images would have removed
   "I rebuilt the same tag — now do a rollout restart", and opened the way to GitOps.
4. **Time on every signal from the first day.** Half of the agent's errors came from data without time: old logs, `lastState`
   without "when", 5-minute windows. Retrieve should have written from the start "X, N minutes ago".
5. **Fewer phases for the infrastructure, the agent earlier.** The mesh and mTLS are useful as a lesson, but for the agent they mostly brought
   problems (4143, the cold start). The agent MVP after observability (Phase 5) and the mesh later — an order in which every new
   complication is immediately tested by the agent.

## The code review after Phase 10 (2026-11-10)

All 20 findings were fixed (the reports themselves are kept locally and are not part of the repository). The lesson: **the most
dangerous errors were in handling failures, not in the main scenario.** Each of them shows up only when something is already broken, so the game day did not find them:
- an action was executed, and then the *check* failed — and the action disappeared from the report;
- the *completion check* failed — and the run died, although the rest of the code was specifically "surviving" such failures;
- the queue put the request into the channel before writing it to the journal (a race), and `BoundedChannelFullMode.DropWrite`
  returns `true` even when it throws the write away — "the queue is full" was not detected;
- the "exactly two actions" protection checked not all the fields of the Deployment (`paused`, labels, finalizers).
The rule: for every operation with a side effect ask "what if it fails right AFTER it?" and write a test for that.

Decisions accepted and left as they are: the base Docker images (`sdk:10.0`, `aspnet:10.0`) are not pinned by digest —
for a local sandbox without a registry this is a deliberate compromise (pinning without automatic updates goes stale quickly); resuming an
interrupted run is not implemented (RunState is a journal, see docs/16).

## State at Phase 10

The figures and observations in the phase docs 10–18 were recorded on the project in the state of the tag `phase-10-baseline`.
After implementing [20](20-phases-11-plus-plan.md) the default behaviour is the same (the new things are off), but if
you switched on the options from there, the figures may differ. To return to the state the docs describe:
`./scripts/reset-exercise.ps1` (the cluster) and `git switch --detach phase-10-baseline` (the code).

Snapshot tags: `phase-0` … `phase-10` — the commits "Complete Phase N" (`phase-7` — the commit of the report synthesis right after
it). `phase-10` (`0880985`) — the state at the time of writing this document, `phase-10-baseline` (`ef6dbfe`) —
the same plus the fixes of the code review of 2026-11-10; `reset-exercise.ps1` rolls back to it. A separate folder with
a snapshot: `git worktree add ../sre-ai-phase-N phase-N` (the docs of phases 0–9 have a ready snippet for the exercises).

## Where next

- An LLM judge and regular evals of the agent (the scorecard as CI for prompts and the harness).
- A comparison of models: the same game day on a larger model — where the line "the harness compensates for the model" is.
- A "PR instead of an action" mode: the agent proposes a change to the values as a commit — together with GitOps.
- The exercises of phases 0–9 (they are not ticked in the roadmap — this is the owner's work, not something to automate).
