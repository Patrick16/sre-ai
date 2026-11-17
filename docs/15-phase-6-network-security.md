# 15. Phase 6: network security — NetworkPolicy, mTLS, service mesh

The theory — the sections "NetworkPolicy", "TLS and mTLS", "Service Mesh" in [03-kubernetes-concepts.md](03-kubernetes-concepts.md).

## Two layers of protection

| Layer | What | What it knows about the client | What it gives |
|---|---|---|---|
| L3/L4 | **NetworkPolicy** (enforced by the CNI — kindnet) | The pod's IP → its **labels** | "who may connect to whom at all" |
| L7 | **Linkerd** (a sidecar proxy in each pod) | A **cryptographic identity** from the mTLS certificate (the ServiceAccount) | encryption, identity, metrics of each request, per-request load balancing, retries, timeouts |

They complement each other, and the verification showed why (see "What we found", item 2).

## NetworkPolicy: default-deny + explicit allowances

**The base level** — [networkpolicy-baseline.yaml](../infra/manifests/platform/networkpolicy-baseline.yaml)
for `sandbox` and `sre-agent`: deny everything incoming and outgoing, allow DNS (without it no name resolves — the most common way
to "break" a cluster with default-deny), allow the Linkerd proxies to reach the control plane and
to serve metrics (4191) and tap (4190).

**Each service declares in its own values** who calls it and whom it calls:

```
                      ┌──────────── traefik (ns) ─────────────┐
                      ▼                                       ▼
loadgen ──► traefik   frontend ──────────► orders-api ◄────── worker
(only 443)            (from: traefik;      (from: frontend, worker, traefik;
                       to: orders-api)      to: postgres)
                                               │
                                               ▼
                                            postgres (from: orders-api; to: nobody)
monitoring (ns) ──► :metrics, :4191 of all pods          sre-agent (from: the alertmanager pod — webhook)
```

```yaml
# infra/helm/releases/sandbox/orders-api.yaml
networkPolicy:
  allowFromApps: [frontend, worker]
  allowFromNamespaces: [traefik]
  allowToApps:
    - app: postgres
      port: postgres
```

Details that are easy to miss:
- **`allowFromNamespaces` lets in a whole namespace, `allowFromPods` — one pod in it** (`{ namespace: monitoring, app:
  alertmanager }`: a `namespaceSelector` and a `podSelector` in one element mean "both at once"). The agent needs the second
  form: its API has `POST /approvals` and `POST /runs`, and `monitoring` is a privileged namespace with a lot of other pods.
- **The port is the pod's port, not the Service's.** The policy sees the packet after the Service's DNAT: `orders-api:80` → pod `:8080`.
  That is why the rules use the **names** of the container ports (`http`, `postgres`), not 80.
- **Policies add up.** Traffic is allowed if *at least one* policy selecting the pod allows it. There are no deny rules —
  "deny" = the absence of an allowance, as long as at least one policy selects the pod.
- **The `helm test` pod got its own label** (`<release>-test`) and its own policy: with the application label it would fall under its rules.
- **Kubelet probes always pass**: traffic from the pod's own node is not limited by the policies.
- **NetworkPolicy works only if the CNI enforces it.** We checked separately: without a policy `200`, with deny-all — a timeout.

## Linkerd

