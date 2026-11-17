# 14. Phase 5: observability in practice — metrics, logs, SLO, alerts

The theory — [04-observability-stack.md](04-observability-stack.md). Here — what was deployed, how it is connected, and what
the verification on a live cluster showed.

## What is deployed

| Component | Version / chart | Namespace | Role |
|---|---|---|---|
| kube-prometheus-stack | 93.0.0 | `monitoring` | Prometheus Operator, Prometheus (PVC 5Gi, retention 5d — longer than the longest SLO window, 3d), Alertmanager, Grafana, node-exporter, kube-state-metrics, ready-made rules and dashboards for Kubernetes |
| Loki | grafana/loki 7.5.0, SingleBinary mode | `monitoring` | Log storage (files on a PVC, retention 72h) |
| Grafana Alloy | grafana/alloy 1.15.0, DaemonSet | `monitoring` | Collects the logs from `/var/log/pods` on each node → Loki |
| [sandbox-observability](../infra/helm/sandbox-observability/) | our chart | `monitoring` | Dashboards as code |
| [loadgen](../infra/helm/loadgen/) | our chart, k6 2.3.0 | `sandbox` | Steady ~5 rps through Traefik |
| sre-agent (a stub) | our `sandbox-service` | `sre-agent` | Accepts the Alertmanager webhook, logs it, `GET /alerts` |
| postgres-exporter | v0.20.1, a sidecar in the postgres chart | `sandbox` | Database metrics (`pg_up`, connections, size) |

| UI | Address | Access |
|---|---|---|
| Grafana | https://grafana.localhost | `admin`, password: `./scripts/grafana-password.ps1` |
| Prometheus | https://prometheus.localhost | no authentication |
| Alertmanager | https://alertmanager.localhost | no authentication |

> Prometheus and Alertmanager without authentication is acceptable **only** because kind now listens on ports 80/443
> exclusively on `127.0.0.1` (`listenAddress` in [kind-config.yaml](../infra/cluster/kind-config.yaml)). Before Phase 5
> they were open on `0.0.0.0` — that is, the whole local network. In production such UIs sit behind oauth2-proxy/SSO.

```powershell
./scripts/deploy-infra.ps1   # + kube-prometheus-stack, Loki, Alloy, dashboards (first — for the CRDs)
./scripts/build-images.ps1
./scripts/deploy-apps.ps1    # + loadgen, sre-agent
```

## How it all fits together

```
                          ┌──────────── pull every 15 s ──────────────┐
 .NET services :9090/metrics ◄── PodMonitor ──┐                       │
 postgres-exporter :9187    ◄── ServiceMonitor┤◄── Prometheus Operator ─┤──► Prometheus ──► PrometheusRule (SLO, PostgresDown,
 Traefik, cert-manager      ◄── ServiceMonitor┘    (generates           │       │                  + ~150 ready-made k8s rules)
 kubelet/cAdvisor, node-exporter, kube-state-metrics   the scrape config)│       ▼
                                                                         │   Alertmanager ──webhook──► sre-agent /webhooks/alertmanager
 stdout of all containers ─► /var/log/pods on the node ─► Alloy (DaemonSet) ─► Loki        │
                                                                                       ▼
                                          Grafana ◄── Prometheus + Loki (dashboard Sandbox / Golden signals)
```

## Key decisions

