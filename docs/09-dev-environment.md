# 09. Development environment (Phase 0)

## Tools and why each one

| Tool | Version we use | What it is | Why in the project |
|---|---|---|---|
| **Docker Desktop** | 29.8 | Runs containers (on Windows — inside WSL2) | Builds the service images; the kind "nodes" also run on it |
| **.NET SDK** | 10.0.401 (pinned in `global.json`) | Compiler, `dotnet build/test/run` | The agent and the sandbox services |
| **kind** | 0.33 | Kubernetes in Docker: each cluster node is a Docker container | A local multi-node cluster in a minute |
| **kubectl** | 1.33 (see below) | CLI for kube-apiserver | The main way to look at and change the cluster |
| **Helm** | 4.3 | Kubernetes package manager | Installing the infrastructure and our charts (from Phases 2–3) |
| **k9s** | 0.51 | A TUI for the cluster in the terminal | Quickly look at pods, logs, events — more convenient than dozens of kubectl commands |

Installation (already done; on a new machine):

```powershell
winget install --id Kubernetes.kind -e
winget install --id Helm.Helm -e
winget install --id Derailed.k9s -e
```

### Caveat: kubectl version skew

Kubernetes officially supports kubectl within **±1 minor version** of the server.
Right now the client is 1.33 (installed together with Docker Desktop), and the cluster is 1.37. Basic commands
work, but new fields and flags may behave unexpectedly. To update:

```powershell
winget install --id Kubernetes.kubectl -e
```

After installing, check which kubectl comes first in `PATH` (`Get-Command kubectl`) —
the Docker Desktop version lives in `C:\WINDOWS\system32` and may shadow the new one.

## Container, image, registry — in 30 seconds

- **Image** — an immutable snapshot of a filesystem with an application and everything it needs.
- **Container** — a running process started from an image, isolated from the rest of the system.
- **Registry** — storage for images (Docker Hub, GHCR). Kubernetes downloads images from a registry.
- kind has a simplification: an image can be loaded straight into the nodes with `kind load docker-image`,
  without a registry. In production nobody does this — there is always a registry.

## How kind differs from a "real" cluster

| | kind | Production (EKS/AKS/GKE or bare metal) |
|---|---|---|
| Nodes | Docker containers on one machine | Separate VMs/servers |
| Node failure | `docker stop sre-ai-worker` | A real hardware/network failure |
| LoadBalancer | None out of the box (needs MetalLB/cloud-provider-kind) | A cloud load balancer |
| Storage | `local-path` — a folder on the node's disk | Network disks, replication |
| Control plane | One node | Usually 3 for fault tolerance |

Everything we study at the level of Kubernetes objects (Deployments, Services, Ingress, RBAC…) works the same way.
Only the "lower" layer differs: networking, disks, load balancers.

## What is inside the cluster now

After `scripts/cluster-up.ps1` (`kubectl get pods -A`):

| Pod | Component from [03](03-kubernetes-concepts.md) | Role |
|---|---|---|
| `kube-apiserver-*` | API server | The single entry point: kubectl, controllers, our agent |
| `etcd-*` | etcd | The database of the desired state |
| `kube-scheduler-*` | Scheduler | Chooses a node for each new pod |
| `kube-controller-manager-*` | Controller manager | Reconcile loops: Deployment → ReplicaSet → Pods, etc. |
| `coredns-*` (×2) | Cluster DNS | Resolves `orders-api.sandbox.svc.cluster.local` |
| `kube-proxy-*` (one per node) | kube-proxy | Network rules that implement Services |
| `kindnet-*` (one per node) | CNI | Networking between pods (supports NetworkPolicy) |
| `local-path-provisioner-*` | Provisioner for the StorageClass `standard` | Creates PVs for PVCs (Phase 4) |

Note: `kube-proxy` and `kindnet` are **DaemonSets** (one pod on each node), while
the control-plane components are **static pods** (the kubelet starts them from files on the node, not the API server).

### Cluster configuration

[`infra/cluster/kind-config.yaml`](../infra/cluster/kind-config.yaml):
- 1 control plane + 2 workers — so you can see how pods are distributed across nodes and what "a node failing" means;
- on the control plane: the label `ingress-ready=true` and forwarding of ports 80/443 from the host — needed in Phase 2 for Ingress
  (only on `127.0.0.1`, so the sandbox is not visible from the local network);
- `containerdConfigPatches` — enables the registry-mirror directory in the nodes' containerd (see below).

### Image cache (pull-through registry)

**The problem.** Each kind node is a separate container with its own containerd and its own image storage. The nodes do not
see the images of Docker Desktop. Our images are put into the nodes by `build-images.ps1` itself (`kind load`), but community images
(Prometheus, Grafana, Loki, Alloy, postgres…) every node downloads from the internet on its own — and again after every
cluster recreation. You cannot connect the Docker Desktop storage via a volume: two containerd daemons on one
storage corrupt its metadata, and the Windows folder does not support overlayfs.

**The solution** — [scripts/registry-cache.ps1](../scripts/registry-cache.ps1) (called by `cluster-up.ps1`):

