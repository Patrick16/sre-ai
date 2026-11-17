# 10. Phase 1: basic Kubernetes in practice

The theory for each object is in [03-kubernetes-concepts.md](03-kubernetes-concepts.md).
Here — what was exactly deployed, how it works, and what you can touch by hand.

> **Since Phase 3 these raw manifests have been replaced by a Helm chart** ([12-phase-3-helm.md](12-phase-3-helm.md)).
> The files themselves remain in git history — looking at them is just as instructive:
> `git show 8a47ee6:infra/manifests/phase-1/orders-api.yaml` (and also `frontend.yaml`, `worker.yaml`, `00-namespace.yaml`).
> To repeat the phase "by hand", you can apply them to a clean cluster:
> `git show 8a47ee6:infra/manifests/phase-1/orders-api.yaml | kubectl apply -f -`.
> The objects and their behaviour in the cluster are the same, so the exercises below remain relevant.

## What is deployed

```
 kubectl port-forward ──► Service frontend (ClusterIP :80)
                               │ selects pods by label app.kubernetes.io/name=frontend
                               ▼
                         frontend × 2 ──HTTP + X-Api-Key──► Service orders-api (:80)
                                                                 ▼
                                                           orders-api × 3   (orders in the pod's memory!)
                                                                 ▲
                         worker × 1 ──every 5 s: pending? → process──┘   (the worker has no Service)
```

| Object | File | What it teaches |
|---|---|---|
| Namespace `sandbox` + Pod Security `restricted` | [namespace-sandbox.yaml](../infra/manifests/platform/namespace-sandbox.yaml) | Isolation; the API server will not admit a pod that runs as root |
| Deployment ×3, Service ×2 | `frontend.yaml`, `orders-api.yaml`, `worker.yaml` | Replicas, rolling update, a stable DNS name |
| ConfigMap → **env** | `frontend-config`, `worker-config` | The value is read once, at container start |
| ConfigMap → **file** | `orders-api-config` | The file is updated in a running pod (~1 min) |
| Secret → env and → file | `sandbox-api-key` (created by the script) | Secrets are not in git; two ways of attaching them |
| Probes, requests/limits, securityContext | all Deployments | Self-healing, scheduling, OOMKilled, security |

The service code is in `src/Sandbox.Services/`, with one shared [Dockerfile](../src/Sandbox.Services/Dockerfile) (multi-stage).

## Deploy

```powershell
./scripts/cluster-up.ps1        # if the cluster does not exist yet
./scripts/build-images.ps1      # docker build + kind load for the 3 images
./scripts/deploy-infra.ps1      # namespace, Traefik, cert-manager (from Phase 3)
./scripts/deploy-apps.ps1       # secret + Helm releases of the services (from Phase 3)
kubectl port-forward -n sandbox svc/frontend 8080:80
```

```powershell
Invoke-RestMethod -Method Post "http://localhost:8080/order?item=book"
Invoke-RestMethod http://localhost:8080/
```

To understand the mechanics, do at least once by hand what the Phase 1 script did (on a clean cluster):
`kubectl apply -f infra/manifests/platform/namespace-sandbox.yaml`, then
`kubectl create secret generic sandbox-api-key -n sandbox --from-literal=api-key=<anything>`,
then `git show 8a47ee6:infra/manifests/phase-1/orders-api.yaml | kubectl apply -f -` (and the same for `frontend.yaml`, `worker.yaml`)
— and watch `kubectl get pods -n sandbox -w`.

## Notes on the key parts of the manifests

**`selector` and `labels`.** A Deployment and a Service "find" their pods only by labels.
A Deployment's `selector` is immutable after creation, so it contains only the stable label
`app.kubernetes.io/name`, while `app.kubernetes.io/version` is only in the pod template.

**Image tag `0.1.0`, not `latest`.** For `:latest` Kubernetes by default sets
`imagePullPolicy: Always` and tries to download the image from the registry — kind does not have it there → `ErrImagePull`.

