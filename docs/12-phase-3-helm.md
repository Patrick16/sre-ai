# 12. Phase 3: Helm in practice

The theory — the section "Helm" in [03-kubernetes-concepts.md](03-kubernetes-concepts.md).

## Which problems of Phases 1–2 Helm solves

| Before (raw manifests) | Now (a chart) |
|---|---|
| Three almost identical YAML files of ~150 lines each: securityContext, probes, volumes copied by hand | One template [`sandbox-service`](../infra/helm/sandbox-service/), the differences are in 20–40 lines of values per service |
| You change an env value in a ConfigMap — the pods don't notice, a manual `rollout restart` is needed | A checksum of the ConfigMap in the pod template → a config change triggers a rolling update by itself |
| It is unknown what is deployed now and what was before | `helm history`: revisions, chart versions, status; `helm rollback` in one command |
| A typo in a field is silently ignored | `values.schema.json` rejects unknown fields and the `latest` tag before anything reaches the cluster |
| No check "does it work after deployment" | `helm test` runs a pod that calls the service by its DNS name |

## Structure

```
infra/helm/
├── sandbox-service/            ← OUR chart: one for all services
│   ├── Chart.yaml              version (of the chart) and appVersion (the default image tag)
│   ├── values.yaml             default values + comments "why"
│   ├── values.schema.json      validation of the values
│   └── templates/
│       ├── _helpers.tpl        names, labels, selectorLabels, rendering of probes
│       ├── deployment.yaml     securityContext, probes, volumes, preStop, checksum
│       ├── service.yaml        if service.enabled
│       ├── configmap-env.yaml  configEnv → envFrom
│       ├── configmap-files.yaml configFiles → files
│       ├── ingress.yaml        Ingress (+ Traefik Middleware, if stripPrefix)
│       └── tests/test-health.yaml  hook for `helm test`
│       (added later: serviceaccount, pvc, configmap-faults — Phase 9; networkpolicy,
│        httproutes — Phase 6; podmonitor, prometheusrule-slo — Phase 5)
├── releases/sandbox/           ← the values of EACH release: frontend.yaml, orders-api.yaml, worker.yaml
├── releases/sre-agent/         ← the values of the agent's release (the same chart)
└── values/                     ← values for the community charts: traefik.yaml, cert-manager.yaml
```

**Layers of values** (each next one overrides the previous):
`sandbox-service/values.yaml` → `releases/sandbox/<service>.yaml` (`-f`) → `--set` on the command line.

To see what would be produced, **without touching the cluster**:
```powershell
helm template orders-api infra/helm/sandbox-service -n sandbox -f infra/helm/releases/sandbox/orders-api.yaml
```

### One chart — three releases (not an umbrella chart)
We could have made one "umbrella" chart `sandbox` with three dependencies — then `helm rollback sandbox`
would roll back everything at once. We chose **a separate release per service**: this is how real microservices are built —
each is rolled out and rolled back independently, each has its own history. The price: there is no atomic deployment of the whole system.

### Platform and applications
| Layer | What | Script |
|---|---|---|
| Platform | Namespaces with Pod Security, Traefik, cert-manager, the CA issuers ([manifests/platform/](../infra/manifests/platform/)) | [deploy-infra.ps1](../scripts/deploy-infra.ps1) |
| Applications | Secrets (generated), the Helm releases | [deploy-apps.ps1](../scripts/deploy-apps.ps1) |

The namespace is not created by the application chart on purpose: `helm --create-namespace` cannot set labels
(Pod Security), and a namespace outlives any single release. In companies this is the zone of the "platform team".

## Key techniques in the templates

- **The selector — only `app.kubernetes.io/name`.** Standard charts add `app.kubernetes.io/instance` to the selector too,
  but a Deployment's selector is immutable: when migrating existing objects this would mean deleting and recreating them. The `version` and `helm.sh/chart` labels — only in `labels`, not in the selector.
- **`checksum/config-env`** — a sha256 of the rendered ConfigMap in an annotation of the pod template. The config changed →
  the pod template changed → a new ReplicaSet. There is no checksum for `configFiles`: orders-api re-reads the file itself.
- **`lifecycle.preStop.sleep`** (`shutdownDelaySeconds: 5`) — see "What we found", item 3.
- **`required` and `fail`** — the template fails with a clear error (`ingress.host is required`) instead of rendering garbage.
- **The Middleware only if `stripPrefix`** — Traefik-specific code is isolated in one `if`.
- **The hook `helm.sh/hook: test`** — the pod is created only by the command `helm test`, not during deployment.

## How we migrated live resources (and what broke along the way)

The services were already running from `kubectl apply`. The goal — hand them over to Helm **without downtime**. Here is what happened in practice:

**Attempt 1 — simply `helm upgrade --install`:**
```
Service "orders-api" ... exists and cannot be imported into the current release: invalid ownership metadata;
missing key "app.kubernetes.io/managed-by": must be set to "Helm" ... "meta.helm.sh/release-name" ...
```
Helm does not take over resources without its own labels — protection so that two releases do not overwrite each other.

**Attempt 2 — `--take-ownership`** (a new flag of Helm 4):
```
Apply failed with 2 conflicts: conflicts with "kubectl-client-side-apply":
- ...volumeMounts[mountPath="/app/config"].name
```
Helm 4 uses **server-side apply (SSA)** by default: the API server remembers *which manager owns which
field* (`metadata.managedFields`). The fields whose value changes belong to `kubectl` — a conflict.

**Attempt 3 — `--take-ownership --force-conflicts`:** success, no outage (14 of 15 requests — 200, see item 3 below).

**A time bomb.** The very next `helm upgrade` with a new image tag failed again with a conflict —
on the `image` and `version` fields. `--force-conflicts` took only the fields that **then** differed; the fields with
identical values stayed in **shared** ownership of `helm` and `kubectl-client-side-apply`. The right
cure is not `--force-conflicts` in every deployment, but removing the old manager:

```powershell
kubectl get deploy orders-api -n sandbox -o json --show-managed-fields   # managedFields are hidden by default
# remove from managedFields the entries with manager=kubectl-client-side-apply (JSON patch replace /metadata/managedFields)
# and the annotation kubectl.kubernetes.io/last-applied-configuration (used by the client-side kubectl apply)
```
After that each field has one owner — `helm` (and `kube-controller-manager` for the status), and all subsequent
`upgrade`/`rollback` runs went through without flags.

**Orphans.** Renamed objects (`orders-api-config` → `orders-api-files`, the Middleware `strip-api` →
`orders-api-strip-prefix`) are not deleted by Helm — it does not know about them. We had to delete them by hand.

On a fresh cluster none of this happens: Helm creates the resources right away. Verified by recreating the cluster from scratch —
**2 min 41 s** from `cluster-up` to a working HTTPS, and all objects have only `helm` and the controllers as managers.

## What we found during verification

### 1. Rollout and rollback — without a single error
Under continuous polling of both routes (every 200 ms): `upgrade` of orders-api to 0.2.0 → `rollback` → a change of the frontend
configuration → back. **128 of 128** responses — 200.
```
rev 5  Rollback to 3
rev 6  Upgrade complete   (0.2.0)
rev 7  Rollback to 5      (back to 0.1.0)
```
Note: a rollback is a **new revision** with the contents of an old one; the history is not rewritten.

### 2. The checksum works, but "rollout finished" ≠ "all traffic is on the new version"
Right after `helm upgrade --wait` the Deployment was fully updated (`updated=2 ready=2`), but the first request
returned the **old** greeting. The old pods still live their `preStop` for 5 seconds and may receive a request.
A couple of seconds later — only the new one. A similar effect from the other side: on a fresh cluster, right after deployment
`GET /` returned **503** — the pods were already Ready, but Traefik had not yet received the updated EndpointSlices.
**Kubernetes and the proxies see the world with a delay** — this is normal, but it must be taken into account in post-deployment checks.

### 3. preStop and lost requests during a rolling update
During the migration of orders-api one request out of 15 failed (`000`), although `maxUnavailable: 0`. The cause — a race when
a pod stops: the kubelet sends SIGTERM, Kestrel stops accepting connections, but Traefik has not yet learned that the pod
is leaving and sends it a request. The cure — `preStop: sleep 5`: the pod keeps working for 5 more seconds while the endpoint
changes spread through the cluster, and only then receives SIGTERM.

An interesting detail: **the rollout that added preStop still lost requests** (2 of 28) — because it
stopped the *old* pods, created without preStop. The protection works only for pods created with it already. The next
`rollout restart` — **47 of 47**.

## Helm 4 differences we ran into

| Helm 3 | Helm 4 |
|---|---|
| `--atomic` | `--rollback-on-failure` |
| `--force` | `--force-replace` |
| client-side 3-way merge | server-side apply by default (`--server-side auto`) |
| — | `--take-ownership` to take over existing resources |
| `--wait` until ready | `--wait` = the `watcher` strategy (kstatus) |

## The danger of `--set`

`helm upgrade ... --set image.tag=0.2.0` works, but the value **lives only in the cluster** (`helm get values`),
not in git. The next deployment from the values file will silently bring back the old tag. In production, versions are changed
by a commit to the values file; the next step is GitOps (Argo CD, Phase 10), where the cluster syncs itself with git.

