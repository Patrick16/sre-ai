# Chaos experiments (Phase 9)

Chaos Mesh CRDs, applied by `scripts/game-day.ps1` or by hand (`kubectl apply -f`; `kubectl delete -f` stops one
early). Every experiment: only namespace `sandbox` (the controller refuses others, see
`infra/helm/values/chaos-mesh.yaml`), one target, a `duration` - it ends by itself even if nobody deletes it.

| File | Fault | Hypothesis |
|---|---|---|
| `pod-kill-orders-api.yaml` | kill 1 of 3 orders-api pods | the mesh retries GETs, the Deployment replaces the pod: SLIs stay < 1%, no alert |
| `network-delay-postgres.yaml` | +300 ms on every packet leaving postgres | orders-api latency SLO (< 250 ms) burns; the cause is the database, not orders-api |
| `cpu-stress-orders-api.yaml` | CPU stress inside every orders-api container | was: "throttling at the 500m limit -> latency grows". **Refuted**: 100% of CFS periods throttled, 0% slow requests at ~5 rps - throttling is not user impact |

App-level faults (500s, latency, memory leak) are not here: they are built into the services
(`src/Sandbox.Services/Shared/Faults.cs`) and switched with `helm upgrade ... --set-json faults.errorRatio=0.3`.