**`maxUnavailable: 0, maxSurge: 1`** (orders-api). During an update a new pod is started first,
and only when it is Ready is an old one stopped — capacity never drops below 3.

**`topologySpreadConstraints`.** We ask to spread the orders-api replicas across different nodes.
`ScheduleAnyway` is a soft requirement: if that is not possible, the pod still starts.

**Probes.** `startupProbe` gives up to 60 s to start; after that `liveness` (restart on a hang)
and `readiness` (removal from load balancing) work. The worker's liveness checks **whether its loop is running**, not
whether orders-api is available: otherwise an orders-api outage would cause pointless restarts of the worker.

**securityContext.** `runAsNonRoot`, `readOnlyRootFilesystem`, `drop: [ALL]`, `seccompProfile` —
exactly what the `restricted` profile requires. The root filesystem is read-only, so `/tmp` is a separate `emptyDir`.

**Secret not in git.** The script ([deploy-apps.ps1](../scripts/deploy-apps.ps1), from Phase 3) generates a random key
if the Secret does not exist yet. Later it can be replaced with Sealed Secrets, so the secrets are stored encrypted in the repository.

## What we found during verification (and why it matters)

These effects were actually observed on the first run — this is not theory.

### 1. "Balancing" does not balance
All requests went to **one** frontend pod and **one** orders-api pod. There are two reasons:
- `kubectl port-forward svc/frontend` picks one pod and keeps a tunnel to it — the Service does not take part here.
- HttpClient keeps a **keep-alive** connection, and kube-proxy balances **connections (L4)**, not requests.
  The pod chosen once receives all subsequent requests over that connection.

In production this is solved by L7 balancing per request — exactly what a service mesh (Phase 6) or an Ingress (Phase 2) gives.

### 2. Each replica has its own state
Orders are stored in the pod's memory. After 4 orders were created:
```
orders-api-...-ppqzn -> 0 orders
orders-api-...-qth9b -> 4 orders
orders-api-...-zjrjt -> 0 orders
```
and the worker polled `ppqzn` and honestly wrote `Polled orders-api-...-ppqzn: 0 pending`. Plus, after any
restart or rollout all orders disappear. Conclusion: **stateless replicas must not hold state** —
this is why Phase 4 exists (PostgreSQL on a StatefulSet + PVC).

### 3. .NET protects itself from the memory limit
The first version of `/debug/allocate` allocated managed arrays — and instead of OOMKilled got HTTP 500
(`OutOfMemoryException`). The .NET runtime knows about the container's limit and caps the GC heap (~75% of the limit).
So the endpoint now allocates **native** memory, which the GC does not control — and then the kernel kills the
container: `lastState=OOMKilled`. In reality, OOMKilled in .NET services most often means a leak of
unmanaged memory or a limit that is too small for the load.

### 4. The ConfigMap file updated in 70 seconds
After `kubectl patch configmap orders-api-config` the value in `/info` changed after ~70 s, with 0 restarts.
The kubelet syncs volumes with a period of about a minute. For env variables (frontend) this never happens —
you need `kubectl rollout restart`.

## Exercises

**Project snapshot at this phase** — the tag `phase-1` (commit `8a47ee6`, raw manifests, no Helm). Needed
when an exercise no longer reproduces on the current project: for example, item 3 (orders live on only one pod)
works only while orders are in memory — since Phase 4 they are in PostgreSQL. The snapshot was not re-run to verify — it is the
state the exercises were composed for.

```powershell
git worktree add ../sre-ai-phase-1 phase-1   # a separate folder; your working copy stays unchanged
cd ../sre-ai-phase-1
./scripts/cluster-down.ps1                   # the cluster name is shared (sre-ai): delete the current one first
./scripts/cluster-up.ps1
./scripts/build-images.ps1
./scripts/deploy-phase1.ps1
# ... exercises ...
cd ../sre-ai; git worktree remove ../sre-ai-phase-1 --force
./scripts/reset-exercise.ps1 -Level Cluster -Yes   # rebuild the current project from scratch
```

