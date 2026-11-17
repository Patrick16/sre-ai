# sre-ai

A learning sandbox: a Kubernetes cluster set up "as in production", plus a .NET agent that
watches the system (metrics, logs, alerts), performs **safe** actions and writes reports.

The main goal of the project is not the result but understanding: to learn SRE and Kubernetes in practice,
and to design and implement an agent harness of our own. The agent works as a **Ralph loop**: it keeps
going until the task is solved. Each iteration is a **RALF** cycle:
Retrieve → Analyze → Lead → Feedback, see [docs/05-agent-architecture.md](docs/05-agent-architecture.md).

> The documentation is in English in `docs/`. The Russian course notes live in `docs-ru/`, which is local only
> (not tracked by git, see `.gitignore`).

## Repository layout

| Folder | Contents |
|---|---|
| [`docs/`](docs/00-overview.md) | Documentation (English): overview, roadmap, Kubernetes/SRE concepts, agent design, guardrails, chaos engineering, reporting, glossary |
| `infra/` | Everything for the cluster: cluster bootstrap (`cluster/`), Helm charts (`helm/`), raw k8s manifests (`manifests/`), chaos experiment configuration (`chaos/`) |
| `src/` | The .NET solution: agent core, tools, LLM clients, sandbox services |
| `scripts/` | Bring-up (`up.ps1`), checks (`smoke-test.ps1`), deploy, game day, mesh recovery |

## Where to start

1. [docs/00-overview.md](docs/00-overview.md) — what the project is and why it exists.
2. [docs/01-roadmap.md](docs/01-roadmap.md) — a step-by-step plan by phase: from an empty
   environment to chaos engineering and mTLS/service mesh. All other documents are tied to the roadmap phases.
3. [docs/glossary.md](docs/glossary.md) — if you run into an unfamiliar term.

## Technologies

- **Kubernetes**: kind (3 nodes), Deployments, StatefulSet, Services, Ingress (Traefik), ConfigMaps, Secrets,
  PV/PVC, Helm 4, Pod Security, default-deny NetworkPolicy, mTLS and a service mesh (Linkerd), ValidatingAdmissionPolicy.
- **Observability**: Prometheus, Grafana, Alertmanager (kube-prometheus-stack), Loki + Alloy, OpenTelemetry
  in the .NET services, SLOs with multi-window burn-rate alerts.
- **Agent**: .NET 10 on Microsoft Agent Framework, a local model `qwen/qwen3.5-9b` in LM Studio (or any
  OpenAI-compatible API). Ralph loop + RALF, facts and synthesis, two actions behind guardrails.
- **Chaos engineering**: Chaos Mesh, our own `Chaos.Monkey` in .NET, "bad modes" in the services, a game day with a scorecard.
- **Sandbox services**: frontend → orders-api → PostgreSQL, worker; deliberately simple.

## Quick start

You need Docker Desktop, kind, kubectl, Helm 4, .NET SDK 10, PowerShell 7 ([docs/09](docs/09-dev-environment.md)).

```powershell
./scripts/up.ps1           # everything from scratch in one command (~20-30 min with warm caches), ends with a smoke test
./scripts/smoke-test.ps1   # check that everything works (at any time)
./scripts/recover-mesh.ps1 # after restarting Docker Desktop or the laptop sleeping, if the smoke test complains about the mesh or certificates
./scripts/game-day.ps1     # chaos scenarios + agent evaluation
./scripts/cluster-down.ps1 # delete the cluster (and all data in it)
```

| UI | Address |
|---|---|
| Application | https://sandbox.localhost |
| Grafana | https://grafana.localhost (`admin`, password: `./scripts/grafana-password.ps1`) |
| Prometheus | https://prometheus.localhost |
| Alertmanager | https://alertmanager.localhost |
| Linkerd Viz | `kubectl port-forward -n linkerd-viz svc/web 8084:8084` → http://localhost:8084 |
| SRE agent | `kubectl port-forward -n sre-agent svc/sre-agent 8099:80` → `POST /runs`, `GET /runs/{id}/report`, `GET /actions`, `/approvals` |

The browser does not know our CA: either use `curl.exe --ssl-no-revoke --cacert .local/sandbox-root-ca.crt ...`, or import
`.local/sandbox-root-ca.crt` into your trusted roots yourself — that is your decision.

## Status

**All 10 phases are done** (the exercises at the end of each phase document are for self-study). Phase map:

| Phase | Document |
|---|---|
| 0–1. Environment, Kubernetes basics | [09](docs/09-dev-environment.md), [10](docs/10-phase-1-kubernetes-basics.md) |
| 2. Ingress and TLS | [11](docs/11-phase-2-ingress-tls.md) |
| 3. Helm | [12](docs/12-phase-3-helm.md) |
| 4. Data storage | [13](docs/13-phase-4-storage.md) |
| 5. Observability | [14](docs/14-phase-5-observability.md) |
| 6. NetworkPolicy, mTLS, mesh | [15](docs/15-phase-6-network-security.md) |
| 7. Agent MVP | [16](docs/16-phase-7-agent-mvp.md) |
| 8. Safe actions | [17](docs/17-phase-8-actions-guardrails.md) |
| 9. Chaos engineering | [18](docs/18-phase-9-chaos-engineering.md) |
| 10. From scratch, GitOps, lessons | [19](docs/19-phase-10-polish-and-lessons.md) |