## Exercises

**Project snapshot at this phase** — the tag `phase-3` (commit `50ed554`). The orders there are still in the pods' memory
(PostgreSQL appeared in Phase 4). It was not re-run to verify — it is the state the exercises were composed for.

```powershell
git worktree add ../sre-ai-phase-3 phase-3   # a separate folder; your working copy stays unchanged
cd ../sre-ai-phase-3
./scripts/cluster-down.ps1                   # the cluster name is shared (sre-ai): delete the current one first
./scripts/cluster-up.ps1
./scripts/build-images.ps1
./scripts/deploy-infra.ps1
./scripts/deploy-apps.ps1
# ... exercises ...
cd ../sre-ai; git worktree remove ../sre-ai-phase-3 --force
./scripts/reset-exercise.ps1 -Level Cluster -Yes   # rebuild the current project from scratch
```

**Preconditions and rollback.** You need a green `./scripts/smoke-test.ps1`. Exercise 1 needs the git commit `8a47ee6`
(it is in the history). Exercises 3–9 edit the values files and create drift. Rollback (including the field-owner conflict
after `kubectl scale` from item 8 — the script takes the fields back with `--force-conflicts`):

```powershell
git restore infra/helm/releases                  # if you edited the values files
./scripts/reset-exercise.ps1
```

| # | Do | What to expect |
|---|---|---|
| 1 | `helm template` for each service and compare with the deleted manifests: `git show 8a47ee6:infra/manifests/phase-1/orders-api.yaml` | The same objects, plus the Helm labels, the checksum, preStop |
| 2 | `helm get manifest orders-api -n sandbox`, `helm get values orders-api -n sandbox` | What Helm stores about the release (a `sh.helm.release.v1.*` secret in the namespace) |
| 3 | `./scripts/build-images.ps1 -Services orders-api -Tag 0.2.0`, change `tag` in `orders-api.yaml`, `./scripts/deploy-apps.ps1 -Services orders-api`; then `helm rollback orders-api -n sandbox` | `/api/info` → `0.2.0`, after the rollback `0.1.0`; `helm history` |
| 4 | Change `Frontend__Greeting` in `frontend.yaml` and deploy | The frontend pods are recreated by themselves (checksum) |
| 5 | Set `image.tag: "9.9.9"` and deploy via `deploy-apps.ps1` | `--rollback-on-failure`: after the timeout Helm rolls the release back by itself; `helm history` shows failed + rollback |
| 6 | Write `replcas: 3` or `image.tag: latest` in the values | The schema rejects it before anything reaches the cluster |
| 7 | `helm uninstall worker -n sandbox`, then `./scripts/deploy-apps.ps1 -Services worker` | All objects of the release are deleted and created again |
| 8 | Look at the field owners: `kubectl get deploy orders-api -n sandbox -o yaml --show-managed-fields`; then `kubectl scale deploy/orders-api -n sandbox --replicas=5` and `helm upgrade` again | `kubectl scale` becomes the owner of `spec.replicas` → an SSA conflict at the next upgrade. Manual edits in a cluster managed by Helm are drift |
| 9 | `shutdownDelaySeconds: 0`, deploy, then `kubectl rollout restart` while polling with curl in a loop | Lost requests appear again |

## Cheat sheet

```powershell
helm lint infra/helm/sandbox-service -f infra/helm/releases/sandbox/orders-api.yaml
helm template orders-api infra/helm/sandbox-service -n sandbox -f infra/helm/releases/sandbox/orders-api.yaml
# diff against the live cluster (kubectl diff needs the diff utility — it is in Git for Windows):
$env:Path += ';C:\Program Files\Git\usr\bin'
helm template orders-api infra/helm/sandbox-service -n sandbox -f infra/helm/releases/sandbox/orders-api.yaml --skip-tests | kubectl diff -n sandbox -f -
helm list -n sandbox
helm history orders-api -n sandbox
helm rollback orders-api <revision> -n sandbox --wait
helm test orders-api -n sandbox
```

| Symptom | Cause |
|---|---|
| `invalid ownership metadata` | The object was created not by this release (kubectl or another release) |
| `Apply failed with N conflicts: conflicts with "<manager>"` | SSA: another manager owns the field (kubectl, `kubectl scale`, another controller) |
| `values don't meet the specifications of the schema` | An error in the values — read the path in the message |
| `field is immutable` (selector) | The selectorLabels changed — the Deployment has to be recreated |
| The release is `failed`, but the pods work | `--wait` timed out; check `kubectl get events`, `helm history` |
| An object is "stuck" after a rename in the chart | An orphan: Helm deletes only what was in its previous revision |