**Preconditions and rollback.** You need a green `./scripts/smoke-test.ps1`; the new features from
[20](20-phases-11-plus-plan.md) (if you switched them on) must be off — otherwise the numbers in "What to expect" may
differ. The exercises break the applications (a deleted Secret, a bad image, a stopped node) — to restore
the state from which they make sense:

```powershell
./scripts/reset-exercise.ps1          # ~2-3 min: starts stopped nodes, restores the releases from the values files
```

| # | Do | What to expect |
|---|---|---|
| 1 | `kubectl delete pod -n sandbox <orders-api-pod>` and `kubectl get pods -n sandbox -w` | Almost at once a new pod with another name; the ReplicaSet keeps 3 |
| 2 | `kubectl scale deploy/orders-api -n sandbox --replicas=5`, then back to 3 | Where the new pods ended up: `-o wide` |
| 3 | Create a couple of orders and open each orders-api pod with `kubectl port-forward pod/<name> 8081:8080` (`GET /orders` with the `X-Api-Key` header) | Orders exist only on one pod (effect 2) |
| 4 | Change `Frontend__Greeting` via `kubectl edit configmap frontend-config -n sandbox` | The answer does not change until you run `kubectl rollout restart deploy/frontend -n sandbox` |
| 5 | Change `Banner` in `orders-api-config`, wait ~1 min, `GET /info` on a pod | It changes without a restart (effect 4) |
| 6 | `kubectl get secret sandbox-api-key -n sandbox -o jsonpath='{.data.api-key}'` and decode the base64 | A Secret is **not encryption**, it is base64; only RBAC protects it |
| 7 | Via port-forward to an orders-api pod: `POST /debug/allocate?mb=300` with the `X-Api-Key` header (after the 2026-11-10 review the endpoint is behind the key, `mb` from 1 to 512) | `kubectl describe pod` → `Last State: Terminated, Reason: OOMKilled`, `Restart Count: 1` |
| 8 | A "bad release": `kubectl set image deploy/orders-api orders-api=sandbox/orders-api:0.9.9 -n sandbox` | A new pod in `ErrImagePull`/`ImagePullBackOff`, the old 3 keep working (thanks to `maxUnavailable: 0`). Rollback: `kubectl rollout undo deploy/orders-api -n sandbox` |
| 9 | `kubectl rollout history deploy/orders-api -n sandbox` | The revision history — what the agent will later use for a rollback |
| 10 | Delete the Secret: `kubectl delete secret sandbox-api-key -n sandbox`, then delete a frontend pod | A new pod in `CreateContainerConfigError` — the Secret is needed at start. Restore: `./scripts/deploy-apps.ps1` |
| 11 | `docker stop sre-ai-worker2`, wait ~5 min | The node is `NotReady`; pods on it are recreated on other nodes after a while. Why it takes so long — see the `tolerations` for `node.kubernetes.io/unreachable` in `kubectl get pod -o yaml` |

## Debugging cheat sheet

| Symptom | Where to look | Common cause |
|---|---|---|
| `Pending` | `kubectl describe pod` → Events | Not enough resources for the requests, no node fits |
| `ErrImagePull` / `ImagePullBackOff` | Events | No image in kind (`kind load`), the `latest` tag, a typo |
| `CreateContainerConfigError` | Events | No ConfigMap/Secret, or the key is missing in it |
| `CrashLoopBackOff` | `kubectl logs <pod> --previous` | The application crashes at startup |
| `OOMKilled` | `kubectl describe pod` → Last State | The memory limit was exceeded |
| Running, but not Ready | `kubectl describe pod` → readiness failures | The readiness probe does not pass |
| A pod gets no traffic | `kubectl get endpointslices -n sandbox` | The pod's labels do not match the Service's selector |
| Pod Security violation | The `kubectl apply` response / ReplicaSet events | No `runAsNonRoot`, `seccompProfile`, etc. |

The universal rule: `kubectl get events -n sandbox --sort-by=.lastTimestamp` — the answer is almost always there.
