# 01. Roadmap

The project proceeds in phases. Each phase is a **learning goal** (what to understand) and a **result**
(what must work). Do not move on to the next phase until you can explain the previous one in your own words.
Tick the checkboxes as you go.

Principle: first do it by hand (`kubectl apply` of raw manifests) to understand the mechanics, and only then
automate (Helm, scripts).

---

## Phase 0. Environment and structure

**Understand:** what a container, an image and a registry are; how kind differs from a "real" cluster.

- [x] Repository structure and documentation
- [x] Install: Docker Desktop, .NET SDK, kubectl, kind, Helm, k9s (a TUI for the cluster) — see [09](09-dev-environment.md)
- [x] Create the .NET solution (`SreAi.slnx` in the root) with empty projects
- [x] Script `scripts/cluster-up` / `cluster-down` (kind, 1 control plane + 2 workers)
- [x] Fill in the Commands section in `CLAUDE.md`
- [ ] Upgrade kubectl to the cluster's version (currently 1.33 against 1.37)
- [ ] Complete the exercises from [09-dev-environment.md](09-dev-environment.md)

## Phase 1. Basic Kubernetes

**Understand:** Pod, ReplicaSet, Deployment, Service, Namespace, ConfigMap, Secret, probes, requests/limits.

- [x] 3 sandbox services in .NET: `frontend` → `orders-api` ← `worker` (minimal HTTP) — see [10](10-phase-1-kubernetes-basics.md)
- [x] A shared multi-stage Dockerfile, loading the images into kind (`scripts/build-images.ps1`)
- [x] Raw manifests (an optional manual copy in `infra/manual/phase-1/`; from Phase 3 the main path is Helm): Namespace (Pod Security `restricted`), Deployment, Service
- [x] ConfigMap with settings, Secret with an API key — attached both as env and as a file
- [x] liveness/readiness/startup probes, resources requests/limits, securityContext
- [ ] Complete the exercises from [10-phase-1-kubernetes-basics.md](10-phase-1-kubernetes-basics.md) (deleting a pod, scale, OOMKilled, rollout undo…)

## Phase 2. Traffic from outside: Ingress and TLS

**Understand:** why a Service is not enough, what an Ingress Controller does, how TLS works.

- [x] Install an Ingress Controller — Traefik (ingress-nginx was retired in March 2026), see [11](11-phase-2-ingress-tls.md)
- [x] Ingress for `frontend` by the host `sandbox.localhost`, `/api` → orders-api (path-based)
- [x] cert-manager + our own CA (self-signed root → ClusterIssuer `sandbox-ca`), HTTPS and redirect from HTTP
- [ ] Exercises from [11-phase-2-ingress-tls.md](11-phase-2-ingress-tls.md): host-based routing, certificate rotation
- [ ] (Optional) the same route via Gateway API (`Gateway` + `HTTPRoute`) on Traefik

## Phase 3. Helm

**Understand:** chart, release, values, templates, why packaging manifests matters.

- [x] A shared chart `infra/helm/sandbox-service` (one template for all services, schema, `helm test`) — see [12](12-phase-3-helm.md)
- [x] A separate release and values file per service (`infra/helm/releases/sandbox/`) — chosen instead of an umbrella chart
- [x] Move everything from Phases 1–2 to Helm without downtime (`--take-ownership`, cleaning `managedFields`), delete the raw manifests
- [x] Experiment: `helm upgrade` with a new version, `helm rollback` — 128/128 requests without errors
- [x] Split the deployment into platform (`deploy-infra.ps1`) and applications (`deploy-apps.ps1`); verified from scratch
- [ ] Exercises from [12-phase-3-helm.md](12-phase-3-helm.md) (failed release + automatic rollback, drift via `kubectl scale`)

## Phase 4. Data storage

**Understand:** PV, PVC, StorageClass, StatefulSet, why a database is not a Deployment.