| Decision | Why |
|---|---|
| Metrics through the **OpenTelemetry** .NET SDK + the Prometheus exporter (`1.20.1-beta.1`) | Standard names (semantic conventions): `http_server_request_duration_seconds` with `http_route`, `http_response_status_code`. The exporter is still beta — a deliberate choice: the official OTel path to the Prometheus pull model |
| The built-in .NET meters, not our own | ASP.NET Core, Kestrel, HttpClient, the runtime (GC/threads) and Npgsql (the connection pool) already publish metrics — `AddMeter(...)` is enough |
| `/metrics` on a **separate port 9090** | Otherwise the Ingress `/api/*` would expose the orders-api metrics to the outside. The check uses the real connection port (`Connection.LocalPort`), not the `Host` header, which the client controls |
| JSON logs through `Logging:Console:FormatterName=json` | No code change; Alloy extracts `LogLevel` into the `level` label |
| **Alloy**, not Promtail | Promtail reached end-of-life in March 2026 |
| The Alloy DaemonSet with a **toleration** for the control plane | Otherwise the Traefik logs (it runs on the control-plane node) would silently not be collected |
| `*SelectorNilUsesHelmValues: false` | By default Prometheus sees only the ServiceMonitors with its own release's label, and **silently ignores** the others |
| etcd/scheduler/controller-manager/kube-proxy are not scraped | In kind they listen on 127.0.0.1 inside the node; on EKS/GKE they cannot be scraped at all. Otherwise — eternal "target down" |
| The `monitoring` namespace — Pod Security `privileged` | node-exporter and Alloy read the host (`hostNetwork`, `/proc`, `/var/log`). Infrastructure only, no applications |
| `NodeClockNotSynchronising/SkewDetected` disabled | The kind nodes are containers on the host's clock (WSL2), and the NTP status "flickers" falsely |
| SLOs and alerts — **in the service's chart** (`slo:` in the values) | The service's owner owns its SLO; the same rules for all services come from the template |

## SLO and burn-rate alerts

**SLI** = the share of "good" requests: not 5xx **and** faster than the threshold. **SLO** = 99% (the error budget is 1%).

| Service | Threshold | Why this one |
|---|---|---|
| orders-api | 250 ms | `0.25` — a bucket boundary of the ASP.NET Core histogram |
| frontend | 500 ms | includes the wait for orders-api |

**The SLO threshold must match a bucket boundary** (`le="0.25"`). Originally the roadmap said "300 ms", but there is no such
bucket — it is impossible to compute "the share of requests faster than 300 ms" from this histogram.

Recording rules `slo:sli_error:ratio_rate{5m,30m,1h,2h,6h,1d,3d}` and four alerts (Google SRE Workbook, multi-window multi-burn-rate):

| Alert | Long window | Short window | Burn rate | `for` | Severity | Meaning |
|---|---|---|---|---|---|---|
| `…ErrorBudgetBurnFast` | 1h | 5m | 14.4 | 2m | critical | 2% of the monthly budget in an hour |
| `…ErrorBudgetBurnFast` | 6h | 30m | 6 | 15m | critical | 5% in 6 hours |
| `…ErrorBudgetBurnSlow` | 1d | 2h | 3 | 1h | warning | 10% in a day |
| `…ErrorBudgetBurnSlow` | 3d | 6h | 1 | 3h | warning | 10% in 3 days |

The long window means "this is significant", the short one means "this is still happening" (otherwise the alert would keep firing for an hour after the fix).

Two PromQL traps, handled in the [template](../infra/helm/sandbox-service/templates/prometheusrule-slo.yaml):
- **`or vector(0)`**: when all "good" requests are gone entirely, the sum over them is an empty vector, and the whole expression
  is empty too → **the alert stays silent exactly when it is most needed**.
- **`http_route=~".+"`**: `/metrics` scrapes and unknown addresses (404) get into the histogram without a route label;
  without the filter they would "dilute" the SLO with good requests.

A similar trap in `PostgresDown`: `pg_up == 0` catches "the exporter is alive, the database is not", but if the database pod
disappears entirely, there is no series at all — `absent(pg_up{...})` is needed.

## What the verification showed

### 1. End-to-end scenario: the database goes down → alert → agent
Under constant load: `helm upgrade postgres --set replicas=0`.