### Why Linkerd, and which one
Linkerd is simpler and lighter than Istio — a good start for learning mesh. But **since February 2024 the open-source project publishes only
edge releases**; stable builds are produced by vendors (Buoyant Enterprise) ([releases](https://linkerd.io/releases/)).
We install `edge-26.11.1` (Helm charts `2026.11.1` from `helm.linkerd.io/edge`). For the third time in the project (after ingress-nginx and
Bitnami) the lifecycle of a dependency determines the choice — this is a normal part of an SRE's work.

### What is deployed

| Component | Namespace | Role |
|---|---|---|
| `linkerd-crds` (+ Gateway API CRDs) | `linkerd` | Server, AuthorizationPolicy, HTTPRoute… |
| `linkerd-cni` (DaemonSet) | `linkerd-cni` | Redirects a pod's traffic to the proxy at the time the pod's network is set up |
| `linkerd-control-plane` | `linkerd` | identity (the proxies' certificates), destination (discovery, balancing), policy, proxy-injector |
| `linkerd-viz` | `linkerd-viz` | Dashboard and tap on top of **our** Prometheus |
| PodMonitor `linkerd-proxy` | `monitoring` | Scrapes the metrics of all proxies ([podmonitor-linkerd.yaml](../infra/helm/sandbox-observability/templates/podmonitor-linkerd.yaml)) |

```powershell
kubectl port-forward -n linkerd-viz svc/web 8084:8084    # http://localhost:8084
```

### Identity: a certificate chain via cert-manager

```
linkerd-trust-anchor (the root, 10 years)  ──signs──►  linkerd-identity-issuer (intermediate CA, 48 h, renewed by cert-manager)
                                                            └─► linkerd-identity issues each proxy a certificate for 24 h
                                                                 named <serviceaccount>.<namespace>.serviceaccount.identity.linkerd.cluster.local
```

[linkerd-identity.yaml](../infra/manifests/platform/linkerd-identity.yaml). A root separate from the CA for the Ingress: different trust domains
must not share a root. The rotation of the intermediate certificate and of the proxies' certificates is automatic.

What was needed in practice: with `identity.externalCA: true` the chart **does not create** the ConfigMap `linkerd-identity-trust-roots`
(the control-plane pods hung in `CreateContainerConfigError`) — it expects an external tool to distribute the root. In production this is
**trust-manager** (a cert-manager project), which can keep both the old and the new root in a bundle during root rotation.
We have the ConfigMap created by [deploy-infra.ps1](../scripts/deploy-infra.ps1) from the cert-manager secret. And also: in production the
root's private key is not kept in the cluster at all (an offline CA, Vault, KMS).

### Compatibility with Pod Security `restricted`
Normal injection adds an init container with `NET_ADMIN`/`NET_RAW` (it writes iptables) — the `restricted` profile forbids that.
**linkerd-cni** does the same thing at the node level, when the pod's network is created, and only unprivileged containers remain in the pod.
Check: the pods in `sandbox` were admitted by the API server with the proxy inside.

### Native sidecar
The proxy is injected as a *native sidecar* — an init container with `restartPolicy: Always` (in `kubectl get pod` it is among the initContainers).
It starts **before** the application and stops **after** it — otherwise the application's `preStop` delay from Phase 3 would work
in vain: the proxy in front of it would no longer accept connections.

### How the mesh is switched on
The annotation `linkerd.io/inject: enabled` on the namespace (`sandbox`, `sre-agent`) — the application charts do not change. Existing pods
get a proxy only after being recreated (`kubectl rollout restart`). Traefik and monitoring are **not** in the mesh (exercise 3).

## Retries and timeouts: Gateway API HTTPRoute

[httproutes.yaml](../infra/helm/sandbox-service/templates/httproutes.yaml), enabled for orders-api (`mesh.routes.enabled`):

| HTTPRoute | Match | Policy |
|---|---|---|
| `orders-api-reads` | `method: GET` | up to 3 retries on 5xx, 1 s per attempt, 3 s for the whole request |
| `orders-api-all` | everything else | only a 3 s timeout, **no retries** |

- The parent of the route is a **Service**, not a Gateway (the GAMMA approach for "service → service" traffic).
- The retries are made by the proxy of the **caller** (frontend, worker). Requests from Traefik (outside the mesh) get none of this.
- **`POST` is not retried**: a retry of creating an order = a duplicate order. Only idempotent requests may be retried.
- As soon as a Service has at least one HTTPRoute, Linkerd passes **only** the described routes — hence the mandatory catch-all.

## What we found during verification

### 1. The NetworkPolicy really blocks
Temporary "impostor" pods:
```
[sandbox] as 'frontend' to postgres:5432       -> blocked
[sandbox] as 'worker'   to frontend:80         -> blocked
[sandbox] as 'loadgen'  to orders-api:80       -> blocked
[sandbox] as 'nobody'   to orders-api:80       -> blocked
[default] as 'nobody'   to orders-api.sandbox  -> blocked
[sandbox] as 'worker'   to orders-api:80       -> CONNECTED   (control)
[sandbox] as 'orders-api' to postgres:5432     -> CONNECTED   (control)
```

### 2. The NetworkPolicy trusts labels — and that is a hole
A pod with the label `app.kubernetes.io/name: worker` got the worker's rights **without being the worker**. Anyone who can create a pod in a
namespace can assign it any label. mTLS closes this: the identity is a certificate for a ServiceAccount, and it cannot be forged
with a label. For this, each service was given **its own ServiceAccount** (previously they were all `default`), without an API token.

### 3. mTLS and identity are visible in the proxy metrics
```
orders-api <- frontend.sandbox.serviceaccount.identity.linkerd.cluster.local   tls=true   4.63 rps
orders-api <- worker.sandbox.serviceaccount.identity.linkerd.cluster.local     tls=true   1.13 rps
postgres   <- orders-api.sandbox.serviceaccount.identity.linkerd.cluster.local tls=true   port=5432 open=4
frontend   <- (none)                                                           no_identity  ← Traefik is outside the mesh
```
The `helm test` pod appears as `default.sandbox.serviceaccount…` — it has no ServiceAccount of its own.

### 4. A trap: all traffic between proxies goes to port 4143
After injection orders-api stopped seeing the database (`/health/db: Unhealthy`, `GET /` → 502). 5432 is one of Linkerd's default "opaque" ports:
the client's proxy sends the stream not to 5432, but to the **inbound port of the server's proxy** — 4143. The policy
allowed only 5432. Then the same appeared for HTTP: a test pod in the mesh could not reach orders-api even though 8080 was
allowed — in the proxy log `endpoint{addr=…:8080}: Failed to connect`, while the real connection went to 4143.

The fix — open 4143 to the **same** "client → server" pairs that were already allowed. The price: whoever reaches 4143 can, through the proxy,
reach any port of the pod. From then on fine-grained access control is the job of Linkerd's identity-based authorization
(`Server` + `AuthorizationPolicy`, exercise 5), and the NetworkPolicy remains a coarse outer perimeter.

### 5. Per-request balancing — the effect of Phase 1 is gone
In Phase 1 each frontend pod "stuck" to one orders-api pod (kube-proxy balances connections). Now:
```
orders-api-…-hpcs6: 1.25 rps   orders-api-…-x98ht: 1.71 rps   orders-api-…-xn4d8: 1.69 rps   (from frontend only)
```
The Linkerd proxy chooses a pod for **each request** (EWMA by latency) on top of one HTTP/2 connection.

### 6. A hard kill of a pod — 0 errors even without retries
`kubectl delete pod --grace-period=0 --force` three times under load: **104 of 104** requests succeeded. A refused connection to a dead
proxy endpoint is handled before the request is sent, and another pod is chosen, while the destination controller quickly removes
the pod from the list. Without the mesh, the same scenario in Phase 3 lost requests.

### 7. Retries: what they can and cannot do
A restart of PostgreSQL (about 3 s of unavailability) under load, 2 runs:

| | User-visible errors |
|---|---|
| without retries | 3, 5 |
| with retries (GET, up to 3 repeats) | 2, 2 |

The frontend proxy's metrics for the same period: `retry_requests` 103, `retry_successes` 12, `retry_limit_exceeded` 19.
Retries smooth over short "blinks", but not a multi-second outage — they go out immediately, without a pause. And they **multiply the load**:
up to 4 requests to an already sick service for each user request. This is the mechanics of a *retry storm*; hence — a small limit,
only idempotent requests and a retry budget.

### 8. From scratch — 9 minutes
Recreating the cluster and a full run of `cluster-up → build-images → deploy-infra → deploy-apps`: **8 min 59 s**
(with warm image caches), all pods in the mesh at once, `helm test` of all three services passes. The order matters:
cert-manager → Linkerd identity → CRDs → CNI → control plane — and only then the applications, otherwise the pods would be created without a proxy.

### 9. Small things along the way
| What | Cause | Fix |
|---|---|---|
| `helm --set-file` did not find the file `E:Gitpet-projects…` | Helm treats `\` in a path as an escape | Forward slashes in the path |
| The schema rejected `getRetries.true` | YAML 1.1 reads the key `on:` as `true` | The key `retryOn` |
| 1 timeout out of 106 when the policies were switched on | Pods were recreated (the ServiceAccount changed) | — |
| The cluster "disappeared" after a PC restart | Docker Desktop was not running | Start Docker: the kind nodes and the caches come up by themselves |

## Exercises

**Project snapshot at this phase** — the tag `phase-6` (commit `2739c5a`). It was not re-run to verify —
it is the state the exercises were composed for.

```powershell
git worktree add ../sre-ai-phase-6 phase-6   # a separate folder; your working copy stays unchanged
cd ../sre-ai-phase-6
./scripts/cluster-down.ps1                   # the cluster name is shared (sre-ai): delete the current one first
./scripts/cluster-up.ps1
./scripts/build-images.ps1
./scripts/deploy-infra.ps1
./scripts/deploy-apps.ps1
# ... exercises ...
cd ../sre-ai; git worktree remove ../sre-ai-phase-6 --force
./scripts/reset-exercise.ps1 -Level Cluster -Yes   # rebuild the current project from scratch
```

**Preconditions and rollback.** You need a green `./scripts/smoke-test.ps1`. Exercise 3 changes the Traefik values
(the platform), 5–6 create a `Server`/`AuthorizationPolicy` (the script deletes them — there are none in the baseline). Rollback:

```powershell
git restore infra/
./scripts/reset-exercise.ps1 -Level Platform
```

| # | Do | What to expect |
|---|---|---|
| 1 | Open Linkerd Viz (the port-forward above): Namespaces → sandbox → orders-api | Success rate, RPS, latencies per edge, the mTLS locks |
| 2 | `kubectl get pod <orders-api-pod> -n sandbox -o yaml`: find `linkerd-proxy` in initContainers, `restartPolicy: Always`, the identity token volume | What the injected proxy looks like |
| 3 | Enable the mesh for Traefik (`podAnnotations: linkerd.io/inject: ingress` in the Traefik values + egress/ingress 4143) | `frontend <- traefik.traefik.serviceaccount… tls=true`: encryption along the whole path |
| 4 | Remove `- port: 4143` from the postgres policy (`helm template` + `kubectl apply` on a copy) | `Database unavailable` again — the trap of item 4 |
| 5 | Create a `Server` (the `http` port of orders-api) and an `AuthorizationPolicy` with `MeshTLSAuthentication` only for `frontend` | worker gets a 403 from the proxy — authorization by identity, which a label cannot forge |
| 6 | An impostor pod with the worker label (as in item 1) against orders-api after exercise 5 | The connection exists (NetworkPolicy let it through), the request does not (the wrong identity) |
| 7 | Disable retries (`getRetries.enabled: false`) and repeat the database restart | The numbers from item 7 |
| 8 | `kubectl get certificate -n linkerd`, `kubectl get secret linkerd-identity-issuer -n linkerd -o yaml` | A 48 h lifetime; cert-manager renews it — Linkerd picks it up without a restart |
| 9 | Compare with Istio ambient mode (no sidecar, ztunnel on the node + a waypoint for L7) — read about it, don't install it | How the model "a proxy in every pod" differs from "a proxy per node" |

## Cheat sheet

```promql
sum by (deployment, client_id, tls) (rate(request_total{direction="inbound", namespace="sandbox"}[2m]))   # who calls whom, and with mTLS or not
sum by (dst_deployment) (rate(request_total{direction="outbound", deployment="frontend"}[2m]))          # where frontend goes
sum by (deployment, route_name) (increase(outbound_http_route_retry_requests_total[5m]))               # retries
```

| Symptom | Where to look | Common cause |
|---|---|---|
| Everything stopped resolving after default-deny | `nslookup` from a pod | No DNS allowance (UDP+TCP 53 to kube-dns) |
| A service cannot reach its neighbour, but the port is open in the policy | the client's `linkerd-proxy` logs: `Failed to connect` | Port 4143 is not open for proxy-to-proxy traffic |
| The pod is not created: `violates PodSecurity "restricted"` | the ReplicaSet events | Injection without linkerd-cni (an init container with `NET_ADMIN`) |
| The control plane is in `CreateContainerConfigError` | `kubectl describe pod -n linkerd` | No ConfigMap `linkerd-identity-trust-roots` with `externalCA` |
| Requests to a Service suddenly get 404 | `kubectl get httproute -n sandbox` | An HTTPRoute exists, but there is no route for this request |
| `tls="no_identity"` in the metrics | the client_id labels | The client is outside the mesh (Traefik, Prometheus, kubelet) |
