# 00. Project overview

## Why this project

This is a learning sandbox. It has three goals, in increasing order of "interestingness":

1. **Kubernetes "as in production".** Bring up a local cluster and configure in it everything that
   usually shows up in a real production environment: Deployments, Services, Ingress,
   ConfigMaps, Secrets, Helm, persistent storage, NetworkPolicy, mTLS,
   a service mesh. Above all, understand *why* each piece is needed.
2. **SRE environment.** Set up monitoring (metrics, logs, alerts, dashboards),
   learn to formulate SLIs/SLOs, break the system in a controlled way (chaos engineering)
   and see how that shows up in telemetry.
3. **An AI agent for SRE.** Design and write, in .NET, our own agent harness:
   the agent collects the state of the system itself, reasons about it through an LLM, performs
   only safe actions and writes a report.

## What counts as success

- The cluster comes up with one command from `scripts/` and is deployed with Helm charts.
- Grafana shows metrics and logs of all services, and Alertmanager sends alerts.
- Traffic between services goes over mTLS (service mesh), and there is an Ingress from outside.
- A chaos experiment (for example, killing a pod or adding network latency) leads to an alert, the agent
  notices it, diagnoses it, performs a safe action (or proposes one) and writes a report (on the agent's PVC,
  `GET /runs/{id}/report`; game-day results go to `.local/game-day/`).
- The project owner can explain every component in their own words.

## What is in scope and what is not

| In scope | Not in scope |
|---|---|
| A local cluster (kind/minikube) | Managed cloud clusters (EKS/AKS/GKE) — possible later as a bonus |
| Simple sandbox services in .NET | Real business logic |
| An agent with an allowlist of safe actions | An agent with arbitrary access to the cluster |
| LLMs via an OpenAI-compatible API / LM Studio | Training or fine-tuning our own models |
| Chaos engineering inside the sandbox | Chaos on anything other than the local cluster |

## Stack

| Layer | Technology | Why |
|---|---|---|
| Cluster | kind (Kubernetes in Docker) | A multi-node cluster locally, quick to recreate |
| Packaging | Helm | De facto standard for deploying to Kubernetes |
| Ingress | Traefik | Supports both Ingress and Gateway API; ingress-nginx was retired in March 2026 |
| Certificates | cert-manager | Automatic TLS certificate issuance, as in production |
| Service mesh | Linkerd (main choice) | Simpler than Istio, mTLS out of the box — ideal for learning |
| Metrics | Prometheus + kube-prometheus-stack | The standard for monitoring in Kubernetes |
| Logs | Loki + Grafana Alloy | Integrates well with Grafana; Promtail was retired in March 2026 |
| Dashboards | Grafana | One place to view metrics and logs |
| Alerts | Alertmanager | Alert routing, a webhook into the agent |
| Chaos | Chaos Mesh + our own .NET tools | Kubernetes-native experiments + understanding "from the inside" |
| Agent and services | .NET 10, C# | A project requirement |
| LLM | OpenAI-compatible API / LM Studio | Works both in the cloud and fully offline |

Concrete versions are pinned in the phase where a component is first installed (see the roadmap).

## How to read the documentation

- [01-roadmap.md](01-roadmap.md) — the order of work. Start here.
- [02-architecture.md](02-architecture.md) — how everything fits together.
- [03](03-kubernetes-concepts.md), [04](04-observability-stack.md) — theory on Kubernetes and observability.
- [05](05-agent-architecture.md), [06](06-safe-actions-and-guardrails.md), [08](08-reporting-and-audit.md) — agent design.
- [07-chaos-engineering.md](07-chaos-engineering.md) — how to break the system.
- [glossary.md](glossary.md) — a glossary of terms.