| Time | Event |
|---|---|
| 0:00 | The database is stopped; orders-api answers 503, frontend — 502 |
| 0:32 | `PostgresDown` — pending |
| 1:18 | `OrdersApiErrorBudgetBurnFast` (1h) and `FrontendErrorBudgetBurnFast` (1h) — pending |
| **1:33** | `PostgresDown` firing → **delivered to sre-agent** |
| **3:20** | Both fast-burn alerts firing → **delivered to sre-agent** (the share of bad requests: 53% over 5m, 44% over 1h) |
| +0:03 after the return | The service answers 200 again |
| +0:50 | `PostgresDown` resolved → to the agent |
| +5:41 | Fast-burn alerts resolved → to the agent (the 5m short window "cooled down") |

The agent received both symptoms — the cause (`PostgresDown`) and the consequence (the burn rate of two SLOs). Linking them is the task
of the RALF iteration in Phase 7.

### 2. All scrape targets are alive, all dashboard queries return data
34 targets — `up`; 18 queries of the dashboard "Sandbox / Golden signals" — non-empty (except the 5xx on Traefik — there were none).

### 3. Monitoring the monitoring
`AlertmanagerFailedToSendAlerts` fired by itself: the platform is installed before the applications, and for some time
Alertmanager sent webhooks to the agent, which did not exist yet. The alert pipeline watches itself — and it worked.
Similarly, the always-firing `Watchdog` proves that the chain Prometheus → Alertmanager is alive (it is routed to `null`).

### 4. Slow burn-rate alerts on a young cluster
After a 5-minute outage the alerts for the 6h/1d/3d windows went into pending: with half an hour of history, 5 minutes of outage is a huge share
even of the "three-day" window. The SLO arithmetic assumes the window is filled with data; on a fresh cluster the long windows
greatly overstate the error share, and the warning alert may arrive several hours after the experiment.

### 5. Small things found along the way
| What | Cause | Fix |
|---|---|---|
| `RequireHost("*:9090")` did not protect the port | It compares the `Host` **header**, not the socket port | `UseOpenTelemetryPrometheusScrapingEndpoint(ctx => ctx.Connection.LocalPort == 9090 …)` |
| orders-api stderr: `libgssapi_krb5.so.2: cannot open shared object file` | Npgsql by default tries Kerberos/GSSAPI encryption | `GssEncryptionMode = Disable` (orders-api 0.3.1) |
| 2 Information lines from HttpClient per frontend request | Logging of `System.Net.Http.HttpClient` at the default level | The `Warning` level for this category |
| loadgen: `unknown flag: --no-summary` | k6 v2 renamed the flag | `--summary-mode=disabled` |
| Grafana `Progress deadline exceeded`, Alloy pulled for 14 minutes | Slow image downloads (Grafana ~500 MB) into each node separately | Pull-through registry caches ([registry-cache.ps1](../scripts/registry-cache.ps1)), see [09](09-dev-environment.md) |

## Exercises

**Project snapshot at this phase** — the tag `phase-5` (commit `c3d09cc`; the agent there is still a webhook stub).
It was not re-run to verify — it is the state the exercises were composed for.

```powershell
git worktree add ../sre-ai-phase-5 phase-5   # a separate folder; your working copy stays unchanged
cd ../sre-ai-phase-5
./scripts/cluster-down.ps1                   # the cluster name is shared (sre-ai): delete the current one first
./scripts/cluster-up.ps1
./scripts/build-images.ps1
./scripts/deploy-infra.ps1
./scripts/deploy-apps.ps1
# ... exercises ...
cd ../sre-ai; git worktree remove ../sre-ai-phase-5 --force
./scripts/reset-exercise.ps1 -Level Cluster -Yes   # rebuild the current project from scratch
```

**Preconditions and rollback.** You need a green `./scripts/smoke-test.ps1`; the optional "noisy" alerts from
[20](20-phases-11-plus-plan.md), if you installed them, must be removed — otherwise the picture in Alertmanager will differ.
Rollback:

```powershell
git restore infra/                      # if you edited the values (items 8, 9)
./scripts/reset-exercise.ps1            # postgres/orders-api back from the values
```

The script does not remove the silence from item 7 — end it in the Alertmanager UI (Silences → Expire).

| # | Do | What to expect |
|---|---|---|
| 1 | Open Grafana → Sandbox / Golden signals, switch `Service` | Traffic, errors, latencies, saturation, burn rate, the logs of the chosen service |
| 2 | Prometheus → Status → Targets; then `up == 0` in Graph | All targets, and what a failed one looks like |
| 3 | `histogram_quantile(0.99, sum by (le) (rate(http_server_request_duration_seconds_bucket{app="orders-api"}[5m])))` | The p99 from the histogram; try it without `sum by (le)` — why the result is meaningless |
| 4 | Grafana → Explore → Loki: `{namespace="sandbox", app="orders-api"} \| json \| LogLevel="Warning"` | Filtering of structured logs by fields |
| 5 | Repeat the end-to-end scenario: `helm upgrade postgres infra/helm/postgres -n sandbox -f infra/helm/releases/sandbox/postgres.yaml --set replicas=0`, after 5 minutes `./scripts/deploy-apps.ps1 -Services postgres` | A timeline like item 1; watch the Alertmanager UI and the `sre-agent` logs |
| 6 | `POST /debug/allocate?mb=300` (with `X-Api-Key`) to an orders-api pod (Phase 1) | `KubePodCrashLooping`/restarts on the dashboard, OOMKilled in kube-state-metrics |
| 7 | Create a Silence in the Alertmanager UI on `PostgresDown` and repeat item 5 | The alert fires in Prometheus but does not reach the agent |
| 8 | Remove `or vector(0)` from the SLO template (locally, `helm template`) and think about what happens at 100% errors | An empty result → no alert |
| 9 | Add `await Task.Delay(300)` to `GET /orders`, bump the tag in `orders-api.yaml` (for example `0.3.2-slow`), `build-images` + `deploy-apps -Services orders-api`; then roll it back | Without a single 5xx, the SLO fires — on latency (the 250 ms threshold) |
| 10 | `kubectl port-forward -n sre-agent svc/sre-agent 8099:80`, then `GET http://localhost:8099/alerts` | The history of alerts the agent will see |

## Cheat sheet

```promql
up == 0                                                              # what is not being scraped
sum by (app) (rate(http_server_request_duration_seconds_count{namespace="sandbox", http_route=~".+"}[5m]))   # rps
slo:sli_error:ratio_rate1h / 0.01                                    # burn rate over an hour
kube_pod_container_status_restarts_total{namespace="sandbox"}       # restarts
pg_up                                                                # is the database alive?
```
```logql
{namespace="sandbox", level=~"Warning|Error"}                       # warnings and errors of all services
{namespace="sandbox", app="orders-api"} |= "Database unavailable"   # text search
```

| Symptom | Where to look | Common cause |
|---|---|---|
| A service is missing from Targets | `kubectl get podmonitor,servicemonitor -A`; the `prometheus-operator` logs | The selector did not match the labels; no `metrics` port; `*SelectorNilUsesHelmValues` |
| Target `down` | Targets → Last Error | A wrong port/path; the metrics listen on another port |
| A rule did not load | Status → Rules; `kubectl get prometheusrule -A` | A PromQL error; Prometheus has not re-read it yet (config-reloader, ~a minute) |
| An alert fires, but did not arrive | The Alertmanager UI → Silences/Inhibit; `AlertmanagerFailedToSendAlerts` | A route to `null`, a silence, an unavailable webhook |
| No logs of a pod | `{pod="..."}` in Loki; the Alloy pods on that node | The DaemonSet is not on this node (a taint without a toleration) |
| Empty values in the SLO | Query the recording rule in Graph | No traffic (0/0 = NaN) — you need loadgen |
