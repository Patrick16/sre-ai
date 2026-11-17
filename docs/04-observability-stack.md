# 04. Observability: metrics, logs, alerts

> The practice (what is deployed, how it is verified) is in [14-phase-5-observability.md](14-phase-5-observability.md).
> In the project: the log collector is Grafana Alloy (Promtail was retired in March 2026); the orders-api SLO is 99% faster than 250 ms.

## Why

SRE starts with the question "how do we know the system works?". The answer is telemetry.
Three kinds of signals ("three pillars"):

| Signal | What it is | Question it answers | Tool in the project |
|---|---|---|---|
| **Metrics** | Numbers over time (RPS, latency, % errors, memory) | *What* is happening and *how bad* it is | Prometheus |
| **Logs** | Text events with context | *Why* it happened | Loki |
| **Traces** | The path of one request through all services | *Where* exactly it slows down | (optional) Tempo + OpenTelemetry |

The agent uses all the available signals the way an on-call engineer does: alert → metrics → logs.

## Components

### Prometheus
- **Pull model**: Prometheus itself makes HTTP requests to each service's `/metrics` every N seconds (a scrape).
- **What it needs to know**: whom to scrape. In Kubernetes this is described by the `ServiceMonitor`/`PodMonitor` CRDs
  (from prometheus-operator), not by hand.
- **PromQL** — the query language. Examples you will need:
  - `sum(rate(http_requests_total{namespace="sandbox"}[5m])) by (service)` — RPS per service
  - `histogram_quantile(0.99, sum(rate(http_request_duration_seconds_bucket[5m])) by (le, service))` — p99 latency
  - `kube_pod_container_status_restarts_total` — restarts (from kube-state-metrics)
- **API**: `GET /api/v1/query?query=...` — exactly how the agent gets metrics.

### Auxiliary exporters (they come with kube-prometheus-stack)
- **node-exporter** — node metrics (CPU, memory, disk).
- **kube-state-metrics** — the state of Kubernetes objects as metrics (replicas, restarts, pod statuses).
- **cAdvisor** (in the kubelet) — resource consumption by containers.

### Alertmanager
- Prometheus evaluates **alert rules** (`PrometheusRule`): "if p99 > 300 ms for 5 minutes — fire".
- Alertmanager receives the firing alerts and decides what to do with them: grouping,
  deduplication, inhibition, silences, routing to receivers.
- **In the project**: a `webhook` receiver → the agent's HTTP endpoint. This way an alert becomes the trigger of the Ralph loop, and its resolve becomes part of the completion criterion.

### Loki
- A log store that indexes only **labels** (namespace, pod, app), not the full text — which is why it is cheap.
- Grafana Alloy (a DaemonSet, one pod per node) collects the logs and sends them to Loki.
- **LogQL**: `{namespace="sandbox", app="orders-api"} |= "error" | json | level="Error"`.
- **Important for services**: write **structured logs** (JSON) — they are easier to filter, for both people and the agent.

### Grafana
- A UI on top of Prometheus and Loki: dashboards, Explore, correlation "metric → logs of the same pod for the same time".
- We keep dashboards as code (JSON in a ConfigMap with a label for the sidecar loader) — the same as in production.

## What we instrument in the .NET services

- Metrics: the OpenTelemetry .NET SDK + a Prometheus exporter (or `prometheus-net`). ASP.NET Core
  gives basic HTTP metrics almost for free.
- Logs: `Microsoft.Extensions.Logging` with a JSON console — Kubernetes collects stdout.
- Health endpoints for probes: `/health/live`, `/health/ready`.
- (Optional) traces via OpenTelemetry → Tempo.

## SLI, SLO, error budget

- **SLI** (indicator): a measurable quantity that reflects the user's experience. For example:
  "the share of requests to orders-api that succeed in under 300 ms".
- **SLO** (objective): "SLI ≥ 99% over 30 days".
- **Error budget**: 1% — how much you may "fail". If the budget is spent fast — freeze releases and fix reliability.
- **Burn-rate alerts**: alert not on every spike, but when the budget burns too fast
  (multi-window, multi-burn-rate). This is the mature approach from the SRE Book — we implement it in phase 5.
- **Golden signals** (Google SRE): latency, traffic, errors, saturation — the basic dashboard for every service.

## How the agent sees it

| Source | API | What the agent takes |
|---|---|---|
| Alertmanager | webhook (push) + `GET /api/v2/alerts` | What is firing now, the labels of the alert |
| Prometheus | `GET /api/v1/query`, `/query_range` | Metric values around the incident time |
| Loki | `GET /loki/api/v1/query_range` | Pod logs selected by the alert's labels |
| k8s API | KubernetesClient for .NET | Pod statuses, events, rollout history, resource descriptions |

Principle: the agent receives **compressed** data (aggregates, top errors, the last N lines),
not raw gigabytes — an LLM's context window is limited (especially for local models).
