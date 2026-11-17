# 02. Architecture

## The big picture

```
                         ┌──────────────────────────── kind cluster ─────────────────────────────┐
  Browser / curl         │                                                                       │
  https://sandbox.localhost ► traefik ──► frontend ──► orders-api ◄── worker                     │
                         │      (TLS from          │  mTLS       │   mTLS       │                   │
                         │    cert-manager)      └─────── Linkerd sidecars ───┘                   │
                         │                                     │                                 │
                         │                                     ▼                                 │
                         │                               PostgreSQL (StatefulSet + PVC)          │
                         │                                                                       │
                         │  ── observability ─────────────────────────────────────────────────   │
                         │  Prometheus ◄── scrapes metrics of all pods, node-exporter, kube-state│
                         │  Loki       ◄── logs of all pods (via the log collector)              │
                         │  Alertmanager ◄── alert rules from Prometheus                         │
                         │  Grafana    ──► dashboards on top of Prometheus and Loki              │
                         │                                                                       │
                         │  ── chaos ─────────────────────────────────────────────────────────   │
                         │  Chaos Mesh / .NET chaos tool ──► breaks pods/network in namespace sandbox│
                         │                                                                       │
                         │  ── agent ─────────────────────────────────────────────────────────   │
                         │  sre-agent (.NET) ◄── webhook from Alertmanager / timer / manual runs │
                         │     │ reads: k8s API, Prometheus API, Loki API, Alertmanager API     │
                         │     │ acts: only through Guardrails → k8s API (narrow RBAC)          │
                         │     └──► reports (PVC / reports/)                                     │
                         └─────┼─────────────────────────────────────────────────────────────────┘
                               ▼
                 LLM endpoint (OpenAI-compatible): a cloud API  or  LM Studio on the host
```

## Namespaces

| Namespace | What is there | Why separate |
|---|---|---|
| `sandbox` | frontend, orders-api, worker, postgres | The system under test; the target for chaos and the agent |
| `traefik` | Ingress Controller | Incoming-traffic infrastructure |
| `cert-manager` | Certificate issuance | Infrastructure |
| `linkerd`, `linkerd-cni`, `linkerd-viz` | Mesh control plane, CNI plugin, dashboard | Infrastructure |
| `monitoring` | Prometheus, Grafana, Alertmanager, Loki | Observability must not fall over together with the application |
| `chaos-mesh` | Chaos Mesh | The tool, separate from the target |
| `sre-agent` | The agent | Its own ServiceAccount and RBAC, rights isolation |

Separation by namespace is at the same time organization, an RBAC boundary and a NetworkPolicy boundary. The agent
has the right to *act* only in `sandbox`.

## Sandbox services

Deliberately primitive .NET Minimal APIs:

- **frontend** — accepts a request, calls `orders-api`, returns the result.
- **orders-api** — CRUD for orders in PostgreSQL, exposes metrics and logs.
- **worker** — background processing (polls the DB or a queue).

Each service has **"fault modes"** (`src/Sandbox.Services/Shared/Faults.cs`), switched on by the `faults:` values of the chart
(a file from a ConfigMap, no pod restart): response delay, a percentage of 500 errors, a memory leak up to an OOMKill.
This gives controlled problems for the agent in addition to Chaos Mesh.

## The agent: components of the .NET solution

| Project | Responsibility |
|---|---|
| `SreAgent.Core` | Domain model (Observation, Finding, ActionProposal, Report), Ralph loop and RALF iteration, RunState, tool and LLM interfaces, guardrails |
| `SreAgent.Tools` | Tool implementations: k8s (KubernetesClient), Prometheus, Loki, Alertmanager, actions |
| `SreAgent.LlmClients` | OpenAI-compatible client (cloud and LM Studio), tool calling, retries |
| `SreAgent.Host` | Entry point: ASP.NET Core (webhook from Alertmanager, run API, console) + a BackgroundService (the run queue), DI, configuration |
| `Sandbox.Services/*` | The sandbox services under test |

Dependencies point inward: `Host → Tools, LlmClients → Core`. `Core` knows nothing about Kubernetes or a specific LLM — which
makes it possible to test the loop with fakes.

## Data flow

1. **Trigger**: Alertmanager sends a webhook, a timer fires (a periodic health check), or someone starts a run by hand.
2. **Retrieve**: the agent collects context: the alert, the state of pods, events, key metrics, fresh logs.
3. **Analyze**: the context and the available tools go to the LLM; the LLM may request more data
   (tool calls) — the cycle repeats a limited number of steps.
4. **Lead/Act**: if the LLM proposes an action, it passes the guardrails; depending on the mode it is executed, waits for
   confirmation, or is only recorded.
5. **Feedback**: a check of the result (did the metrics recover?), recording of the report and the audit log.

Details: [05-agent-architecture.md](05-agent-architecture.md).

## Where the agent runs

- **Inside the cluster** (the main mode, since Phase 7) — a Deployment in `sre-agent` with its own ServiceAccount
  and narrow RBAC. The agent receives webhooks from Alertmanager, runs started by hand through `POST /runs`, and the console.
  LM Studio on the host is reachable from kind via `host.docker.internal`.
- **On the host** (for development and debugging) — `dotnet run --project src/SreAgent.Host` with
  `ASPNETCORE_ENVIRONMENT=Development`; access to the cluster through kubeconfig, to Prometheus/Loki/Alertmanager
  through `scripts/port-forward.ps1`.