- [x] PostgreSQL 18 as a StatefulSet — our own chart `infra/helm/postgres` (Bitnami no longer updates its images), PVC via `volumeClaimTemplates`, no root — see [13](13-phase-4-storage.md)
- [x] `orders-api` 0.2.x writes to the DB; all replicas share the state, the worker processes all orders
- [x] Experiment: delete the DB pod — data survived (about 3.5 s of downtime, 503); `helm uninstall` — the PVC is kept (`Retain`); delete the PVC — the data is gone
- [x] Found and fixed a bug "health is green, the service is broken" after the database was swapped (0.2.1)
- [ ] Exercises from [13-phase-4-storage.md](13-phase-4-storage.md): losing a node with a PV, a `pg_dump` backup, retries on dead connections
- [ ] (Optional) CloudNativePG: 3 instances and automatic failover

## Phase 5. Observability

**Understand:** metrics vs logs vs traces, Prometheus's pull model, PromQL, SLI/SLO, alerts.

- [x] kube-prometheus-stack (Prometheus on a PVC, Alertmanager, Grafana over HTTPS, node-exporter, kube-state-metrics) — see [14](14-phase-5-observability.md)
- [x] Metrics from the .NET services (OpenTelemetry, a separate port 9090) + PodMonitor; postgres-exporter + ServiceMonitor; Traefik and cert-manager metrics
- [x] Loki + Grafana Alloy (DaemonSet), structured JSON logs from the services
- [x] "Golden signals" dashboard (latency, traffic, errors, saturation + burn rate, dependencies, logs)
- [x] SLOs for `orders-api` (99% < 250 ms — a bucket boundary) and `frontend` (99% < 500 ms), multi-window burn-rate alerts; `PostgresDown`
- [x] Alertmanager → a webhook stub of the agent (`SreAgent.Host`); the end-to-end scenario "database down → alert → agent" verified
- [x] Background load (k6, about 5 rps through Traefik)
- [ ] Exercises from [14-phase-5-observability.md](14-phase-5-observability.md): silences, latency SLO, LogQL
- [ ] (Optional) traces: OpenTelemetry → Tempo

## Phase 6. Network security: NetworkPolicy, mTLS, Service Mesh

**Understand:** zero trust inside the cluster, the sidecar proxy, what exactly mTLS encrypts.

- [x] NetworkPolicy: default-deny + DNS per namespace, each chart declares its own allowances; blocking verified — see [15](15-phase-6-network-security.md)
- [x] Its own ServiceAccount per service (the mTLS identity), without an API token
- [x] Linkerd edge-26.11.1: identity via cert-manager, linkerd-cni (compatibility with Pod Security `restricted`), native sidecar, inject for `sandbox` and `sre-agent`
- [x] mTLS verified through the proxy metrics (`tls=true`, `client_id` = ServiceAccount); load balancing by requests fixed the Phase 1 effect
- [x] Retries (GET only) and timeouts through Gateway API HTTPRoute; the effect measured; proxy metrics in our Prometheus, Linkerd Viz on top of it
- [ ] Exercises from [15-phase-6-network-security.md](15-phase-6-network-security.md): mesh for Traefik, `AuthorizationPolicy` by identity
- [ ] (Optional) compare with Istio ambient — understand how they differ

## Phase 7. Agent MVP (read-only)

**Understand:** how an agent harness is built, tool calling, LLM context, the Ralph loop and the RALF iteration.