```
kind node ──containerd──► /etc/containerd/certs.d/quay.io/hosts.toml: "ask the cache first"
                              │
                              ▼
        kind-cache-quay-io (registry:2.8.3, proxy mode) ──► quay.io   (only on the first request)
                              │
                         volume kind-cache-quay-io  ← survives cluster-down
```

- Four caches — one per registry: `docker.io`, `quay.io`, `registry.k8s.io`, `ghcr.io` (for Chaos Mesh); one registry instance in
  proxy mode can only have one upstream. All are on the Docker network `kind`, and the nodes reach them by container name.
- If a cache is unavailable, containerd silently goes to the internet directly (`server = ...` in `hosts.toml`) — the cache speeds things up,
  but is not a point of failure.
- Company mirrors work the same way (Harbor, Artifactory, ECR pull-through cache): faster, you don't hit
  Docker Hub limits, and the cluster can start even if the external registry is down.

Measured: the `grafana/alloy` image the first time — 337 s (internet speed); from another node already from the cache — **8.6 s**.

**Addresses that depend on your Docker.** The agent's NetworkPolicy hard-codes the kind network (`172.18.0.0/16`) and
`host.docker.internal` (`192.168.65.254`). On another Docker they differ, and then the agent silently loses the cluster API or the LLM.
`./scripts/check-network-assumptions.ps1` reads the real values (the `kind` network, `host.docker.internal` from inside a node) and
reports whether they match the values.

**Why `registry:2`, not `registry:3`.** With `registry:3` (distribution v3), large layers failed at exactly 30 s with
`500 … context canceled`: in proxy mode v3 hands a layer to the client only after it has downloaded it entirely, and
containerd does not wait. Small images passed, and containerd silently went to the internet — so the cache "seemed to work"
but accelerated nothing. v2 hands over a layer as it downloads. The lesson: verify not "configured" but
"actually lands in the cache" (`/v2/_catalog`, download time).

```powershell
./scripts/registry-cache.ps1                  # bring up the caches and configure the nodes (idempotent)
docker exec kind-cache-docker-io wget -qO- http://localhost:5000/v2/_catalog   # what is cached
./scripts/registry-cache.ps1 -Remove          # remove the containers (data stays in volumes)
./scripts/registry-cache.ps1 -Remove -Purge   # remove the data too
```

## .NET solution

The file `SreAi.slnx` in the root (the new XML solution format of .NET 10, instead of `.sln`):

```
src/SreAgent.Core          classlib   ← everything depends on it
src/SreAgent.Tools         classlib   → Core
src/SreAgent.LlmClients    classlib   → Core
src/SreAgent.Host          web        → Tools, LlmClients
tests/SreAgent.Core.Tests  xunit      → Core
tests/SreAgent.Host.Tests  xunit      → Host
src/Sandbox.Services/*     sandbox services (frontend, orders-api, worker) + shared Shared/Faults.cs
src/Chaos.Monkey           CLI: kills a random pod in sandbox (dry run by default)
```

The agent code appeared in Phase 7, the sandbox services in Phase 1, `Chaos.Monkey` in Phase 9.

## Exercises (by hand)

**Project snapshot at this phase** — the tag `phase-0` (commit `fae3a2c`). The exercises were written against it; use it when
something no longer reproduces on the current project. It was not re-run to verify — it is the state the exercises were composed for.
A separate folder (your working copy stays unchanged):

```powershell
git worktree add ../sre-ai-phase-0 phase-0
cd ../sre-ai-phase-0
./scripts/cluster-down.ps1                   # the cluster name is shared (sre-ai): delete the current one first
./scripts/cluster-up.ps1
# ... exercises ...
cd ../sre-ai; git worktree remove ../sre-ai-phase-0 --force
./scripts/reset-exercise.ps1 -Level Cluster -Yes   # rebuild the current project from scratch
```

**Preconditions and rollback.** The exercises of this phase can be done in any order and before the other phases; you only need a
working cluster. If you broke something or want to start from a clean state (exercise 6 does the same):

```powershell
./scripts/reset-exercise.ps1               # bring the apps back (after docker stop of a node etc.), data is kept
./scripts/reset-exercise.ps1 -Level Cluster -Yes   # from scratch, ~9 min, data is lost
```

1. `kubectl get nodes -o wide` — find the internal IPs of the nodes. Then `docker ps` — make sure the "nodes" are containers.
2. `kubectl get pods -n kube-system -o wide` — on which node each component lives, and why the control plane is on only one node.
3. Open `k9s`, press `:pods`, select `coredns`, press `l` (logs), `d` (describe). Exit — `:q`.
4. `kubectl api-resources | Measure-Object` — how many object types the cluster knows "out of the box" (compare later, after installing cert-manager/Prometheus — they add CRDs).
5. `docker stop sre-ai-worker2`, wait a minute, `kubectl get nodes` — the node is `NotReady`. `docker start sre-ai-worker2` — it came back. This is the first "chaos experiment".
6. `scripts/cluster-down.ps1` and then `scripts/cluster-up.ps1` again — the cluster is recreated from scratch in about a minute. Don't be afraid to break things.