- [x] Microsoft Agent Framework (the successor of Semantic Kernel) on top of Microsoft.Extensions.AI — see [16](16-phase-7-agent-mvp.md)
- [x] An LLM client abstraction: an OpenAI-compatible API and LM Studio through one interface; default model `qwen/qwen3.5-9b`, a choice from `AllowedModels`
- [x] Read-only tools: pods, workloads, Kubernetes events, PromQL, logs from Loki, active alerts
- [x] RALF iteration v1: Retrieve (code) → Analyze (agent, fresh session, step budget) → Lead (observe-only) → Feedback (completion check)
- [x] Ralph loop v1: TaskSpec, RunState on disk, the "namespace steady state" completion check, limits, backoff
- [x] Runs triggered by an Alertmanager webhook, on a schedule, and manually (`POST /runs`); one run per namespace
- [x] A Markdown report and an audit log (JSONL) on the PVC
- [x] The agent's own metrics and logs (+ `gen_ai.*` from Microsoft.Extensions.AI)
- [x] 17 harness tests on a "scripted" model; read-only RBAC in `sandbox`; default-deny egress
- [x] End-to-end scenario "database down → agent → steady state" — `Resolved`; the steady-state criterion and resilience to model failures were found and fixed
- [x] A final report synthesis across all iterations + "established facts" in RunState (the final root cause was sometimes worse than the intermediate one)
- [ ] Exercises from [16-phase-7-agent-mvp.md](16-phase-7-agent-mvp.md): comparing models, prompt injection

## Phase 8. Safe actions and guardrails

**Understand:** how to give an agent rights without letting it break the cluster.

- [x] Actions `restart_deployment` (Auto) and `delete_pod` (Approve); the model only proposes them in a JSON field — see [17](17-phase-8-actions-guardrails.md)
- [x] Guardrails in code: allowlist, target check, kill switch (ConfigMap), limits per run and per window, cooldown, circuit breaker, no-repeat, preconditions
- [x] Modes `DryRun` (default; `dryRun=All` in the API) → `Approve` (`POST /approvals/{id}`, timeout = "no") → `Auto`
- [x] RBAC (patch deployments, delete pods, get one ConfigMap) + ValidatingAdmissionPolicy (only the restart annotation, only Deployment pods); verified with `--as`
- [x] An audit record for each step before and after; the "Actions" section of the report is built from the record, not from the model's text
- [x] Effect verification (1m 5xx + unready pods, before/after) → Improved / Unchanged / Degraded; Degraded → `Escalated`
- [x] Ralph loop with actions: RunState remembers actions, a useless repeat is forbidden; current symptom (after the end-to-end run); 53 harness tests
- [x] End-to-end scenario in Auto: no actions while the database is down; afterwards a restart of orders-api — "stale data" and "post hoc" in the effect assessment were found and fixed
- [ ] Exercises from [17](17-phase-8-actions-guardrails.md): kill switch, protected targets, prompt injection, a worsening action

## Phase 9. Chaos engineering

**Understand:** steady state, hypothesis, blast radius, game day.

- [x] Chaos Mesh 2.8.7 (only the `sandbox` namespace): pod-kill, network-delay, CPU stress — hypotheses tested, one refuted — see [18](18-phase-9-chaos-engineering.md)
- [x] Our own chaos tool in .NET: `src/Chaos.Monkey` (a random pod, only `sandbox`, never the last ready one, dry run by default)
- [x] "Bad modes" in the sandbox services: latency, 500 errors, memory leak — `faults:` in values, no pod restart
- [x] Agent 0.5.0: an incident declared by a human (`POST /runs {"incident": true}`); the steady state includes ready pods and the absence of restarts
- [x] Scenarios and agent evaluation: `scripts/game-day.ps1` + `infra/chaos/scenarios.json` → scorecard
- [x] Game day: 5 scenarios with analysis; the failed one (database latency) was repeated 3 times: 0/3 → 2/3 thanks to the Retrieve data (latency, event age)
- [x] Finding: mesh cold start after a Docker Desktop restart → `scripts/recover-mesh.ps1`
- [ ] Exercises from [18](18-phase-9-chaos-engineering.md)

## Phase 10. Polish

- [x] One script brings everything up from scratch: `scripts/up.ps1` + `scripts/smoke-test.ps1` (read-only checks); verified `cluster-down` → `up.ps1` — 8.5 min — see [19](19-phase-10-polish-and-lessons.md)
- [x] GitOps (Argo CD) — described (how it would fit releases, where it would meet agent actions and chaos), not adopted
- [x] Final documentation: what was learned, what to do differently, where to go next

---

Next: see [20](20-phases-11-plus-plan.md) (not new phases of this roadmap, but a separate plan for "after").
